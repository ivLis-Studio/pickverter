import Foundation

/// Limits how many heavy jobs (encodes, renders) run at once. Everything else (AirDrop, copy path…)
/// never waits. Slots are handed over in FIFO order; a cancelled waiter simply leaves the line.
final class WorkQueue: @unchecked Sendable {
    static let shared = WorkQueue()

    /// True inside a task that already owns a slot, so nested `produce` calls never deadlock.
    @TaskLocal static var isHoldingSlot = false

    private struct Waiter {
        let id: UUID
        let continuation: CheckedContinuation<Void, Error>
    }

    private let lock = NSLock()
    private var running = 0
    private var waiters: [Waiter] = []

    /// 0 in the preferences means "automatic": a quarter of the cores, between 1 and 3.
    var limit: Int {
        let chosen = UserDefaults.standard.integer(forKey: "maxJobs")
        if chosen > 0 { return chosen }
        return limitForAuto
    }

    var limitForAuto: Int { max(1, min(3, ProcessInfo.processInfo.activeProcessorCount / 4)) }

    var isFull: Bool { lock.withLock { running >= limit } }
    var activeCount: Int { lock.withLock { running } }
    var waitingCount: Int { lock.withLock { waiters.count } }

    func acquire() async throws {
        let id = UUID()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                let granted: Bool = lock.withLock {
                    if running < limit {
                        running += 1
                        return true
                    }
                    waiters.append(Waiter(id: id, continuation: continuation))
                    return false
                }
                if granted { continuation.resume() }
            }
        } onCancel: {
            let waiter: Waiter? = lock.withLock {
                guard let index = waiters.firstIndex(where: { $0.id == id }) else { return nil }
                return waiters.remove(at: index)
            }
            waiter?.continuation.resume(throwing: CancellationError())
        }
    }

    func release() {
        let next: Waiter? = lock.withLock {
            if !waiters.isEmpty, running <= limit { return waiters.removeFirst() }
            running = max(0, running - 1)
            return nil
        }
        // The slot moves straight to the next waiter (running stays the same).
        next?.continuation.resume()
    }
}
