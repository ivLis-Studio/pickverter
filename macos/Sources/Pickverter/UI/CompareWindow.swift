import AppKit
import SwiftUI

/// Before and after of a picture result: both fitted to the same frame, a divider to drag or click (← → move it),
/// size and pixel dimensions in the corners, a checkerboard behind transparency. Esc closes.
@MainActor
enum CompareWindow {
    private static var open: [ObjectIdentifier: EditorWindowController] = [:]

    /// One picture in, one picture out (a result waiting in the clipboard cache counts).
    static func canCompare(source: URL, output: URL) -> Bool {
        FileKind.of(source) == .image && FileKind.of(output) == .image && FileManager.default.fileExists(atPath: output.path)
    }

    static func show(before: URL, after: URL) {
        guard let model = CompareModel(before: before, after: after) else {
            NSSound.beep()
            return
        }
        let controller = EditorWindowController()
        let key = ObjectIdentifier(controller)
        open[key] = controller
        controller.onClose = { open[key] = nil }
        let view = CompareView(model: model) {
            controller.dismiss()
            open[key] = nil
        }
        controller.show(title: L("compare.title"), size: windowSize(for: model.frameAspect), minSize: CGSize(width: 420, height: 340),
                        root: AnyView(view))
        controller.window?.subtitle = after.lastPathComponent
    }

    /// The pictures' shape at a comfortable size (the window never outgrows the screen).
    static func windowSize(for aspect: CGFloat) -> CGSize {
        let chrome: CGFloat = 76
        let maxWidth: CGFloat = 1000, maxHeight: CGFloat = 720
        var width = maxWidth
        var height = width / max(aspect, 0.1)
        if height > maxHeight {
            height = maxHeight
            width = height * aspect
        }
        return CGSize(width: max(width, 420) + 32, height: max(height, 260) + chrome)
    }
}

@MainActor
final class CompareModel: ObservableObject {
    struct Side {
        let image: NSImage
        let pixels: CGSize
        let caption: String
        let opaque: Bool
    }

    let before: Side
    let after: Side
    /// 0 = all "after", 1 = all "before": the divider's position across the frame.
    @Published var position: CGFloat = 0.5

    init?(before beforeURL: URL, after afterURL: URL) {
        guard let before = Self.side(beforeURL, label: L("compare.before")),
              let after = Self.side(afterURL, label: L("compare.after")) else { return nil }
        self.before = before
        self.after = after
    }

    /// The frame both pictures fit into: the original's shape.
    var frameAspect: CGFloat { before.pixels.width / max(before.pixels.height, 1) }

    func nudge(_ step: CGFloat) {
        position = min(1, max(0, position + step))
    }

    private static func side(_ url: URL, label: String) -> Side? {
        guard let loaded = try? ImageEngine.load(url, maxPixel: 2400) else { return nil }
        let pixels = ImageEngine.orientedPixelSize(url) ?? CGSize(width: loaded.image.width, height: loaded.image.height)
        let caption = [label, FileNames.formatBytes(FileNames.size(of: url)), "\(Int(pixels.width)) × \(Int(pixels.height))"]
            .joined(separator: " · ")
        let image = NSImage(cgImage: loaded.image, size: CGSize(width: loaded.image.width, height: loaded.image.height))
        return Side(image: image, pixels: pixels, caption: caption, opaque: !ImageEngine.usesAlpha(loaded.image))
    }
}

struct CompareView: View {
    @ObservedObject var model: CompareModel
    let close: () -> Void
    @FocusState private var focused: Bool
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: 0) {
            GeometryReader { proxy in
                let frame = fitted(model.frameAspect, in: proxy.size)
                ZStack(alignment: .topLeading) {
                    stage(frame)
                }
                .frame(width: proxy.size.width, height: proxy.size.height)
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0).onChanged { value in
                    model.position = min(1, max(0, (value.location.x - frame.minX) / max(frame.width, 1)))
                })
            }
            .padding(16)
            Divider()
            HStack(spacing: 8) {
                Text(L("compare.hint"))
                Text("← →  ·  esc").foregroundStyle(.tertiary)
            }
            .font(.system(size: 11.5))
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .minimumScaleFactor(0.8)
            .frame(maxWidth: .infinity)
            .frame(height: 40)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onAppear { focused = true }
        .onMoveCommand { direction in
            switch direction {
            case .left: model.nudge(-0.05)
            case .right: model.nudge(0.05)
            default: break
            }
        }
        .onExitCommand(perform: close)
    }

    @ViewBuilder
    private func stage(_ frame: CGRect) -> some View {
        let split = frame.minX + frame.width * model.position
        ZStack(alignment: .topLeading) {
            if !(model.before.opaque && model.after.opaque) {
                Checkerboard(dark: scheme == .dark)
                    .frame(width: frame.width, height: frame.height)
                    .offset(x: frame.minX, y: frame.minY)
            }
            picture(model.after, in: frame)
            picture(model.before, in: frame)
                .mask(alignment: .topLeading) {
                    Rectangle().frame(width: max(0, split - frame.minX), height: frame.height)
                        .offset(x: frame.minX, y: frame.minY)
                }
            Rectangle()
                .fill(Color.white)
                .frame(width: 2, height: frame.height)
                .shadow(color: .black.opacity(0.35), radius: 1.5)
                .offset(x: split - 1, y: frame.minY)
            Circle()
                .fill(Color.white)
                .frame(width: 30, height: 30)
                .shadow(color: .black.opacity(0.3), radius: 2, y: 1)
                .overlay(Image(systemName: "arrow.left.and.right").font(.system(size: 12, weight: .semibold)).foregroundStyle(.black.opacity(0.7)))
                .offset(x: split - 15, y: frame.midY - 15)
            caption(model.before.caption)
                .offset(x: frame.minX + 10, y: frame.minY + 10)
            caption(model.after.caption)
                .frame(width: max(0, frame.width - 20), alignment: .trailing)
                .offset(x: frame.minX + 10, y: frame.minY + 10)
        }
    }

    private func picture(_ side: CompareModel.Side, in frame: CGRect) -> some View {
        Image(nsImage: side.image)
            .resizable()
            .interpolation(.high)
            .aspectRatio(contentMode: .fit)
            .frame(width: frame.width, height: frame.height)
            .offset(x: frame.minX, y: frame.minY)
    }

    private func caption(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 11, weight: .medium))
            .monospacedDigit()
            .foregroundStyle(.white)
            .lineLimit(1)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(Capsule().fill(Color.black.opacity(0.6)))
    }

    private func fitted(_ aspect: CGFloat, in size: CGSize) -> CGRect {
        guard size.width > 0, size.height > 0 else { return .zero }
        var width = size.width
        var height = width / max(aspect, 0.01)
        if height > size.height {
            height = size.height
            width = height * aspect
        }
        return CGRect(x: (size.width - width) / 2, y: (size.height - height) / 2, width: width, height: height)
    }
}

/// Grey squares behind see-through pixels.
struct Checkerboard: View {
    let dark: Bool

    var body: some View {
        Canvas { context, size in
            let side: CGFloat = 8
            let light = dark ? Color(white: 0.24) : Color(white: 0.96)
            let shade = dark ? Color(white: 0.18) : Color(white: 0.86)
            context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(light))
            var row = 0
            var y: CGFloat = 0
            while y < size.height {
                var x: CGFloat = row % 2 == 0 ? 0 : side
                while x < size.width {
                    context.fill(Path(CGRect(x: x, y: y, width: side, height: side)), with: .color(shade))
                    x += side * 2
                }
                y += side
                row += 1
            }
        }
    }
}
