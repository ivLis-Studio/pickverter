using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Pickverter.Interop;

namespace Pickverter.Services;

/// Quiet sounds for finished and failed jobs, the system beep, and Narrator announcements.
internal sealed class WinFeedback : IFeedback
{
    private static readonly IntPtr Done = Load("done.wav");
    private static readonly IntPtr Failed = Load("failed.wav");

    /// The sound must stay in place while it plays asynchronously, so it is pinned for the life of the app.
    private static IntPtr Load(string name)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return IntPtr.Zero;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            return handle.AddrOfPinnedObject();
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static void Play(IntPtr sound)
    {
        if (sound == IntPtr.Zero) return;
        Native.PlaySound(sound, IntPtr.Zero, Native.SND_MEMORY | Native.SND_ASYNC | Native.SND_NODEFAULT);
    }

    public void Success() => Play(Done);
    public void Failure() => Play(Failed);
    public void Beep() => Native.MessageBeep(0);

    public bool ScreenReaderActive
    {
        get
        {
            var on = 0;
            return Native.SystemParametersInfo(Native.SPI_GETSCREENREADER, 0, ref on, 0) && on != 0;
        }
    }

    public void Announce(string text) => Platform.Main.Post(() => Announcer.Say(text));
}

/// UI Automation notifications go out through whichever Pickverter surface is on screen.
internal static class Announcer
{
    private static WeakReference<UIElement>? source;

    public static void Register(UIElement element) => source = new WeakReference<UIElement>(element);

    public static void Say(string text)
    {
        if (source == null || !source.TryGetTarget(out var element)) return;
        try
        {
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, "Pickverter");
        }
        catch (Exception error)
        {
            Debug.Log("announce: " + error.Message);
        }
    }
}
