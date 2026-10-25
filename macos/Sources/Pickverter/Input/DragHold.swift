import Foundation

/// Holding ⇧, ⌘ or ⌃ while dragging files opens that key's ring once the pointer rests for a moment. The key may go
/// down before the drag starts or during it. While the pointer keeps moving nothing happens, so Finder's own drags carry
/// on as before, and a quick tap still opens the ring right away (the input monitor handles taps). The Windows app
/// follows the same rule (DragHold.cs). Times are in seconds, positions in points.
struct DragHold {
    /// How long the pointer has to stay put while the key is down.
    static let restSeconds: TimeInterval = 0.5

    let slop: Double
    /// The ring key held on its own.
    private(set) var key: Int64?
    /// This hold already opened the ring during the current drag.
    private(set) var used = false
    private var anchorX = 0.0
    private var anchorY = 0.0
    private var restSince: TimeInterval = 0

    init(slop: Double) { self.slop = slop }

    /// A ring key went down with no other modifier held.
    mutating func press(_ key: Int64, now: TimeInterval, x: Double, y: Double) {
        self.key = key
        used = false
        rest(now, x, y)
    }

    /// The key went up, another key joined it, or typing interrupted it.
    mutating func cancel() {
        key = nil
        used = false
    }

    /// A new file drag started (the key may already be down): the rest counts from here.
    mutating func dragStarted(now: TimeInterval, x: Double, y: Double) {
        used = false
        rest(now, x, y)
    }

    /// The pointer moved during the drag. Small jitter doesn't count as moving.
    mutating func moved(now: TimeInterval, x: Double, y: Double) {
        if abs(x - anchorX) > slop || abs(y - anchorY) > slop { rest(now, x, y) }
    }

    /// Call while files are being dragged: the key whose ring opens now, at most once per hold and drag.
    mutating func due(now: TimeInterval) -> Int64? {
        guard let key, !used, now - restSince >= Self.restSeconds else { return nil }
        used = true
        return key
    }

    private mutating func rest(_ now: TimeInterval, _ x: Double, _ y: Double) {
        anchorX = x
        anchorY = y
        restSince = now
    }
}
