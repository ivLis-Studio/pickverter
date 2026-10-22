using System.Diagnostics;
using System.Runtime.InteropServices;
using Pickverter.Interop;
using Pickverter.UI.Wheel;

namespace Pickverter.Input;

/// What the hook thread needs to know about the wheel, published by the UI thread on every change.
internal sealed record WheelSnapshot(bool IsVisible, WheelModel.WheelOrigin Origin, bool MenuOpen, double CenterX, double CenterY,
                                     double Scale, double Reach, bool TabsVisible)
{
    public static readonly WheelSnapshot Hidden = new(false, WheelModel.WheelOrigin.Selection, false, 0, 0, 1, 0, false);
    private static readonly WheelGeometry Geometry = new();

    /// A screen point (physical pixels) belongs to the wheel: inside its reach, or on the ring tabs below it.
    public bool Contains(int x, int y)
    {
        if (!IsVisible) return false;
        var dx = (x - CenterX) / Scale;
        var dy = (y - CenterY) / Scale;
        if (dx * dx + dy * dy <= Reach * Reach) return true;
        if (!TabsVisible) return false;
        var half = Geometry.TabBarWidth / 2;
        var top = Geometry.TabBarOffset - Geometry.TabBarHeight / 2 - 6;
        var bottom = Geometry.TabBarOffset + Geometry.TabBarHeight / 2 + 6;
        return dx >= -half && dx <= half && dy >= top && dy <= bottom;
    }
}

/// One pair of low-level hooks drives every gesture:
///  • tap Shift, Ctrl or Alt while dragging files (Convert, Tools, Actions)
///  • tap the trigger key (Alt by default) with files selected in File Explorer
///  • hold the trigger key and click a file in File Explorer
/// While the wheel is open it also routes keys, clicks and the scroll wheel to it. Hooks run on their own thread
/// and only ever post work to the UI thread, so a busy UI never delays typing.
internal sealed class InputMonitor
{
    public static InputMonitor Shared { get; } = new();

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    /// Unassigned virtual key: pressing it between Alt down and Alt up stops Windows from opening the menu bar.
    private const ushort VK_MASK = 0xE8;
    private static readonly IntPtr Signature = new(0x50564B31);
    private static readonly long TapLimit = Stopwatch.Frequency * 400 / 1000;
    private static readonly long DoubleTapLimit = Stopwatch.Frequency * 450 / 1000;

    private Thread? thread;
    private uint threadId;
    private IntPtr keyboardHook, mouseHook;
    private Native.HookProc? keyboardProc, mouseProc;
    private volatile WheelSnapshot wheel = WheelSnapshot.Hidden;

    private readonly bool[] down = new bool[256];
    private int pendingKey = -1;
    private long pendingDown;
    private bool pendingInterrupted;
    private int lastTapKey = -1;
    private long lastTapTime;
    private bool altUsed;
    private readonly HashSet<int> swallowed = [];

    private bool mouseDown;
    private Native.POINT downPoint;
    private bool clickCandidate;
    private bool dragActive;
    private bool swallowUp, swallowRightUp;
    private long latestPointer;
    private int pointerPosted;
    private readonly int dragX = Math.Max(4, Native.GetSystemMetrics(Native.SM_CXDRAG));
    private readonly int dragY = Math.Max(4, Native.GetSystemMetrics(Native.SM_CYDRAG));

    public bool IsRunning => keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero;
    public event Action? StateChanged;

    public WheelSnapshot Wheel
    {
        get => wheel;
        set => wheel = value;
    }

    public void Start()
    {
        if (thread != null) return;
        var ready = new ManualResetEventSlim();
        thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Pickverter input", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        Debug.Log("input monitor " + (IsRunning ? "running" : "unavailable"));
    }

    public void Stop()
    {
        if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread = null;
    }

    private void Run(ManualResetEventSlim ready)
    {
        threadId = Native.GetCurrentThreadId();
        keyboardProc = KeyboardProc;
        mouseProc = MouseProc;
        var module = Native.GetModuleHandle(null);
        keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
        ready.Set();
        Platform.Main.Post(() => StateChanged?.Invoke());
        while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        keyboardHook = mouseHook = IntPtr.Zero;
        Platform.Main.Post(() => StateChanged?.Invoke());
    }

    private static WheelController Controller => WheelController.Shared;
    private static void Post(Action action) => Platform.Main.Post(action);
    private static long Now => Stopwatch.GetTimestamp();

    // Keyboard

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != Signature)
            {
                var message = (int)wParam;
                var isDown = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                try
                {
                    if (HandleKey(Normalize((int)data.vkCode, data.flags), isDown, data)) return new IntPtr(1);
                }
                catch (Exception error)
                {
                    Debug.Log("keyboard hook: " + error.Message);
                }
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    private static int Normalize(int vk, uint flags) => vk switch
    {
        VK_SHIFT => VK_LSHIFT,
        VK_CONTROL => (flags & Native.LLKHF_EXTENDED) != 0 ? VK_RCONTROL : VK_LCONTROL,
        VK_MENU => (flags & Native.LLKHF_EXTENDED) != 0 ? VK_RMENU : VK_LMENU,
        _ => vk & 0xFF,
    };

    private static bool IsModifier(int vk) => vk is >= VK_LSHIFT and <= VK_RMENU or VK_LWIN or VK_RWIN;
    private static bool IsAlt(int vk) => vk is VK_LMENU or VK_RMENU;
    private static bool IsCtrl(int vk) => vk is VK_LCONTROL or VK_RCONTROL;
    private static bool IsShift(int vk) => vk is VK_LSHIFT or VK_RSHIFT;
    private bool AltHeld => down[VK_LMENU] || down[VK_RMENU];
    private bool CtrlHeld => down[VK_LCONTROL] || down[VK_RCONTROL];
    private bool ShiftHeld => down[VK_LSHIFT] || down[VK_RSHIFT];
    private bool WinHeld => down[VK_LWIN] || down[VK_RWIN];

    private static WheelMode? ModeFor(int vk) => vk switch
    {
        VK_LSHIFT or VK_RSHIFT => WheelMode.Convert,
        VK_LCONTROL or VK_RCONTROL => WheelMode.Tools,
        VK_LMENU or VK_RMENU => WheelMode.Actions,
        _ => null,
    };

    private static bool IsTrigger(int vk, TriggerKey key) => key switch
    {
        TriggerKey.Alt => IsAlt(vk),
        TriggerKey.Ctrl => IsCtrl(vk),
        _ => vk == VK_RCONTROL,
    };

    /// Exactly the trigger modifier is held (for trigger-click).
    private bool OnlyTriggerHeld(TriggerKey key)
    {
        var shift = ShiftHeld;
        var ctrl = CtrlHeld;
        var alt = AltHeld;
        if (WinHeld || shift) return false;
        return key switch
        {
            TriggerKey.Alt => alt && !ctrl,
            TriggerKey.Ctrl => ctrl && !alt,
            _ => down[VK_RCONTROL] && !down[VK_LCONTROL] && !alt,
        };
    }

    private bool HandleKey(int vk, bool isDown, Native.KBDLLHOOKSTRUCT data)
    {
        if (IsModifier(vk)) return HandleModifier(vk, isDown);
        if (!isDown) return swallowed.Remove(vk);
        pendingInterrupted = true;
        var snapshot = wheel;
        if (!snapshot.IsVisible || snapshot.MenuOpen) return false;
        var input = Translate(vk, data);
        Post(() => Controller.HandleKey(input));
        if (!Routes(input, snapshot.Origin)) return false;
        swallowed.Add(vk);
        if (AltHeld) altUsed = true;
        return true;
    }

    /// Mirrors WheelModel.HandleKey: which keys the open wheel keeps from the app below.
    private static bool Routes(KeyInput input, WheelModel.WheelOrigin origin)
    {
        if (origin == WheelModel.WheelOrigin.Drag) return false;
        if (input.Key == WheelKey.System) return false;
        if (input.Ctrl) return input.Key == WheelKey.Letter && input.Character is 'c' or 's';
        return !input.Alt;
    }

    private KeyInput Translate(int vk, Native.KBDLLHOOKSTRUCT data)
    {
        bool shift = ShiftHeld, ctrl = CtrlHeld, alt = AltHeld;
        switch (vk)
        {
            case 0x1B: return new KeyInput(WheelKey.Escape, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x0D: return new KeyInput(WheelKey.Enter, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x20: return new KeyInput(WheelKey.Space, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x25: return new KeyInput(WheelKey.Left, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x26: return new KeyInput(WheelKey.Up, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x27: return new KeyInput(WheelKey.Right, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x28: return new KeyInput(WheelKey.Down, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x09: return new KeyInput(WheelKey.Tab, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x21: return new KeyInput(WheelKey.PageUp, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x22: return new KeyInput(WheelKey.PageDown, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x24: return new KeyInput(WheelKey.Home, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x23: return new KeyInput(WheelKey.End, Shift: shift, Ctrl: ctrl, Alt: alt);
            case 0x2E or 0x08: return new KeyInput(WheelKey.Delete, Shift: shift, Ctrl: ctrl, Alt: alt);
            case VK_LWIN or VK_RWIN or 0x5D: return new KeyInput(WheelKey.System);
        }
        if (vk is >= 0x30 and <= 0x39) return new KeyInput(WheelKey.Digit, Digit: vk - 0x30, Shift: shift, Ctrl: ctrl, Alt: alt);
        if (vk is >= 0x60 and <= 0x69) return new KeyInput(WheelKey.Digit, Digit: vk - 0x60, Shift: shift, Ctrl: ctrl, Alt: alt);
        if (ctrl && vk is >= 0x41 and <= 0x5A) return new KeyInput(WheelKey.Letter, (char)(vk + 32), Shift: shift, Ctrl: true, Alt: alt);
        var character = Character(vk, data.scanCode);
        if (character != '\0' && char.IsLetter(character))
            return new KeyInput(WheelKey.Letter, char.ToLowerInvariant(character), Shift: shift, Ctrl: ctrl, Alt: alt);
        if (vk is >= 0x41 and <= 0x5A) return new KeyInput(WheelKey.Letter, (char)(vk + 32), Shift: shift, Ctrl: ctrl, Alt: alt);
        if (vk is >= 0x70 and <= 0x87) return new KeyInput(WheelKey.Other, Shift: shift, Ctrl: ctrl, Alt: alt);
        return new KeyInput(WheelKey.Other, Shift: shift, Ctrl: ctrl, Alt: alt);
    }

    /// The character the key types in the foreground window's keyboard layout (type-ahead in any language).
    private static char Character(int vk, uint scan)
    {
        try
        {
            var foreground = Native.GetForegroundWindow();
            var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(foreground, out _));
            var state = new byte[256];
            var buffer = new StringBuilder(4);
            // Flag 4: leave the keyboard state (and pending dead keys) untouched.
            var count = Native.ToUnicodeEx((uint)vk, scan, state, buffer, buffer.Capacity, 4, layout);
            return count == 1 ? buffer[0] : '\0';
        }
        catch (Exception)
        {
            return '\0';
        }
    }

    private bool HandleModifier(int vk, bool isDown)
    {
        var wasDown = down[vk];
        down[vk] = isDown;
        if (isDown)
        {
            if (wasDown) return false;
            var others = false;
            for (var key = VK_LSHIFT; key <= VK_RMENU; key++) others |= key != vk && down[key];
            others |= WinHeld && vk is not (VK_LWIN or VK_RWIN);
            if (!others && vk is not (VK_LWIN or VK_RWIN))
            {
                pendingKey = vk;
                pendingDown = Now;
                pendingInterrupted = false;
            }
            else
            {
                pendingKey = -1;
            }
            if (IsAlt(vk)) altUsed = false;
            return false;
        }
        var tapped = pendingKey == vk && !pendingInterrupted && Now - pendingDown < TapLimit;
        pendingKey = -1;
        var mask = tapped && Tapped(vk);
        if (IsAlt(vk) && (mask || altUsed))
        {
            altUsed = false;
            MaskAltRelease(vk);
            return true;
        }
        return false;
    }

    /// A lone tap of Shift, Ctrl or Alt. Returns true when the tap was used (an Alt release then gets masked).
    private bool Tapped(int vk)
    {
        var prefs = Preferences.Shared;
        if (!prefs.Enabled) return false;
        var mode = ModeFor(vk);
        var trigger = IsTrigger(vk, prefs.TriggerKey);
        var snapshot = wheel;
        Native.GetCursorPos(out var point);
        if (dragActive && mouseDown)
        {
            if (!prefs.DragTrigger || (mode == null && !trigger)) return false;
            if (snapshot.MenuOpen) return false;
            Post(() => Controller.DragTap(mode, point));
            return true;
        }
        if (snapshot.IsVisible)
        {
            if (snapshot.Origin != WheelModel.WheelOrigin.Selection || snapshot.MenuOpen) return false;
            Post(() => Controller.ModifierTap(mode, trigger));
            return true;
        }
        if (!trigger || !prefs.TapTrigger) return false;
        var root = Explorer.ForegroundExplorer();
        if (root == IntPtr.Zero || Explorer.IsEditingText(root)) return false;
        if (prefs.DoubleTap)
        {
            var now = Now;
            if (lastTapKey == vk && now - lastTapTime < DoubleTapLimit)
            {
                lastTapKey = -1;
            }
            else
            {
                lastTapKey = vk;
                lastTapTime = now;
                return true;
            }
        }
        var replay = IsAlt(vk) ? vk : -1;
        Post(() => Controller.OpenFromSelection(root, point, replay));
        return true;
    }

    private static Native.INPUT Key(ushort vk, bool up, bool extended = false) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)Native.MapVirtualKey(vk, 0),
                dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = Signature,
            },
        },
    };

    /// Alt was used by Pickverter: release it with a dummy key in between, so File Explorer doesn't open its menu.
    private static void MaskAltRelease(int vk)
    {
        var inputs = new[] { Key(VK_MASK, false), Key(VK_MASK, true), Key((ushort)vk, true, vk == VK_RMENU) };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    /// Nothing was selected after all: give File Explorer the Alt tap it would have seen.
    public void ReplayAltTap(int vk)
    {
        if (vk < 0 || AltHeld) return;
        var inputs = new[] { Key((ushort)vk, false, vk == VK_RMENU), Key((ushort)vk, true, vk == VK_RMENU) };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    /// A zero-length mouse move makes the running drag look at the window under the pointer again.
    public void Nudge()
    {
        var inputs = new[]
        {
            new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_MOVE, dwExtraInfo = Signature } },
            },
        };
        Native.SendInput(1, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    // Mouse

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != Signature)
            {
                try
                {
                    if (HandleMouse((int)wParam, data)) return new IntPtr(1);
                }
                catch (Exception error)
                {
                    Debug.Log("mouse hook: " + error.Message);
                }
            }
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    private void PostPointer(Native.POINT point)
    {
        Interlocked.Exchange(ref latestPointer, ((long)point.X << 32) | (uint)point.Y);
        if (Interlocked.Exchange(ref pointerPosted, 1) != 0) return;
        Post(() =>
        {
            Interlocked.Exchange(ref pointerPosted, 0);
            var packed = Interlocked.Read(ref latestPointer);
            Controller.PointerMoved(new Native.POINT((int)(packed >> 32), (int)packed));
        });
    }

    private bool HandleMouse(int message, Native.MSLLHOOKSTRUCT data)
    {
        var snapshot = wheel;
        var point = data.pt;
        switch (message)
        {
            case Native.WM_MOUSEMOVE:
                if (mouseDown && !dragActive && (Math.Abs(point.X - downPoint.X) > dragX || Math.Abs(point.Y - downPoint.Y) > dragY))
                {
                    dragActive = true;
                    clickCandidate = false;
                }
                if (snapshot.IsVisible && !snapshot.MenuOpen) PostPointer(point);
                return false;

            case Native.WM_LBUTTONDOWN:
                pendingInterrupted = true;
                if (snapshot.IsVisible && !snapshot.MenuOpen && snapshot.Origin == WheelModel.WheelOrigin.Selection)
                {
                    if (snapshot.Contains(point.X, point.Y))
                    {
                        var flip = AltHeld;
                        if (flip) altUsed = true;
                        swallowUp = true;
                        Post(() => Controller.Click(point, flip));
                        return true;
                    }
                    Post(() => Controller.Dismiss());
                }
                mouseDown = true;
                downPoint = point;
                dragActive = false;
                var prefs = Preferences.Shared;
                clickCandidate = prefs.Enabled && prefs.ClickTrigger && OnlyTriggerHeld(prefs.TriggerKey);
                if (clickCandidate && AltHeld) altUsed = true;
                return false;

            case Native.WM_LBUTTONUP:
                if (swallowUp)
                {
                    swallowUp = false;
                    return true;
                }
                mouseDown = false;
                if (dragActive)
                {
                    dragActive = false;
                    Post(() => Controller.DragSessionEnded());
                }
                if (clickCandidate)
                {
                    clickCandidate = false;
                    Post(() => Controller.OpenFromClick(point));
                }
                return false;

            case Native.WM_RBUTTONDOWN:
                pendingInterrupted = true;
                if (!snapshot.IsVisible || snapshot.MenuOpen) return false;
                if (snapshot.Origin == WheelModel.WheelOrigin.Selection && snapshot.Contains(point.X, point.Y))
                {
                    swallowRightUp = true;
                    Post(() => Controller.ShowContextMenu(point));
                    return true;
                }
                Post(() => Controller.Dismiss());
                return false;

            case Native.WM_RBUTTONUP:
                if (swallowRightUp)
                {
                    swallowRightUp = false;
                    return true;
                }
                return false;

            case Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN:
                pendingInterrupted = true;
                if (snapshot.IsVisible && !snapshot.MenuOpen) Post(() => Controller.Dismiss());
                return false;

            case Native.WM_MOUSEWHEEL:
                if (snapshot.IsVisible && !snapshot.MenuOpen && snapshot.Contains(point.X, point.Y))
                {
                    var delta = (short)(data.mouseData >> 16) / 120.0;
                    Post(() => Controller.Scroll(delta));
                    return true;
                }
                return false;
        }
        return false;
    }
}
