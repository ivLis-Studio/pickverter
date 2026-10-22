"""Score + sound design for the Pickverter promo, synthesised from scratch (no samples).

    .venv/bin/python audio/synth.py out/events-ko.json out/audio-ko.wav

Music: 128 BPM in A major. IV V iii vi (Dmaj7 · E6 · C#m7 · F#m7) under syncopated supersaw stabs, an offbeat house
bass, four-on-the-floor drums with sidechain pump, mallet answers during the demos and a pluck lead for the montage
and the end card. Energy only climbs: a filtered bar for the hook, the drop on the first wheel, a lift into the montage,
one build, and the tonic (A) lands on the app icon. The bar map is shared with the stage (window.MUSIC).
SFX: every UI event recorded by the stage (clicks, key taps, hover ticks, whooshes, the finale hit).
"""
import json
import sys

import numpy as np
from scipy import signal

SR = 48000
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
    if x.ndim == 1:
        x[:na] *= np.linspace(0, 1, na)
        x[-nr:] *= np.linspace(1, 0, nr)
    else:
        x[:, :na] *= np.linspace(0, 1, na)
        x[:, -nr:] *= np.linspace(1, 0, nr)
    return x


def butter(x, kind, f, order=2):
    sos = signal.butter(order, f, kind, fs=SR, output="sos")
    return signal.sosfilt(sos, x, axis=-1)


def shelf(x, f0, gain_db, kind="low", s=1.0):
    """RBJ shelving EQ."""
    A = 10 ** (gain_db / 40)
    w0 = 2 * np.pi * f0 / SR
    alpha = np.sin(w0) / 2 * np.sqrt((A + 1 / A) * (1 / s - 1) + 2)
    cw, sq = np.cos(w0), 2 * np.sqrt(A) * alpha
    if kind == "low":
        b = [A * ((A + 1) - (A - 1) * cw + sq), 2 * A * ((A - 1) - (A + 1) * cw), A * ((A + 1) - (A - 1) * cw - sq)]
        a = [(A + 1) + (A - 1) * cw + sq, -2 * ((A - 1) + (A + 1) * cw), (A + 1) + (A - 1) * cw - sq]
    else:
        b = [A * ((A + 1) + (A - 1) * cw + sq), -2 * A * ((A - 1) + (A + 1) * cw), A * ((A + 1) + (A - 1) * cw - sq)]
        a = [(A + 1) - (A - 1) * cw + sq, 2 * ((A - 1) - (A + 1) * cw), (A + 1) - (A - 1) * cw - sq]
    return signal.lfilter(np.array(b) / a[0], np.array(a) / a[0], x, axis=-1)


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


def reverb_ir(sec=2.2, predelay=0.016, damp=5600, seed=3):
    r = np.random.default_rng(seed)
    n = n_of(sec)
    t = np.arange(n) / SR
    env = np.exp(-t / (sec / 6.4))
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
        src = butter(x[(k + 1) % 2], "lowpass", damp, 1)
        if k * d < x.shape[1]:
            out[k % 2, k * d:] += src[: x.shape[1] - k * d] * g
    return out


# ----------------------------------------------------------------------------- oscillators
def saw(freq, n, phase=0.0):
    """Band-limited sawtooth (polyBLEP)."""
    dt = freq / SR
    t = (phase + dt * np.arange(n)) % 1.0
    y = 2.0 * t - 1.0
    m = t < dt
    x = t[m] / dt
    y[m] -= x + x - x * x - 1.0
    m = t > 1.0 - dt
    x = (t[m] - 1.0) / dt
    y[m] -= x * x + x + x + 1.0
    return y


def pulse(freq, n, phase=0.0):
    return 0.5 * (saw(freq, n, phase) - saw(freq, n, phase + 0.5))


def env_adsr(n, a, d, s, r):
    """Attack/decay to sustain, then a release over the last r seconds."""
    t = np.arange(n) / SR
    e = np.where(t < a, t / max(a, 1e-6), s + (1 - s) * np.exp(-(t - a) / max(d, 1e-6)))
    nr = min(n, n_of(r))
    e[n - nr:] *= np.linspace(1, 0, nr) ** 1.4
    return e


# ----------------------------------------------------------------------------- instruments
_cache = {}


def supersaw(notes, sec, cutoff=3600, detune=(-17, -8, 0, 8, 17), gain=0.05, key=None):
    """Stereo detuned saw chord, low-passed. Cached by key (the same stab repeats many times)."""
    if key is not None and key in _cache:
        return _cache[key]
    n = n_of(sec)
    st = np.zeros((2, n))
    r = np.random.default_rng(hash(tuple(notes)) % 2 ** 32)
    for m in notes:
        f = midi(m)
        for k, c in enumerate(detune):
            y = saw(f * 2 ** (c / 1200), n, r.random())
            p = 0.85 * (k / (len(detune) - 1) * 2 - 1)
            a = (p + 1) * np.pi / 4
            st[0] += y * np.cos(a)
            st[1] += y * np.sin(a)
    st = butter(st, "lowpass", cutoff, 2) * gain / len(detune)
    st = butter(st, "highpass", 180, 1)
    if key is not None:
        _cache[key] = st
    return st


def stab(notes, sec=0.22, cutoff=3600, vel=1.0):
    base = supersaw(notes, sec + 0.06, cutoff, key=("stab", tuple(notes), cutoff, round(sec, 3)))
    e = env_adsr(base.shape[1], 0.003, 0.09, 0.28, 0.06)
    return base * e * vel


def pad(notes, sec, cutoff=1800, attack=0.4, release=0.6, vel=1.0):
    st = supersaw(notes, sec, cutoff, detune=(-11, -4, 4, 11), gain=0.05)
    e = env_adsr(st.shape[1], attack, 10.0, 1.0, release)
    return st * e * vel


def bass_note(m, sec, vel=1.0, bright=850):
    n = n_of(sec)
    t = np.arange(n) / SR
    f = midi(m)
    sub = np.sin(2 * np.pi * f * t)
    mid = butter(saw(f, n) + 0.4 * saw(f * 1.005, n, 0.3), "lowpass", bright, 2)
    e = np.minimum(1, t / 0.003) * np.exp(-t / max(sec * 0.55, 0.05))
    y = (sub * 0.85 + mid * 0.55) * e
    y = np.tanh(y * 1.7) / 1.7
    return fade(y * vel * 0.36, 0.0008, 0.012)


def lead_note(m, sec, vel=1.0):
    n = n_of(sec + 0.12)
    f = midi(m)
    y = saw(f, n) * 0.55 + saw(f * 2 ** (6 / 1200), n, 0.37) * 0.45 + pulse(f * 2, n) * 0.18
    y = sweep(y, 7800, 1500, q=0.85, kind="lowpass", block=96)
    e = env_adsr(n, 0.004, 0.16, 0.42, 0.1)
    t = np.arange(n) / SR
    bell = np.sin(2 * np.pi * f * 2 * t) * np.exp(-t / 0.18) * 0.25
    return fade((y * e + bell) * vel * 0.13, 0.001, 0.04)


def mallet(m, vel=1.0):
    t = tt(0.55)
    f = midi(m)
    y = np.sin(2 * np.pi * f * t) * np.exp(-t / 0.2) + 0.32 * np.sin(2 * np.pi * f * 4 * t) * np.exp(-t / 0.045)
    y += 0.12 * np.sin(2 * np.pi * f * 10 * t) * np.exp(-t / 0.012)
    return fade(y * vel * 0.16, 0.0008, 0.05)


def bell(m, sec=2.5, vel=1.0):
    f = midi(m)
    t = tt(sec)
    index = 2.2 * np.exp(-t / 0.25)
    mod = np.sin(2 * np.pi * f * 3.5 * t) * index
    y = np.sin(2 * np.pi * f * t + mod) * np.exp(-t / 0.9)
    y += 0.25 * np.sin(2 * np.pi * f * 2.76 * t) * np.exp(-t / 0.35)
    return fade(y * vel * 0.25, 0.001, 0.2)


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


# drums
def kick(vel=1.0):
    t = tt(0.42)
    f = 47 + 115 * np.exp(-t / 0.03) + 26 * np.exp(-t / 0.11)
    body = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / 0.25)
    body = np.tanh(body * 2.4) / np.tanh(2.4)
    click = butter(noise(0.42), "bandpass", [1800, 7500]) * np.exp(-t / 0.003) * 0.32
    return fade((body * 0.92 + click) * vel * 0.6, 0.0004, 0.02)


def clap(vel=1.0):
    sec = 0.34
    y = np.zeros(n_of(sec))
    for k, off in enumerate((0, 0.010, 0.021, 0.031)):
        burst = noise(0.26) * expdec(0.26, 0.007 if k < 3 else 0.075)
        i = n_of(off)
        y[i:i + len(burst)] += burst[: len(y) - i]
    y = butter(y, "bandpass", [950, 5200])
    t = tt(sec)
    body = np.sin(2 * np.pi * 210 * t) * np.exp(-t / 0.035) * 0.25
    return fade((y * 0.2 + body * 0.5) * vel, 0.0004, 0.03)


def snare(vel=1.0):
    t = tt(0.2)
    body = np.sin(2 * np.pi * 195 * t) * np.exp(-t / 0.045) * 0.42
    sn = butter(noise(0.2), "bandpass", [1600, 8000]) * np.exp(-t / 0.055)
    return fade((body + sn) * vel * 0.16, 0.0004, 0.02)


def hat(vel=1.0, sec=0.045):
    y = butter(noise(sec + 0.02), "highpass", 7600, 2) * expdec(sec + 0.02, sec / 3.2)
    return fade(y * vel * 0.085, 0.0004, 0.008)


def open_hat(vel=1.0, sec=0.24):
    x = noise(sec)
    y = butter(x, "highpass", 6800, 2) * expdec(sec, 0.075)
    return fade(y * vel * 0.065, 0.0006, 0.03)


def crash(vel=1.0, sec=2.4):
    t = tt(sec)
    x = butter(noise(sec), "highpass", 4000, 2) * np.exp(-t / 0.62)
    metal = np.zeros_like(t)
    for f in (3810, 5230, 6650, 7420, 8890, 10350):
        metal += np.sin(2 * np.pi * f * t + rng.random() * 6.28)
    metal *= np.exp(-t / 0.45) * 0.022
    y = x * 0.1 + metal * 0.75
    st = np.vstack([y, butter(np.roll(y, 37), "highpass", 4500, 1)])
    return fade(st * vel, 0.0004, 0.4)


def rev_crash(sec=1.2):
    c = crash(1.0, sec)[:, ::-1]
    e = np.linspace(0, 1, c.shape[1]) ** 2.2
    return fade(c * e * 0.9, 0.05, 0.004)


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
    return fade(y * (0.66 if big else 0.42), 0.0003, 0.02)


def sfx_tick(pitch=0.0):
    f = 2050 * 2 ** pitch
    t = tt(0.05)
    y = np.sin(2 * np.pi * f * t) * np.exp(-t / 0.009) + 0.3 * np.sin(2 * np.pi * 2 * f * t) * np.exp(-t / 0.004)
    return fade(y * 0.15, 0.0003, 0.01)


def sfx_whoosh(sec=0.42, f0=350, f1=3200, gain=0.35, q=0.9):
    y = sweep(noise(sec), f0, f1, q=q)
    env = np.sin(np.pi * np.linspace(0, 1, len(y))) ** 1.6
    return fade(y * env * gain, 0.002, 0.02)


def sfx_open():
    w = sfx_whoosh(0.2, 500, 2400, 0.2, 1.1)
    t = tt(0.2)
    glide = np.sin(2 * np.pi * np.cumsum(420 + 380 * (t / 0.2)) / SR) * np.exp(-t / 0.06) * 0.08
    spark = np.sin(2 * np.pi * 3100 * t) * np.exp(-t / 0.02) * 0.05
    return w + glide + spark


def sfx_close():
    return sfx_whoosh(0.14, 2200, 700, 0.11, 1.2)


def sfx_pop(f0=520, f1=1050, sec=0.08, gain=0.22):
    t = tt(sec)
    f = f0 + (f1 - f0) * (1 - np.exp(-t / (sec / 3)))
    y = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / (sec / 3.5))
    return fade(y * gain, 0.0005, 0.01)


def sfx_done():
    a, b = bell(88, 1.2, 0.9), bell(95, 1.2, 0.8)
    out = np.zeros(n_of(1.4))
    out[: len(a)] += a
    out[n_of(0.075): n_of(0.075) + len(b)] += b
    return out * 0.85


def sfx_lift():
    t = tt(0.55)
    glide = np.sin(2 * np.pi * np.cumsum(380 * 2 ** (1.6 * t / 0.55)) / SR) * np.sin(np.pi * t / 0.55) * 0.07
    shimmer = butter(noise(0.55), "highpass", 6000) * np.sin(np.pi * t / 0.55) ** 2 * 0.05
    return sfx_whoosh(0.55, 400, 4000, 0.18) + glide + shimmer


def sfx_riser(sec, top=7000):
    t = tt(sec)
    x = sweep(noise(sec), 300, top, q=2.0)
    env = (t / sec) ** 2.2
    tone = np.zeros_like(t)
    for k, base in enumerate((110, 165, 220)):
        f = base * 2 ** (2.0 * (t / sec) ** 1.6)
        tone += np.sin(2 * np.pi * np.cumsum(f) / SR + k)
    return fade((x * 0.5 + tone * 0.05) * env * 0.4, 0.01, 0.01)


def sfx_downlifter(sec=1.4):
    t = tt(sec)
    x = sweep(noise(sec), 6000, 300, q=1.4)
    return fade(x * np.exp(-t / (sec / 3)) * 0.22, 0.005, 0.05)


def sfx_whoosh_big():
    x = sweep(noise(0.6), 180, 9000, q=0.8)
    env = np.sin(np.pi * np.linspace(0, 1, len(x))) ** 1.2
    t = tt(0.6)
    drop = np.sin(2 * np.pi * np.cumsum(140 * np.exp(-t / 0.25) + 35) / SR) * env * 0.25
    st = np.vstack([x * env * np.linspace(1.0, 0.25, len(x)), x * env * np.linspace(0.25, 1.0, len(x))]) * 0.5
    return st + pan2(drop, 0)


def sfx_impact(scale=1.0):
    t = tt(2.2)
    boom = np.sin(2 * np.pi * np.cumsum(36 + 46 * np.exp(-t / 0.08)) / SR) * np.exp(-t / 0.75) * 0.8
    boom = np.tanh(boom * 1.8) / 1.8
    crack = butter(noise(2.2), "lowpass", 3200) * np.exp(-t / 0.05) * 0.35
    return fade((boom + crack) * scale, 0.0005, 0.2)


def sfx_shing():
    t = tt(0.7)
    swish = sweep(noise(0.7), 9000, 2500, q=1.4, kind="bandpass") * np.exp(-t / 0.09) * 0.45
    ring = (np.sin(2 * np.pi * 5200 * t) + 0.6 * np.sin(2 * np.pi * 7850 * t) + 0.4 * np.sin(2 * np.pi * 3400 * t)) * np.exp(-t / 0.22) * 0.05
    return fade(swish + ring, 0.0008, 0.05)


def sfx_chime(notes=(81, 88, 93)):
    out = np.zeros(n_of(2.6))
    for k, (m, d) in enumerate(zip(notes, (0.0, 0.06, 0.12))):
        b = bell(m, 2.4, 0.55 - 0.1 * k)
        i = n_of(d)
        out[i:i + len(b)] += b[: len(out) - i]
    return out


def sfx_ping(i=0):
    t = tt(0.9)
    f = 1318.5 * (1.0, 1.122, 1.26)[i % 3]
    y = np.sin(2 * np.pi * f * t) * np.exp(-t / 0.18) + 0.2 * np.sin(2 * np.pi * f * 2.01 * t) * np.exp(-t / 0.07)
    return fade(y * 0.1, 0.002, 0.1)


def sfx_slam():
    t = tt(0.4)
    thump = np.sin(2 * np.pi * np.cumsum(70 + 90 * np.exp(-t / 0.03)) / SR) * np.exp(-t / 0.12) * 0.35
    air = sfx_whoosh(0.4, 2400, 500, 0.1, 1.0)
    return thump + air


def sfx_mosaic():
    out = np.zeros(n_of(0.4))
    for k in range(5):
        b = butter(noise(0.03), "bandpass", [1500 + 300 * k, 4000 + 400 * k]) * expdec(0.03, 0.006) * 0.12
        i = n_of(0.05 * k)
        out[i:i + len(b)] += b
    return out


def sfx_lock():
    t = tt(0.14)
    a = butter(noise(0.14), "highpass", 3000) * np.exp(-t / 0.004) * 0.4
    a += np.sin(2 * np.pi * 1850 * t) * np.exp(-t / 0.02) * 0.2 + np.sin(2 * np.pi * 620 * t) * np.exp(-t / 0.03) * 0.25
    return fade(a * 0.5, 0.0003, 0.01)


# ----------------------------------------------------------------------------- score
CHORDS = {
    "D": ([62, 66, 69, 73], 38),
    "E": ([64, 68, 71, 73], 40),
    "C#m": ([61, 64, 68, 71], 37),
    "F#m": ([61, 64, 66, 69], 42),
    "A": ([61, 64, 69, 71], 33),
}
LOOP = ["D", "E", "C#m", "F#m"]


def chord_at(bar, step):
    if bar <= 0:
        return "E"
    if bar <= 12:
        return LOOP[(bar - 1) % 4]
    if bar in (13, 15):
        return "D" if step < 8 else "E"
    return "A"


# lead: (bar, 16th step, midi, length in 16ths)
LEAD = [
    (10, 8, 76, 2), (10, 10, 80, 2), (10, 12, 83, 2), (10, 14, 85, 2),
    (11, 0, 76, 3), (11, 3, 73, 3), (11, 6, 76, 2), (11, 8, 80, 3), (11, 11, 76, 3), (11, 14, 73, 2),
    (12, 0, 78, 3), (12, 3, 76, 3), (12, 6, 73, 2), (12, 8, 78, 3), (12, 11, 81, 3), (12, 14, 78, 2),
    (13, 0, 78, 3), (13, 3, 76, 3), (13, 6, 74, 2), (13, 8, 76, 3), (13, 11, 78, 3), (13, 14, 80, 2),
    (14, 0, 81, 6), (14, 6, 85, 2), (14, 8, 83, 3), (14, 11, 81, 3), (14, 14, 76, 2),
    (15, 0, 78, 3), (15, 3, 76, 3), (15, 6, 74, 2), (15, 8, 76, 3), (15, 11, 80, 3), (15, 14, 83, 2),
    (16, 0, 81, 12),
]
# mallet answers during the demos (bar, step, midi): a little call at the end of every second bar
# (not in bar 10, where the lead's pickup into the montage takes over)
ANSWERS = []
for bar in range(2, 10, 2):
    c = CHORDS[chord_at(bar, 12)][0]
    ANSWERS += [(bar, 10, c[3] + 12), (bar, 12, c[2] + 12), (bar, 14, c[1] + 12)]

STAB_STEPS = {0: 1.0, 3: 0.78, 6: 0.9, 10: 0.84, 12: 0.72}


def build(events, duration, music):
    bpm = music.get("bpm", 128)
    beat = 60 / bpm
    bar_len = 4 * beat
    s16 = beat / 4
    at = lambda bar, step=0: bar * bar_len + step * s16
    total = duration + 3
    music_bus, verb, delay, sfx, sfx_verb = Bus(total), Bus(total), Bus(total), Bus(total), Bus(total)
    duck_src = Bus(total)  # pumped by the kick: chords, pad, bass
    kicks = []

    M0, BUILD, FIN, END = at(10, 8), at(13, 8), at(14), at(16)

    def section(t):
        if t < at(1):
            return "intro"
        if t < at(5):
            return "A"
        if t < M0:
            return "A2"
        if t < BUILD:
            return "M"
        if t < FIN:
            return "build"
        if t < END:
            return "F"
        return "end"

    # ---------------- drums, bass, stabs on a 16th grid
    for bar in range(0, 16):
        for step in range(16):
            t = at(bar, step)
            sec = section(t)
            ch = chord_at(bar, step)
            notes, root = CHORDS[ch]
            beat_step = step % 4
            # kick
            four = sec in ("A", "A2", "M", "F") or (sec == "intro") or (sec == "build" and t < at(13, 12))
            if beat_step == 0 and four:
                v = 0.95 if sec != "intro" else 0.85
                music_bus.add(kick(v), t, 1.0)
                kicks.append(t)
            # clap on 2 and 4
            if step in (4, 12) and sec in ("intro", "A", "A2", "M", "F"):
                c = clap(1.0 if sec != "intro" else 0.8)
                music_bus.add(c, t, 1.0, pan=0.04)
                verb.add(pan2(c, 0), t, 0.3)
                if sec in ("M", "F"):
                    music_bus.add(snare(0.7), t, 1.0)
            # hats
            if sec in ("A", "A2", "M", "F", "build"):
                acc = beat_step == 2
                v = 0.85 if acc else (0.32 if beat_step % 2 else 0.45)
                if sec == "A" and beat_step in (1, 3):
                    v *= 0.5
                music_bus.add(hat(v), t, 1.0, pan=0.32 if beat_step % 2 else -0.18)
                if acc and sec in ("A2", "M", "F"):
                    music_bus.add(open_hat(0.9 if sec != "A2" else 0.7), t, 1.0, pan=-0.22)
            elif sec == "intro" and beat_step == 2:
                music_bus.add(hat(0.7), t, 1.0, pan=0.3)
            # offbeat bass (+ an octave pickup at the end of each bar in the montage and finale)
            if sec in ("A", "A2", "M", "F") and beat_step == 2:
                b = bass_note(root + 12, beat * 0.42, 0.95, 950 if sec in ("M", "F") else 800)
                duck_src.add(pan2(b, 0), t, 1.0)
            if sec in ("M", "F") and step == 15:
                duck_src.add(pan2(bass_note(root + 24, s16 * 0.9, 0.55, 1300), 0), t, 1.0)
            if sec in ("A", "A2", "M", "F") and step == 0:
                # a sub under the downbeat for weight (ducked by the kick, so it blooms after it)
                duck_src.add(pan2(bass_note(root, beat * 1.4, 0.26, 300), 0), t, 1.0)
            # chord stabs (filtered in the intro, brighter as the film goes on)
            if step in STAB_STEPS:
                if sec == "intro":
                    cut = 700 + 1900 * (t / at(1)) ** 1.6
                    cut = round(cut / 100) * 100
                elif sec == "A":
                    cut = 3200
                elif sec == "A2":
                    cut = 3800
                elif sec in ("M", "F"):
                    cut = 5200
                elif sec == "build":
                    cut = 2400 + 2800 * (t - BUILD) / (FIN - BUILD)
                    cut = round(cut / 200) * 200
                else:
                    cut = None
                if cut:
                    s = stab(notes, 0.2, cut, STAB_STEPS[step] * (0.75 if sec == "intro" else 1.0))
                    duck_src.add(s, t, 1.0)
                    delay.add(s, t, 0.12)

    # pads: soft bed per bar (fuller in the montage and finale)
    for bar in range(0, 16):
        sec = section(at(bar))
        for half in (0, 8):
            ch = chord_at(bar, half)
            notes, _ = CHORDS[ch]
            dur = 2 * beat + 0.15
            v = {"intro": 0.5, "A": 0.45, "A2": 0.55, "M": 0.8, "build": 0.8, "F": 0.85}.get(sec, 0)
            if v:
                p = pad([n - 12 for n in notes], dur, cutoff=2200 if sec in ("A", "A2", "intro") else 3000, attack=0.05, release=0.12, vel=v * 1.2)
                duck_src.add(p, at(bar, half), 1.0)

    # mallet answers during the demos
    for bar, step, m in ANSWERS:
        s = mallet(m, 0.9)
        music_bus.add(s, at(bar, step), 0.9, pan=0.25 if step != 12 else -0.25)
        delay.add(pan2(s, 0), at(bar, step), 0.35)
        verb.add(pan2(s, 0), at(bar, step), 0.25)

    # lead
    for bar, step, m, length in LEAD:
        t = at(bar, step)
        s = lead_note(m, length * s16, 1.0 if bar < 16 else 1.1)
        music_bus.add(s, t, 1.0, pan=0.0)
        delay.add(pan2(s, 0), t, 0.42)
        verb.add(pan2(s, 0), t, 0.32)

    # crashes, risers, fills, impacts
    music_bus.add(crash(0.85), 0.0, 1.0)
    for t, v in ((at(1), 1.0), (at(5), 0.6), (M0, 1.0), (at(11), 0.55), (FIN, 1.15)):
        music_bus.add(crash(v), t, 1.0)
    music_bus.add(rev_crash(1.0), at(1) - 1.0, 0.8)
    music_bus.add(rev_crash(0.9), M0 - 0.9, 0.8)
    music_bus.add(rev_crash(1.4), FIN - 1.4, 1.0)
    music_bus.add(pan2(sfx_riser(at(1) - at(0, 6), 6000), 0), at(0, 6), 0.75)
    music_bus.add(pan2(sfx_riser(M0 - at(9, 8), 7000), 0), at(9, 8), 0.8)
    music_bus.add(pan2(sfx_riser(FIN - at(12), 9000), 0), at(12), 1.0)
    # snare fills
    for i in range(4):
        music_bus.add(snare(0.35 + 0.15 * i), at(0, 12 + i), 1.0)
    for i in range(4):
        music_bus.add(snare(0.3 + 0.12 * i), at(4, 12 + i), 0.9)
    for i in range(8):
        music_bus.add(snare(0.3 + 0.08 * i), M0 - 8 * s16 + i * s16, 1.0)
    t, gap = BUILD, s16 * 2
    while t < FIN - s16 * 0.4:
        v = 0.3 + 0.7 * (t - BUILD) / (FIN - BUILD)
        sn = snare(v)
        music_bus.add(sn, t, 1.0)
        verb.add(pan2(sn, 0), t, 0.25)
        t += gap
        gap = max(s16 / 2, gap * 0.9)
    for i in range(4):
        music_bus.add(snare(0.4 + 0.1 * i), at(15, 12 + i), 0.9)
    # landing on the icon and the last hit
    music_bus.add(pan2(sfx_impact(1.0), 0), FIN, 0.9)
    music_bus.add(pan2(sfx_downlifter(1.6), 0), FIN + 0.05, 0.7)
    fin_pad = pad([45, 57, 61, 64, 69, 71, 76], 3.6, cutoff=4200, attack=0.01, release=2.2, vel=0.9)
    duck_src.add(fin_pad, FIN, 0.55)
    verb.add(fin_pad, FIN, 0.4)
    end_pad = pad([45, 52, 57, 61, 64, 69, 73, 76], 3.2, cutoff=3800, attack=0.005, release=2.6, vel=1.0)
    music_bus.add(end_pad, END, 0.9)
    verb.add(end_pad, END, 0.5)
    music_bus.add(kick(1.0), END, 1.0)
    music_bus.add(pan2(bass_note(33, 2.2, 0.9, 500), 0), END, 1.0)
    music_bus.add(crash(1.2, 3.0), END, 1.0)
    music_bus.add(pan2(sfx_impact(0.6), 0), END, 0.6)
    for i, (m, d) in enumerate(((81, 0.0), (85, 0.09), (88, 0.18), (93, 0.27))):
        bl = bell(m, 2.6, 0.7 - 0.08 * i)
        music_bus.add(bl, END + d, 0.45, pan=(-0.3, 0.3)[i % 2])
        verb.add(pan2(bl, 0), END + d, 0.5)

    # sidechain pump
    duck = np.ones(duck_src.x.shape[1])
    for kt in kicks:
        i = n_of(kt)
        n = n_of(0.34)
        seg = 1 - 0.72 * np.exp(-np.arange(n) / SR / 0.085)
        j = min(len(duck), i + n)
        duck[i:j] = np.minimum(duck[i:j], seg[: j - i])
    music_bus.x += duck_src.x * duck

    # ------------------------------------------------------------------ sfx
    TILE = [80, 85, 88, 90, 93, 97, 100, 102]
    for e in events:
        name, t = e["name"], e["t"]
        i = e.get("i", 0)
        if name == "click":
            s = sfx_click(1.25 if e.get("icon") else 1.0)
            sfx.add(s, t, 1.25 if e.get("icon") else 1.0)
            if e.get("icon"):
                sfx_verb.add(s, t, 0.5)
        elif name == "key":
            s = sfx_key(e.get("big", False))
            sfx.add(s, t, 1.0)
            sfx_verb.add(s, t, 0.25 if e.get("big") else 0.08)
        elif name == "tick":
            sfx.add(sfx_tick(rng.uniform(-0.04, 0.06)), t, 1.0, pan=rng.uniform(-0.2, 0.2))
        elif name == "open":
            sfx.add(sfx_open(), t - 0.01, 1.0)
        elif name == "bloom":
            sfx.add(sfx_open(), t - 0.01, 1.1)
            sfx.add(sfx_whoosh(0.45, 260, 2600, 0.16), t - 0.08, 1.0)
            sfx_verb.add(sfx_open(), t, 0.4)
        elif name == "close":
            sfx.add(sfx_close(), t, 0.9)
        elif name == "swap":
            sfx.add(sfx_tick(-0.2), t, 0.9)
            sfx.add(sfx_tick(0.15), t + 0.045, 0.9)
        elif name == "choice":
            sfx.add(sfx_pop(600, 900, 0.07, 0.16), t, 1.0)
        elif name == "card":
            sfx.add(pan2(sfx_whoosh(0.32, 900, 3400, 0.14), 0.5), t - 0.04, 1.0)
        elif name == "done":
            d = sfx_done()
            sfx.add(d, t, 0.7, pan=0.4)
            sfx_verb.add(d, t, 0.3)
        elif name == "pop":
            sfx.add(sfx_pop(520 * 2 ** (i * 3 / 12), 1050 * 2 ** (i * 3 / 12), 0.08, 0.2), t, 1.0, pan=-0.2 + 0.2 * i)
        elif name == "swish":
            sfx.add(sfx_whoosh(0.3, 900, 4200, 0.22, 1.4), t, 1.0)
        elif name == "lift":
            l = sfx_lift()
            sfx.add(l, t, 1.0, pan=0.2)
            sfx_verb.add(l, t, 0.3)
        elif name in ("rise",):
            sfx.add(sfx_whoosh(0.5, 250, 2600, 0.22), t - 0.05, 1.0)
        elif name == "whip":
            sfx.add(pan2(sfx_whoosh(0.34, 500, 5200, 0.3, 0.8), 0.15), t, 1.0)
        elif name == "slam":
            sfx.add(sfx_slam(), t, 1.0)
        elif name == "keypop":
            sfx.add(sfx_pop(420, 760, 0.09, 0.16), t, 1.0)
        elif name == "ping":
            p = sfx_ping(i)
            sfx.add(p, t, 1.0, pan=0.2)
            sfx_verb.add(p, t, 0.6)
        elif name in ("phone_in", "slide"):
            sfx.add(pan2(sfx_whoosh(0.36, 700, 3000, 0.16), 0.45), t - 0.03, 1.0)
        elif name == "slide_out":
            sfx.add(pan2(sfx_whoosh(0.3, 2600, 700, 0.12), 0.45), t, 1.0)
        elif name == "fly":
            sfx.add(sfx_whoosh(0.5, 600, 4600, 0.18, 1.2), t, 1.0, pan=0.3)
        elif name == "receive":
            c = sfx_chime((88, 93, 97))
            sfx.add(c, t, 0.45, pan=0.35)
            sfx_verb.add(c, t, 0.4)
        elif name == "message":
            sfx.add(sfx_pop(700, 1150, 0.09, 0.15), t, 1.0, pan=0.3)
        elif name == "paste":
            sfx.add(sfx_whoosh(0.4, 800, 3800, 0.16, 1.1), t, 1.0, pan=0.2)
        elif name == "land":
            sfx.add(sfx_pop(380, 820, 0.1, 0.22), t, 1.0, pan=0.3)
        elif name == "sent":
            sfx.add(sfx_tick(0.3), t, 0.7, pan=0.3)
        elif name == "wipe":
            sfx.add(sfx_whoosh_big(), t, 0.8)
        elif name == "tile":
            s = pluck(TILE[i % len(TILE)], 0.5, 1.5, 0.5)
            sfx.add(s, t, 0.55, pan=-0.35 + 0.14 * i)
            sfx_verb.add(pan2(s, 0), t, 0.25)
        elif name == "v_trim":
            sfx.add(sfx_tick(-0.5), t + 0.08, 0.9)
            sfx.add(sfx_tick(-0.45), t + 0.16, 0.9)
        elif name == "v_merge":
            sfx.add(sfx_whoosh(0.3, 600, 2200, 0.1), t + 0.05, 1.0)
        elif name == "v_faces":
            sfx.add(sfx_mosaic(), t + 0.12, 1.0)
        elif name == "v_lock":
            sfx.add(sfx_lock(), t + 0.32, 1.0)
        elif name == "v_shrink":
            sfx.add(sfx_whoosh(0.35, 3000, 500, 0.12, 1.2), t, 1.0)
        elif name == "v_ocr":
            sfx.add(sfx_whoosh(0.45, 1200, 5200, 0.07, 2.0), t, 1.0)
        elif name == "implode":
            w = sfx_whoosh(0.6, 400, 6000, 0.22, 0.9)
            sfx.add(w, t, 1.0)
        elif name == "zoomout":
            sfx.add(sfx_whoosh_big(), t - 0.04, 1.0)
        elif name == "impact":
            pass  # scored with the music (FIN)
        elif name == "shing":
            s = sfx_shing()
            sfx.add(s, t, 1.0)
            sfx_verb.add(s, t, 0.4)
        elif name == "name":
            sfx.add(sfx_whoosh(0.4, 400, 2400, 0.1), t - 0.05, 1.0)
        elif name == "chime":
            c = sfx_chime()
            sfx.add(c, t, 0.5)
            sfx_verb.add(c, t, 0.45)

    # ------------------------------------------------------------------ mix
    ir = reverb_ir()
    wet = convolve(verb.x, ir) + convolve(sfx_verb.x, ir) * 0.9
    dly = pingpong(delay.x, beat * 0.75, 0.36)
    music_mix = music_bus.x + dly * 0.45 + convolve(dly * 0.25, ir)
    music_mix = butter(music_mix, "highpass", 34, 2)
    music_mix = shelf(music_mix, 110, -4.5, "low", 0.8)
    # gentle glue on the music bus (RMS compressor)
    lvl = np.sqrt(signal.lfilter([1 - np.exp(-1 / (SR * 0.03))], [1, -np.exp(-1 / (SR * 0.03))], (music_mix ** 2).mean(axis=0)) + 1e-12)
    thr = np.percentile(lvl, 70)
    gain = np.where(lvl > thr, (thr / lvl) ** 0.35, 1.0)
    music_mix *= gain
    mix = music_mix * 0.78 + wet * 0.5 + sfx.x * 0.95
    mix = shelf(mix, 7500, -2.5, "high", 0.8)

    n = n_of(duration)
    mix = mix[:, :n]
    fade_n = n_of(0.8)
    mix[:, -fade_n:] *= np.linspace(1, 0, fade_n) ** 1.6
    return mix


def master(x, target_lufs=-14.0, ceiling_db=-1.2):
    try:
        import pyloudnorm as pyln

        meter = pyln.Meter(SR)
        loud = meter.integrated_loudness(x.T)
        x = x * 10 ** ((target_lufs - loud) / 20)
    except Exception as e:  # pragma: no cover
        print("loudness skipped:", e)
        x = x / (np.abs(x).max() + 1e-9) * 0.5
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
    mix = build(data["events"], data["duration"], data.get("music", {"bpm": 128}))
    mix = master(mix)
    from scipy.io import wavfile

    pcm = np.clip(mix.T, -1, 1)
    wavfile.write(out, SR, (pcm * 32767).astype(np.int16))
    print(out, f"{mix.shape[1] / SR:.2f}s", "peak", f"{20 * np.log10(np.abs(mix).max()):.2f} dBFS")


if __name__ == "__main__":
    main()
