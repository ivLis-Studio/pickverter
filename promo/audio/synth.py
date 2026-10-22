"""Score + sound design for the Pickverter promo, synthesised from scratch (no samples).

    .venv/bin/python audio/synth.py out/events-en.json out/audio-en.wav

Music: 120 BPM, F major (Fmaj9 - C/E - Dm9 - Bbmaj9), plucked arpeggio, pad, bass and a light kit.
SFX: every UI event recorded by the stage (clicks, key taps, hover ticks, whooshes, the finale hit).
"""
import json
import sys

import numpy as np
from scipy import signal

SR = 48000
BPM = 120
BEAT = 60 / BPM
rng = np.random.default_rng(7)


# ----------------------------------------------------------------------------- basics
def n_of(sec):
    return int(round(sec * SR))


def tt(sec):
    return np.arange(n_of(sec)) / SR


def midi(m):
    return 440.0 * 2 ** ((m - 69) / 12)


def noise(sec):
    return rng.standard_normal(n_of(sec))


def expdec(sec, tau):
    return np.exp(-tt(sec) / tau)


def fade(x, a=0.002, r=0.01):
    x = x.copy()
    na, nr = max(1, n_of(a)), max(1, n_of(r))
    x[:na] *= np.linspace(0, 1, na)
    x[-nr:] *= np.linspace(1, 0, nr)
    return x


def butter(x, kind, f, order=2):
    sos = signal.butter(order, f, kind, fs=SR, output="sos")
    return signal.sosfilt(sos, x)


def sweep(x, f0, f1, q=1.2, kind="bandpass", curve="exp", block=64):
    """Time-varying RBJ biquad (cutoff glides from f0 to f1 across x)."""
    out = np.zeros_like(x)
    zi = np.zeros(2)
    blocks = int(np.ceil(len(x) / block))
    for b in range(blocks):
        u = b / max(1, blocks - 1)
        f = f0 * (f1 / f0) ** u if curve == "exp" else f0 + (f1 - f0) * u
        f = min(f, SR * 0.45)
        w = 2 * np.pi * f / SR
        alpha = np.sin(w) / (2 * q)
        cw = np.cos(w)
        if kind == "bandpass":
            bb = [alpha, 0, -alpha]
        elif kind == "lowpass":
            bb = [(1 - cw) / 2, 1 - cw, (1 - cw) / 2]
        else:
            bb = [(1 + cw) / 2, -(1 + cw), (1 + cw) / 2]
        aa = [1 + alpha, -2 * cw, 1 - alpha]
        bb = np.array(bb) / aa[0]
        aa = np.array(aa) / aa[0]
        seg = x[b * block:(b + 1) * block]
        y, zi = signal.lfilter(bb, aa, seg, zi=zi)
        out[b * block:(b + 1) * block] = y
    return out


def pan2(x, pan=0.0):
    a = (pan + 1) * np.pi / 4
    return np.vstack([x * np.cos(a), x * np.sin(a)])


class Bus:
    def __init__(self, sec):
        self.x = np.zeros((2, n_of(sec)))

    def add(self, sound, t, gain=1.0, pan=0.0):
        s = sound if sound.ndim == 2 else pan2(sound, pan)
        i = n_of(t)
        if i < 0:
            s = s[:, -i:]
            i = 0
        j = min(self.x.shape[1], i + s.shape[1])
        if j > i:
            self.x[:, i:j] += s[:, : j - i] * gain


def reverb_ir(sec=2.4, predelay=0.018, damp=5200, seed=3):
    r = np.random.default_rng(seed)
    n = n_of(sec)
    t = np.arange(n) / SR
    env = np.exp(-t / (sec / 6.2))
    ir = np.zeros((2, n + n_of(predelay)))
    for c in range(2):
        tail = r.standard_normal(n) * env
        tail = butter(tail, "lowpass", damp, 1)
        ir[c, n_of(predelay):] = tail
    ir /= np.sqrt((ir ** 2).sum(axis=1, keepdims=True))
    return ir


def convolve(bus, ir):
    out = np.zeros((2, bus.shape[1] + ir.shape[1] - 1))
    for c in range(2):
        out[c] = signal.fftconvolve(bus[c], ir[c])
    return out[:, : bus.shape[1]]


def pingpong(x, delay, feedback=0.35, taps=6, damp=4500):
    out = np.zeros_like(x)
    d = n_of(delay)
    for k in range(1, taps + 1):
        g = feedback ** k
        src = x[(k + 1) % 2]
        src = butter(src, "lowpass", damp, 1)
        if k * d < x.shape[1]:
            out[k % 2, k * d:] += src[: x.shape[1] - k * d] * g
    return out


# ----------------------------------------------------------------------------- instruments
def additive(f, sec, harmonics, detune_cents=0.0, phase=0.0):
    t = tt(sec)
    f = f * 2 ** (detune_cents / 1200)
    y = np.zeros_like(t)
    for k, amp in harmonics:
        if f * k < SR * 0.45:
            y += amp * np.sin(2 * np.pi * f * k * t + phase * k)
    return y


def pluck(m, sec=0.9, bright=1.0, vel=1.0):
    f = midi(m)
    t = tt(sec)
    y = np.zeros_like(t)
    for k in range(1, 18):
        if f * k > 15000:
            break
        decay = 0.45 / (1 + 0.55 * (k - 1) / bright)
        y += (1 / k) * np.exp(-t / decay) * np.sin(2 * np.pi * f * k * t + 0.3 * k)
    y *= np.exp(-t / 0.55)
    return fade(y * vel * 0.35, 0.001, 0.05)


def bell(m, sec=2.5, vel=1.0):
    f = midi(m)
    t = tt(sec)
    index = 2.2 * np.exp(-t / 0.25)
    mod = np.sin(2 * np.pi * f * 3.5 * t) * index
    y = np.sin(2 * np.pi * f * t + mod) * np.exp(-t / 0.9)
    y += 0.25 * np.sin(2 * np.pi * f * 2.76 * t) * np.exp(-t / 0.35)
    return fade(y * vel * 0.25, 0.001, 0.2)


def pad(notes, sec, attack=0.5, release=0.8, cutoff=2600, vel=1.0):
    st = np.zeros((2, n_of(sec)))
    for i, m in enumerate(notes):
        f = midi(m)
        harm = [(k, 1 / k) for k in range(1, 40) if f * k < cutoff]
        for side, cents in ((0, -6), (1, 6)):
            st[side] += additive(f, sec, harm, cents, phase=0.7 * i + side)
    env = np.ones(st.shape[1])
    na, nr = n_of(attack), n_of(release)
    env[:na] = np.linspace(0, 1, na) ** 2
    env[-nr:] *= np.linspace(1, 0, nr) ** 1.5
    return st * env * vel * 0.05


def bass(m, sec=0.24, vel=1.0):
    f = midi(m)
    t = tt(sec)
    y = np.sin(2 * np.pi * f * t) + 0.35 * np.sin(2 * np.pi * 2 * f * t) + 0.12 * np.sin(2 * np.pi * 3 * f * t)
    env = np.minimum(1, t / 0.006) * np.exp(-t / 0.18)
    return fade(y * env * vel * 0.32, 0.001, 0.02)


def kick(vel=1.0):
    t = tt(0.45)
    f = 45 + 95 * np.exp(-t / 0.045)
    ph = 2 * np.pi * np.cumsum(f) / SR
    y = np.sin(ph) * np.exp(-t / 0.22)
    click = butter(noise(0.45), "highpass", 2500) * np.exp(-t / 0.004) * 0.25
    return fade((y + click) * vel * 0.55, 0.0005, 0.02)


def clap(vel=1.0):
    sec = 0.3
    y = np.zeros(n_of(sec))
    for k, off in enumerate((0, 0.011, 0.022)):
        burst = noise(0.2) * expdec(0.2, 0.009 if k < 2 else 0.06)
        y[n_of(off):n_of(off) + len(burst)] += burst[: len(y) - n_of(off)]
    y = butter(y, "bandpass", [900, 4200])
    return fade(y * vel * 0.18, 0.0005, 0.03)


def hat(sec=0.05, vel=1.0):
    y = butter(noise(sec + 0.02), "highpass", 7500, 2) * expdec(sec + 0.02, sec / 3)
    return fade(y * vel * 0.12, 0.0005, 0.01)


def snare_hit(vel=1.0):
    t = tt(0.18)
    body = np.sin(2 * np.pi * 190 * t) * np.exp(-t / 0.05) * 0.4
    sn = butter(noise(0.18), "bandpass", [1500, 7000]) * np.exp(-t / 0.06)
    return fade((body + sn) * vel * 0.16, 0.0005, 0.02)


# ----------------------------------------------------------------------------- sfx
def sfx_click(bright=1.0):
    t = tt(0.06)
    y = butter(noise(0.06), "highpass", 2800) * np.exp(-t / 0.0025) * 0.7
    y += np.sin(2 * np.pi * 2300 * bright * t) * np.exp(-t / 0.008) * 0.35
    y += np.sin(2 * np.pi * 160 * t) * np.exp(-t / 0.012) * 0.4
    return fade(y * 0.42, 0.0003, 0.01)


def sfx_key(big=False):
    t = tt(0.12)
    body = np.sin(2 * np.pi * (150 + 60 * np.exp(-t / 0.01)) * t) * np.exp(-t / 0.03)
    top = butter(noise(0.12), "bandpass", [1400, 5200]) * np.exp(-t / 0.006)
    tick = butter(noise(0.12), "highpass", 5000) * np.exp(-t / 0.0015)
    y = body * 0.55 + top * 0.6 + tick * 0.35
    return fade(y * (0.62 if big else 0.42), 0.0003, 0.02)


def sfx_tick(pitch=0.0):
    f = 2050 * 2 ** (pitch * 1.0)
    t = tt(0.05)
    y = np.sin(2 * np.pi * f * t) * np.exp(-t / 0.009) + 0.3 * np.sin(2 * np.pi * 2 * f * t) * np.exp(-t / 0.004)
    return fade(y * 0.16, 0.0003, 0.01)


def sfx_whoosh(sec=0.42, f0=350, f1=3200, gain=0.35, q=0.9):
    x = noise(sec)
    y = sweep(x, f0, f1, q=q)
    env = np.sin(np.pi * np.linspace(0, 1, len(y))) ** 1.6
    return fade(y * env * gain, 0.002, 0.02)


def sfx_open():
    w = sfx_whoosh(0.2, 500, 2400, 0.22, 1.1)
    t = tt(0.2)
    glide = np.sin(2 * np.pi * np.cumsum(420 + 380 * (t / 0.2)) / SR) * np.exp(-t / 0.06) * 0.08
    spark = np.sin(2 * np.pi * 3100 * t) * np.exp(-t / 0.02) * 0.05
    return w + glide + spark


def sfx_close():
    return sfx_whoosh(0.14, 2200, 700, 0.12, 1.2)


def sfx_pop(f0=520, f1=1050, sec=0.08, gain=0.22):
    t = tt(sec)
    f = f0 + (f1 - f0) * (1 - np.exp(-t / (sec / 3)))
    y = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / (sec / 3.5))
    return fade(y * gain, 0.0005, 0.01)


def sfx_done():
    a = bell(88, 1.2, 0.9)
    b = bell(95, 1.2, 0.8)
    out = np.zeros(n_of(1.4))
    out[: len(a)] += a
    out[n_of(0.075): n_of(0.075) + len(b)] += b
    return out * 0.9


def sfx_lift():
    t = tt(0.55)
    glide = np.sin(2 * np.pi * np.cumsum(380 * 2 ** (1.6 * t / 0.55)) / SR) * np.sin(np.pi * t / 0.55) * 0.07
    shimmer = butter(noise(0.55), "highpass", 6000) * np.sin(np.pi * t / 0.55) ** 2 * 0.05
    return sfx_whoosh(0.55, 400, 4000, 0.18) + glide + shimmer


def sfx_riser(sec):
    t = tt(sec)
    x = sweep(noise(sec), 300, 7000, q=2.2)
    env = (t / sec) ** 2.2
    tone = np.zeros_like(t)
    for k, base in enumerate((110, 165, 220)):
        f = base * 2 ** (2.0 * (t / sec) ** 1.6)
        tone += np.sin(2 * np.pi * np.cumsum(f) / SR + k)
    return fade((x * 0.5 + tone * 0.05) * env * 0.4, 0.01, 0.01)


def sfx_whoosh_big():
    x = sweep(noise(0.6), 180, 9000, q=0.8)
    env = np.sin(np.pi * np.linspace(0, 1, len(x))) ** 1.2
    t = tt(0.6)
    drop = np.sin(2 * np.pi * np.cumsum(140 * np.exp(-t / 0.25) + 35) / SR) * env * 0.25
    st = np.vstack([x * env * np.linspace(1.0, 0.25, len(x)), x * env * np.linspace(0.25, 1.0, len(x))]) * 0.5
    return st + pan2(drop, 0)


def sfx_impact():
    t = tt(2.2)
    boom = np.sin(2 * np.pi * np.cumsum(38 + 40 * np.exp(-t / 0.08)) / SR) * np.exp(-t / 0.7) * 0.75
    crack = butter(noise(2.2), "lowpass", 3000) * np.exp(-t / 0.05) * 0.35
    return fade(boom + crack, 0.0005, 0.2)


def sfx_shing():
    t = tt(0.7)
    swish = sweep(noise(0.7), 9000, 2500, q=1.4, kind="bandpass") * np.exp(-t / 0.09) * 0.45
    ring = (np.sin(2 * np.pi * 5200 * t) + 0.6 * np.sin(2 * np.pi * 7850 * t) + 0.4 * np.sin(2 * np.pi * 3400 * t)) * np.exp(-t / 0.22) * 0.05
    return fade(swish + ring, 0.0008, 0.05)


def sfx_chime():
    out = np.zeros(n_of(2.6))
    for k, (m, d) in enumerate(((84, 0.0), (91, 0.06), (96, 0.12))):
        b = bell(m, 2.4, 0.55 - 0.1 * k)
        i = n_of(d)
        out[i:i + len(b)] += b[: len(out) - i]
    return out


# ----------------------------------------------------------------------------- score
def build(events, duration):
    music = Bus(duration + 3)
    verb_send = Bus(duration + 3)
    delay_send = Bus(duration + 3)
    sfx = Bus(duration + 3)
    sfx_verb = Bus(duration + 3)

    chords = [
        {"bass": 41, "pad": [53, 57, 60, 64, 67], "arp": [65, 72, 69, 76, 72, 79, 76, 72]},
        {"bass": 40, "pad": [52, 55, 60, 62, 67], "arp": [64, 72, 67, 74, 72, 79, 74, 72]},
        {"bass": 38, "pad": [53, 57, 60, 64, 65], "arp": [62, 69, 65, 72, 69, 76, 72, 69]},
        {"bass": 34, "pad": [50, 53, 57, 60, 65], "arp": [62, 70, 65, 74, 70, 77, 74, 70]},
    ]
    bar = 4 * BEAT
    end_music = 29.84

    def chord_at(t):
        return chords[int(t // bar) % 4]

    def section(t):
        if t < 2.0:
            return "intro"
        if t < 4.0:
            return "build"
        if t < 19.5:
            return "groove"
        if t < 20.0:
            return "fill"
        if t < 24.0:
            return "montage"
        if t < 26.0:
            return "lang"
        if t < 28.0:
            return "values"
        return "finale"

    # pads (one per bar, ducked later)
    padbus = Bus(duration + 3)
    for b in range(int(np.ceil(end_music / bar))):
        t0 = b * bar
        sec = min(bar + 0.6, end_music - t0 + 0.4)
        if sec <= 0.1:
            continue
        c = chord_at(t0 + 0.01)
        cutoff = 1500 if t0 < 2 else 2400
        if t0 >= 28:
            cutoff = 2400 + 3000 * (t0 - 28) / 2
        p = pad(c["pad"], sec, attack=0.35 if t0 else 1.2, release=0.5, cutoff=cutoff, vel=0.9)
        padbus.add(p, t0)

    # arpeggio
    step = BEAT / 2
    k = 0
    t = 0.0
    while t < end_music - 0.05:
        sec_name = section(t)
        c = chord_at(t)
        idx = k % 8
        m = c["arp"][idx]
        vel = 0.9 if idx % 2 == 0 else 0.62
        bright = 1.0
        if sec_name == "intro":
            vel *= 0.55 + 0.45 * t / 2
            bright = 0.6
        if sec_name == "lang":
            m += 12
            vel *= 0.55
        if sec_name == "finale":
            vel *= 0.8 + 0.4 * (t - 28) / 1.84
            bright = 1.3
        s = pluck(m, 0.8, bright, vel)
        music.add(s, t, 0.55, pan=-0.28 if idx % 2 else 0.28)
        delay_send.add(s, t, 0.32)
        verb_send.add(s, t, 0.22)
        if sec_name == "lang":  # extra 16ths
            s2 = pluck(c["arp"][(idx + 3) % 8] + 12, 0.5, 1.0, vel * 0.7)
            music.add(s2, t + step / 2, 0.45, pan=0.4 if idx % 2 else -0.4)
            delay_send.add(s2, t + step / 2, 0.25)
        k += 1
        t = k * step

    # bass + drums
    bassbus = Bus(duration + 3)
    kicks = []
    for b8 in range(int(end_music / (BEAT / 2))):
        t = b8 * BEAT / 2
        s = section(t)
        on_beat = b8 % 2 == 0
        beat_i = b8 // 2
        c = chord_at(t)
        if s in ("build", "groove", "montage", "values", "fill") or (s == "finale" and t < 29.0):
            bassbus.add(bass(c["bass"] + (12 if (b8 % 8 == 7) else 0), 0.22, 0.95 if on_beat else 0.7), t)
        if on_beat and s in ("groove", "montage", "values") or (on_beat and s == "finale" and t < 29.0):
            if s == "values" and beat_i % 2 == 1:
                pass
            else:
                music.add(kick(0.95), t, 1.0)
                kicks.append(t)
        if on_beat and beat_i % 2 == 1 and s in ("groove", "montage", "values"):
            cl = clap(0.9)
            music.add(cl, t, 1.0, pan=0.05)
            verb_send.add(cl, t, 0.35)
        if not on_beat and s in ("build", "groove", "montage", "values", "lang", "finale"):
            music.add(hat(0.05, 0.9 if s != "lang" else 0.6), t, 1.0, pan=0.35)
        if s in ("groove", "montage"):
            music.add(hat(0.025, 0.35), t + BEAT / 4, 1.0, pan=-0.4)
    # fill into the montage and the snare roll into the finale
    for i in range(8):
        tt_ = 19.5 + i * BEAT / 8
        music.add(snare_hit(0.4 + 0.08 * i), tt_, 1.0)
    roll_t = 28.9
    gap = 0.125
    while roll_t < 29.8:
        v = 0.25 + 0.75 * (roll_t - 28.9) / 0.9
        sh = snare_hit(v)
        music.add(sh, roll_t, 1.0)
        verb_send.add(sh, roll_t, 0.3)
        roll_t += gap
        gap = max(0.035, gap * 0.9)
    # montage stabs on every word
    for i in range(8):
        t0 = 20.0 + i * 0.5
        c = chord_at(t0)
        for j, m in enumerate(c["pad"][1:]):
            st = pluck(m + 12, 0.35, 1.4, 0.5)
            music.add(st, t0, 0.4, pan=-0.3 + 0.15 * j)
            verb_send.add(st, t0, 0.2)

    # finale chord + bells
    fin = 30.36
    final_pad = pad([41, 53, 57, 60, 64, 67, 72], 4.8, attack=0.02, release=2.6, cutoff=5200, vel=0.8)
    music.add(final_pad, fin, 1.0)
    verb_send.add(final_pad, fin, 0.45)
    music.add(pan2(bass(41, 1.6, 0.75), 0), fin, 1.0)
    for i, (m, d) in enumerate(((84, 0.0), (88, 0.34), (91, 0.72), (96, 1.09), (93, 1.6), (91, 2.2))):
        bl = bell(m, 2.6, 0.75 - 0.06 * i)
        music.add(bl, fin + d, 0.5, pan=(-0.3, 0.3)[i % 2])
        verb_send.add(bl, fin + d, 0.5)
        delay_send.add(bl, fin + d, 0.2)

    # sidechain ducking from the kick
    duck = np.ones(music.x.shape[1])
    for kt in kicks:
        i = n_of(kt)
        n = n_of(0.3)
        seg = 1 - 0.6 * np.exp(-np.arange(n) / SR / 0.09)
        j = min(len(duck), i + n)
        duck[i:j] = np.minimum(duck[i:j], seg[: j - i])
    music.x += padbus.x * duck
    music.x += bassbus.x * duck

    # ------------------------------------------------------------------ sfx
    for e in events:
        name, t = e["name"], e["t"]
        if name == "click":
            s = sfx_click(1.25 if e.get("icon") else 1.0)
            sfx.add(s, t, 1.0 if not e.get("icon") else 1.2)
            if e.get("icon"):
                sfx_verb.add(s, t, 0.5)
        elif name == "key":
            s = sfx_key(e.get("big", False))
            sfx.add(s, t, 1.0)
            sfx_verb.add(s, t, 0.25 if e.get("big") else 0.08)
        elif name == "tick":
            pitch = e.get("pitch", None)
            if e.get("chase"):
                s = sfx_tick(0.15 + 0.6 * pitch)
                sfx.add(s, t, 1.1, pan=np.sin(pitch * 12) * 0.5)
            else:
                sfx.add(sfx_tick(rng.uniform(-0.04, 0.06)), t, 1.0, pan=rng.uniform(-0.2, 0.2))
        elif name == "open":
            sfx.add(sfx_open(), t - 0.01, 1.0)
        elif name == "close":
            sfx.add(sfx_close(), t, 0.9)
        elif name == "swap":
            sfx.add(sfx_tick(-0.2), t, 0.9)
            sfx.add(sfx_tick(0.15), t + 0.045, 0.9)
        elif name == "choice":
            sfx.add(sfx_pop(600, 900, 0.07, 0.16), t, 1.0)
        elif name == "card":
            sfx.add(pan2(sfx_whoosh(0.32, 900, 3400, 0.16), 0.55), t - 0.04, 1.0)
        elif name == "done":
            d = sfx_done()
            sfx.add(d, t, 0.85, pan=0.4)
            sfx_verb.add(d, t, 0.35)
        elif name == "pop":
            sfx.add(sfx_pop(), t, 1.0, pan=0.25)
        elif name == "swish":
            sfx.add(sfx_whoosh(0.3, 900, 4200, 0.22, 1.4), t, 1.0)
        elif name == "lift":
            l = sfx_lift()
            sfx.add(l, t, 1.0, pan=0.25)
            sfx_verb.add(l, t, 0.3)
        elif name == "term":
            sfx.add(sfx_pop(380, 760, 0.1, 0.18), t, 1.0, pan=0.2)
            sfx.add(sfx_whoosh(0.22, 600, 2600, 0.1), t - 0.03, 1.0, pan=0.2)
        elif name in ("type", "enter"):
            sfx.add(sfx_key(), t, 0.55 if name == "type" else 0.75, pan=0.2)
        elif name == "whoosh":
            sfx.add(sfx_whoosh(0.5, 300, 2600, 0.24), t - 0.08, 1.0)
        elif name == "word":
            sfx.add(sfx_pop(900, 700, 0.05, 0.08), t, 1.0)
        elif name == "blip":
            i = e.get("i", 0)
            s = sfx_tick(-0.3 + 0.08 * i)
            sfx.add(s, t, 0.9, pan=-0.5 + i * 0.11)
            sfx_verb.add(s, t, 0.3)
        elif name == "bloom":
            sfx.add(sfx_whoosh(0.5, 250, 2200, 0.2), t - 0.05, 1.0)
            sfx.add(sfx_open(), t, 0.9)
        elif name == "riser":
            sec = e["until"] - t
            sfx.add(pan2(sfx_riser(sec), 0), t, 1.0)
        elif name == "whoosh_big":
            sfx.add(sfx_whoosh_big(), t - 0.06, 1.0)
        elif name == "impact":
            im = sfx_impact()
            sfx.add(pan2(im, 0), t, 0.8)
            sfx_verb.add(pan2(im, 0), t, 0.3)
        elif name == "shing":
            s = sfx_shing()
            sfx.add(s, t, 1.0)
            sfx_verb.add(s, t, 0.4)
        elif name == "chime":
            c = sfx_chime()
            sfx.add(c, t, 0.6)
            sfx_verb.add(c, t, 0.5)

    # ------------------------------------------------------------------ mix
    ir = reverb_ir()
    wet = convolve(verb_send.x, ir) + convolve(sfx_verb.x, ir) * 0.9
    dly = pingpong(delay_send.x, BEAT * 0.75, 0.38)
    music_mix = music.x + dly * 0.5 + convolve(dly * 0.3, ir)
    music_mix = butter(music_mix, "highpass", 28, 2)
    mix = music_mix * 0.62 + wet * 0.55 + sfx.x * 1.0

    # gentle fade at the end
    n = n_of(duration)
    mix = mix[:, :n]
    fade_n = n_of(1.2)
    mix[:, -fade_n:] *= np.linspace(1, 0, fade_n) ** 1.5
    return mix


def master(x, target_lufs=-15.0, ceiling_db=-1.5):
    try:
        import pyloudnorm as pyln

        meter = pyln.Meter(SR)
        loud = meter.integrated_loudness(x.T)
        x = x * 10 ** ((target_lufs - loud) / 20)
    except Exception as e:  # pragma: no cover
        print("loudness skipped:", e)
        x = x / (np.abs(x).max() + 1e-9) * 0.5
    # look-ahead limiter on the 4× oversampled (true) peak
    ceiling = 10 ** (ceiling_db / 20)
    over = np.abs(signal.resample_poly(x, 4, 1, axis=1)).max(axis=0)
    peak = over[: x.shape[1] * 4].reshape(-1, 4).max(axis=1)
    look = n_of(0.003)
    peak = np.maximum.reduce([np.roll(peak, -k) for k in range(look)])
    gain = np.minimum(1.0, ceiling / np.maximum(peak, 1e-9))
    rel = np.exp(-1 / (SR * 0.08))
    smooth = np.empty_like(gain)
    g = 1.0
    for i in range(len(gain)):
        target = gain[i]
        g = target if target < g else target + (g - target) * rel
        smooth[i] = g
    return x * smooth


def main():
    src, out = sys.argv[1], sys.argv[2]
    data = json.load(open(src))
    mix = build(data["events"], data["duration"])
    mix = master(mix)
    from scipy.io import wavfile

    pcm = np.clip(mix.T, -1, 1)
    wavfile.write(out, SR, (pcm * 32767).astype(np.int16))
    print(out, f"{mix.shape[1] / SR:.2f}s", "peak", f"{20 * np.log10(np.abs(mix).max()):.2f} dBFS")


if __name__ == "__main__":
    main()

