import AVFoundation
import AVKit
import SwiftUI

struct TrimSelection: Codable, Equatable {
    var start: Double
    var end: Double
    /// Stream copy: instant and lossless, cuts land on the nearest keyframe.
    var lossless: Bool
}

@MainActor
final class TrimModel: ObservableObject {
    @Published var lossless = false
    @Published var start: Double = 0
    @Published var end: Double
    @Published var useSliders: Bool
    let duration: Double

    init(duration: Double, playable: Bool) {
        self.duration = max(duration, 0.1)
        end = max(duration, 0.1)
        useSliders = !playable
    }
}

@MainActor
enum TrimEditor {
    static func pick(_ url: URL, duration: Double, isVideo: Bool) async throws -> TrimSelection {
        if let value: TrimSelection = try EditorHost.injected("trim") { return value }
        let playable = (try? await AVURLAsset(url: url).load(.isPlayable)) ?? false
        let size = isVideo && playable ? CGSize(width: 780, height: 560) : CGSize(width: 640, height: playable ? 300 : 250)
        return try await EditorHost.run("trim", title: L("editor.trim.title", url.lastPathComponent), size: size) { finish in
            TrimEditorView(url: url, model: TrimModel(duration: duration, playable: playable), finish: finish)
        }
    }
}

struct TrimEditorView: View {
    let url: URL
    @StateObject var model: TrimModel
    let finish: (TrimSelection?) -> Void

    init(url: URL, model: TrimModel, finish: @escaping (TrimSelection?) -> Void) {
        self.url = url
        _model = StateObject(wrappedValue: model)
        self.finish = finish
    }

    var body: some View {
        VStack(spacing: 0) {
            if model.useSliders {
                RangeTrimView(model: model)
                    .padding(.horizontal, 22)
                    .padding(.vertical, 18)
                Spacer(minLength: 0)
            } else {
                TrimPlayerView(url: url, onDone: { range in
                    guard let range else { return finish(nil) }
                    finish(TrimSelection(start: range.0, end: range.1, lossless: model.lossless))
                }, onUnsupported: { model.useSliders = true })
            }
            if model.useSliders {
                EditorFooter(primary: L("trim.apply"), primaryEnabled: model.end - model.start >= 0.05,
                             onCancel: { finish(nil) },
                             onPrimary: { finish(TrimSelection(start: model.start, end: model.end, lossless: model.lossless)) }) {
                    modePicker
                }
            } else {
                VStack(spacing: 0) {
                    EditorDivider()
                    HStack(spacing: 12) {
                        modePicker
                        Spacer(minLength: 8)
                        Text(L("trim.hint")).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 11)
                }
                .background(Color(nsColor: .windowBackgroundColor))
            }
        }
    }

    private var modePicker: some View {
        HStack(spacing: 10) {
            Picker("", selection: $model.lossless) {
                Text(L("trim.precise")).tag(false)
                Text(L("trim.fast")).tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            Text(model.lossless ? L("trim.fast.d") : L("trim.precise.d"))
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }
}

/// QuickTime's own trimming bar (yellow handles, frame-accurate scrubbing).
struct TrimPlayerView: NSViewRepresentable {
    let url: URL
    let onDone: ((Double, Double)?) -> Void
    let onUnsupported: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .inline
        view.showsFullScreenToggleButton = false
        view.player = AVPlayer(url: url)
        context.coordinator.begin(view, onDone: onDone, onUnsupported: onUnsupported)
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {}

    static func dismantleNSView(_ view: AVPlayerView, coordinator: Coordinator) {
        coordinator.cancel()
        view.player?.pause()
        view.player = nil
    }

    @MainActor
    final class Coordinator {
        private var task: Task<Void, Never>?

        func begin(_ view: AVPlayerView, onDone: @escaping ((Double, Double)?) -> Void, onUnsupported: @escaping () -> Void) {
            task = Task { @MainActor [weak view] in
                for _ in 0..<120 {
                    guard let view, !Task.isCancelled else { return }
                    let status = view.player?.currentItem?.status
                    if status == .failed { onUnsupported(); return }
                    if view.window != nil, status == .readyToPlay { break }
                    try? await Task.sleep(nanoseconds: 100_000_000)
                }
                guard let view, !Task.isCancelled else { return }
                guard view.canBeginTrimming else { onUnsupported(); return }
                view.window?.makeFirstResponder(view)
                let result = await view.beginTrimming()
                guard result == .okButton, let item = view.player?.currentItem else { onDone(nil); return }
                let total = item.duration.isNumeric ? item.duration.seconds : 0
                let start = item.reversePlaybackEndTime.isNumeric ? item.reversePlaybackEndTime.seconds : 0
                let end = item.forwardPlaybackEndTime.isNumeric ? item.forwardPlaybackEndTime.seconds : total
                onDone((max(0, start), end > start ? end : total))
            }
        }

        func cancel() { task?.cancel() }
    }
}

/// Fallback for containers AVFoundation can't play (MKV, WebM, Ogg…): two handles and exact times.
struct RangeTrimView: View {
    @ObservedObject var model: TrimModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            TrimBar(model: model).frame(height: 34)
            HStack(spacing: 24) {
                TimeField(title: L("trim.start"), value: $model.start, range: 0...model.duration)
                TimeField(title: L("trim.end"), value: $model.end, range: 0...model.duration)
                Spacer()
                VStack(alignment: .trailing, spacing: 2) {
                    Text(L("trim.length")).font(.system(size: 11)).foregroundStyle(.secondary)
                    Text(TimeText.format(max(0, model.end - model.start)))
                        .font(.system(size: 15, weight: .semibold).monospacedDigit())
                }
            }
            Text(L("trim.fallback")).font(.system(size: 11)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .onChange(of: model.start) { _, value in
            if value > model.end - 0.05 { model.end = min(model.duration, value + 0.05) }
        }
        .onChange(of: model.end) { _, value in
            if value < model.start + 0.05 { model.start = max(0, value - 0.05) }
        }
    }
}

/// Flat range bar: the kept part is filled with the accent color, handles are plain capsules.
struct TrimBar: View {
    @ObservedObject var model: TrimModel
    @State private var dragging: Int?

    var body: some View {
        GeometryReader { proxy in
            let width = proxy.size.width
            let x0 = CGFloat(model.start / model.duration) * width
            let x1 = CGFloat(model.end / model.duration) * width
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.primary.opacity(0.08))
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(Theme.accent.opacity(0.85))
                    .frame(width: max(4, x1 - x0))
                    .offset(x: x0)
                ForEach([0, 1], id: \.self) { index in
                    Capsule()
                        .fill(Color.white)
                        .overlay(Capsule().strokeBorder(Color.black.opacity(0.18), lineWidth: 0.5))
                        .frame(width: 8, height: proxy.size.height + 6)
                        .offset(x: (index == 0 ? x0 : x1) - 4)
                }
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onChanged { value in
                let time = Double(min(max(0, value.location.x / max(width, 1)), 1)) * model.duration
                if dragging == nil { dragging = abs(value.startLocation.x - x0) <= abs(value.startLocation.x - x1) ? 0 : 1 }
                if dragging == 0 { model.start = min(time, model.end - 0.05) } else { model.end = max(time, model.start + 0.05) }
            }.onEnded { _ in dragging = nil })
        }
    }
}

struct TimeField: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    @State private var text = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(.system(size: 11)).foregroundStyle(.secondary)
            TextField("0:00.0", text: $text)
                .font(.system(size: 13).monospacedDigit())
                .frame(width: 96)
                .textFieldStyle(.roundedBorder)
                .onSubmit(commit)
        }
        .onAppear { text = TimeText.format(value) }
        .onChange(of: value) { _, newValue in text = TimeText.format(newValue) }
    }

    private func commit() {
        if let seconds = DataEngine.parseTime(text.contains(":") ? text : "0:" + text) {
            value = min(max(seconds, range.lowerBound), range.upperBound)
        }
        text = TimeText.format(value)
    }
}
