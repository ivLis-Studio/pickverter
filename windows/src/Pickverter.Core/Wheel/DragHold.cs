namespace Pickverter;

/// Holding Shift, Ctrl or Alt (⇧, ⌘ or ⌃ on a Mac) while dragging files opens that key's ring once the pointer rests for
/// a moment. The key may go down before the drag starts or during it. While the pointer keeps moving nothing happens,
/// so the system's own copy, move and shortcut drags carry on as before, and a quick tap still opens the ring right
/// away (the input monitor handles taps). The Mac app follows the same rule (DragHold.swift).
/// Times are in seconds; positions in whatever unit the slop is given in (pixels on Windows, points on a Mac).
public sealed class DragHold(double slop)
{
    /// How long the pointer has to stay put while the key is down.
    public const double RestSeconds = 0.5;

    private double anchorX, anchorY, restSince;

    /// The ring key held on its own, or null.
    public int? Key { get; private set; }

    /// This hold already opened the ring during the current drag.
    public bool Used { get; private set; }

    /// A ring key went down with no other modifier held.
    public void Press(int key, double now, double x, double y)
    {
        Key = key;
        Used = false;
        Rest(now, x, y);
    }

    /// The key went up, another key joined it, or typing interrupted it.
    public void Cancel()
    {
        Key = null;
        Used = false;
    }

    /// A new file drag started (the key may already be down): the rest counts from here.
    public void DragStarted(double now, double x, double y)
    {
        Used = false;
        Rest(now, x, y);
    }

    /// The pointer moved during the drag. Small jitter doesn't count as moving.
    public void Moved(double now, double x, double y)
    {
        if (Math.Abs(x - anchorX) > slop || Math.Abs(y - anchorY) > slop) Rest(now, x, y);
    }

    /// Call while files are being dragged: the key whose ring opens now, at most once per hold and drag.
    public int? Due(double now)
    {
        if (Key is not { } key || Used || now - restSince < RestSeconds) return null;
        Used = true;
        return key;
    }

    private void Rest(double now, double x, double y)
    {
        anchorX = x;
        anchorY = y;
        restSince = now;
    }
}
