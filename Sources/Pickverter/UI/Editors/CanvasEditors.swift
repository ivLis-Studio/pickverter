import AppKit
import SwiftUI

/// Rectangle in normalized image coordinates (0…1, origin top-left), independent of preview scale.
struct NormRect: Codable, Equatable, Hashable {
    var x: Double
    var y: Double
    var w: Double
    var h: Double

    static let full = NormRect(x: 0, y: 0, w: 1, h: 1)

    var maxX: Double { x + w }
    var maxY: Double { y + h }
    var isFull: Bool { x < 0.0005 && y < 0.0005 && w > 0.999 && h > 0.999 }

    static func between(_ a: CGPoint, _ b: CGPoint) -> NormRect {
        NormRect(x: Double(min(a.x, b.x)), y: Double(min(a.y, b.y)), w: Double(abs(a.x - b.x)), h: Double(abs(a.y - b.y)))
    }

    func clamped() -> NormRect {
        var r = self
        r.w = min(max(r.w, 0), 1)
        r.h = min(max(r.h, 0), 1)
        r.x = min(max(r.x, 0), 1 - r.w)
        r.y = min(max(r.y, 0), 1 - r.h)
        return r
    }

    /// Whole-pixel rect (top-left origin) inside an image of `size`; `even` keeps video encoders happy.
    func pixels(in size: CGSize, even: Bool = false) -> CGRect {
        var px = CGRect(x: x * size.width, y: y * size.height, width: w * size.width, height: h * size.height)
        px = CGRect(x: px.minX.rounded(), y: px.minY.rounded(), width: px.width.rounded(), height: px.height.rounded())
            .intersection(CGRect(origin: .zero, size: size))
        if even {
            px.origin.x = (px.minX / 2).rounded(.down) * 2
            px.origin.y = (px.minY / 2).rounded(.down) * 2
            px.size.width = max(2, (px.width / 2).rounded(.down) * 2)
            px.size.height = max(2, (px.height / 2).rounded(.down) * 2)
        }
        return px
    }

    func view(in frame: CGRect) -> CGRect {
        CGRect(x: frame.minX + x * frame.width, y: frame.minY + y * frame.height, width: w * frame.width, height: h * frame.height)
    }

    func expanded(by fraction: Double) -> NormRect {
        NormRect(x: x - w * fraction, y: y - h * fraction, w: w * (1 + 2 * fraction), h: h * (1 + 2 * fraction)).clipped()
    }

    /// Intersected with the unit square (unlike `clamped`, which slides the rect inside).
    func clipped() -> NormRect {
        let x0 = max(0, x), y0 = max(0, y), x1 = min(1, maxX), y1 = min(1, maxY)
        return NormRect(x: x0, y: y0, w: max(0, x1 - x0), h: max(0, y1 - y0))
    }

    func overlap(_ other: NormRect) -> Double {
        let iw = max(0, min(maxX, other.maxX) - max(x, other.x))
        let ih = max(0, min(maxY, other.maxY) - max(y, other.y))
        let inter = iw * ih
        let union = w * h + other.w * other.h - inter
        return union > 0 ? inter / union : 0
    }

    /// Vision boxes are normalized with a bottom-left origin.
    static func vision(_ box: CGRect) -> NormRect {
        NormRect(x: Double(box.minX), y: Double(1 - box.maxY), w: Double(box.width), h: Double(box.height))
    }
}

struct CropSelection: Codable, Equatable {
    var rect: NormRect
}

enum RedactStyle: String, Codable, CaseIterable, Identifiable {
    case pixelate, blur, fill
    var id: String { rawValue }
    var label: String { L("redact." + rawValue) }
}

struct RedactSelection: Codable, Equatable {
    var regions: [NormRect]
    var style: RedactStyle
}

enum CropAspect: String, CaseIterable, Identifiable {
    case free, original, square, r4x3, r3x2, r16x9, r9x16, r4x5

    var id: String { rawValue }

    var label: String {
        switch self {
        case .free: return L("crop.free")
        case .original: return L("crop.original")
        case .square: return "1:1"
        case .r4x3: return "4:3"
        case .r3x2: return "3:2"
        case .r16x9: return "16:9"
        case .r9x16: return "9:16"
        case .r4x5: return "4:5"
        }
    }

    /// Width / height in pixels.
    func ratio(for size: CGSize) -> Double? {
        switch self {
        case .free: return nil
        case .original: return size.height > 0 ? Double(size.width / size.height) : nil
        case .square: return 1
        case .r4x3: return 4.0 / 3
        case .r3x2: return 1.5
        case .r16x9: return 16.0 / 9
        case .r9x16: return 9.0 / 16
        case .r4x5: return 0.8
        }
    }
}

/// What the editors draw: a downsampled, upright preview plus the real output size.
struct CanvasSource: @unchecked Sendable {
    let preview: CGImage
    let pixelSize: CGSize
    let isVideo: Bool

    static func load(_ url: URL) async throws -> CanvasSource {
        if FileKind.of(url) == .video {
            let frame = try await MediaEngine.previewFrame(url, maxSide: 1600)
            let size = await MediaEngine.displaySize(url) ?? CGSize(width: frame.width, height: frame.height)
            return CanvasSource(preview: frame, pixelSize: size, isVideo: true)
        }
        return try await Task.detached(priority: .userInitiated) { () throws -> CanvasSource in
            let loaded = try ImageEngine.load(url, maxPixel: 1600)
            let full = ImageEngine.orientedPixelSize(url) ?? CGSize(width: loaded.image.width, height: loaded.image.height)
            return CanvasSource(preview: loaded.image, pixelSize: full, isVideo: false)
        }.value
    }
}

enum CanvasGeometry {
    enum Target: Equatable { case move, create, corner(Int), edge(Int) }

    static func fit(_ content: CGSize, in bounds: CGRect) -> CGRect {
        guard content.width > 0, content.height > 0, bounds.width > 0, bounds.height > 0 else { return bounds }
        let scale = min(bounds.width / content.width, bounds.height / content.height)
        let size = CGSize(width: content.width * scale, height: content.height * scale)
        return CGRect(x: bounds.midX - size.width / 2, y: bounds.midY - size.height / 2, width: size.width, height: size.height)
    }

    /// View point -> normalized point, clamped to the image.
    static func normalize(_ point: CGPoint, in frame: CGRect) -> CGPoint {
        CGPoint(x: min(max((point.x - frame.minX) / max(frame.width, 1), 0), 1),
                y: min(max((point.y - frame.minY) / max(frame.height, 1), 0), 1))
    }

    /// 0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left.
    static func corners(_ rect: CGRect) -> [CGPoint] {
        [CGPoint(x: rect.minX, y: rect.minY), CGPoint(x: rect.maxX, y: rect.minY),
         CGPoint(x: rect.maxX, y: rect.maxY), CGPoint(x: rect.minX, y: rect.maxY)]
    }

    static func corner(_ r: NormRect, _ index: Int) -> CGPoint {
        [CGPoint(x: r.x, y: r.y), CGPoint(x: r.maxX, y: r.y), CGPoint(x: r.maxX, y: r.maxY), CGPoint(x: r.x, y: r.maxY)][index]
    }

    static func hit(_ point: CGPoint, _ rect: CGRect, edges: Bool = true) -> Target? {
        for (index, corner) in corners(rect).enumerated() where hypot(point.x - corner.x, point.y - corner.y) < 13 {
            return .corner(index)
        }
        if edges {
            let insideX = point.x > rect.minX && point.x < rect.maxX
            let insideY = point.y > rect.minY && point.y < rect.maxY
            if insideX, abs(point.y - rect.minY) < 8 { return .edge(0) }
            if insideY, abs(point.x - rect.maxX) < 8 { return .edge(1) }
            if insideX, abs(point.y - rect.maxY) < 8 { return .edge(2) }
            if insideY, abs(point.x - rect.minX) < 8 { return .edge(3) }
        }
        return rect.contains(point) ? .move : nil
    }

    static func cursor(for target: Target?) -> NSCursor {
        switch target {
        case .move: return .openHand
        case .corner, .create: return .crosshair
        case .edge(let side): return side % 2 == 0 ? .resizeUpDown : .resizeLeftRight
        case nil: return .crosshair
        }
    }

    /// Rect spanned from a fixed anchor to the pointer; `ratio` is normalized width / height.
    static func span(from anchor: CGPoint, to point: CGPoint, ratio: Double?, minimum: Double = 0.01) -> NormRect {
        let ax = Double(anchor.x), ay = Double(anchor.y)
        let dx = Double(point.x) - ax, dy = Double(point.y) - ay
        let sx: Double = dx < 0 ? -1 : 1, sy: Double = dy < 0 ? -1 : 1
        let roomX = sx > 0 ? 1 - ax : ax, roomY = sy > 0 ? 1 - ay : ay
        var w = min(max(abs(dx), minimum), roomX)
        var h = min(max(abs(dy), minimum), roomY)
        if let ratio, ratio > 0 {
            let side = max(w, h * ratio)
            w = side
            h = side / ratio
            if w > roomX { w = roomX; h = w / ratio }
            if h > roomY { h = roomY; w = h * ratio }
        }
        return NormRect(x: sx > 0 ? ax : ax - w, y: sy > 0 ? ay : ay - h, w: w, h: h)
    }

    static func edge(_ side: Int, of origin: NormRect, to point: CGPoint, ratio: Double?, minimum: Double = 0.01) -> NormRect {
        var r = origin
        let px = Double(point.x), py = Double(point.y)
        switch side {
        case 0: r.y = min(py, origin.maxY - minimum); r.h = origin.maxY - r.y
        case 1: r.w = max(px - origin.x, minimum)
        case 2: r.h = max(py - origin.y, minimum)
        default: r.x = min(px, origin.maxX - minimum); r.w = origin.maxX - r.x
        }
        guard let ratio, ratio > 0 else { return r.clipped() }
        if side % 2 == 0 {
            var w = r.h * ratio
            if w > 1 { w = 1; r.h = w / ratio; if side == 0 { r.y = origin.maxY - r.h } }
            r.x = origin.x + origin.w / 2 - w / 2
            r.w = w
        } else {
            var h = r.w / ratio
            if h > 1 { h = 1; r.w = h * ratio; if side == 3 { r.x = origin.maxX - r.w } }
            r.y = origin.y + origin.h / 2 - h / 2
            r.h = h
        }
        return r.clamped()
    }

    static func moved(_ origin: NormRect, from start: CGPoint, to point: CGPoint) -> NormRect {
        var r = origin
        r.x = origin.x + Double(point.x - start.x)
        r.y = origin.y + Double(point.y - start.y)
        return r.clamped()
    }
}

enum CanvasDrawing {
    static func dim(_ context: inout GraphicsContext, frame: CGRect, hole: CGRect) {
        var path = Path(frame)
        path.addRect(hole)
        context.fill(path, with: .color(.black.opacity(0.52)), style: FillStyle(eoFill: true))
    }

    static func handles(_ context: inout GraphicsContext, rect: CGRect) {
        for point in CanvasGeometry.corners(rect) {
            let box = Path(roundedRect: CGRect(x: point.x - 5, y: point.y - 5, width: 10, height: 10), cornerRadius: 2.5)
            context.fill(box, with: .color(.white))
            context.stroke(box, with: .color(.black.opacity(0.35)), lineWidth: 0.5)
        }
    }

    static func thirds(_ context: inout GraphicsContext, rect: CGRect) {
        var lines = Path()
        for step in 1...2 {
            let x = rect.minX + rect.width * CGFloat(step) / 3
            let y = rect.minY + rect.height * CGFloat(step) / 3
            lines.move(to: CGPoint(x: x, y: rect.minY)); lines.addLine(to: CGPoint(x: x, y: rect.maxY))
            lines.move(to: CGPoint(x: rect.minX, y: y)); lines.addLine(to: CGPoint(x: rect.maxX, y: y))
        }
        context.stroke(lines, with: .color(.white.opacity(0.45)), lineWidth: 0.5)
    }
}

@MainActor
enum CanvasEditors {
    static func windowSize(for source: CanvasSource, extra: CGFloat) -> CGSize {
        let maxCanvas = CGSize(width: 980, height: 640)
        let scale = min(maxCanvas.width / max(source.pixelSize.width, 1), maxCanvas.height / max(source.pixelSize.height, 1), 1)
        let canvas = CGSize(width: max(560, source.pixelSize.width * scale + 48), height: max(380, source.pixelSize.height * scale + 40))
        return CGSize(width: canvas.width, height: canvas.height + extra)
    }

    static func crop(_ url: URL, count: Int) async throws -> CropSelection {
        if let value: CropSelection = try EditorHost.injected("crop") { return value }
        let source = try await CanvasSource.load(url)
        return try await EditorHost.run("crop", title: L("editor.crop.title", url.lastPathComponent),
                                        size: windowSize(for: source, extra: 58)) { finish in
            CropEditorView(source: source, count: count, finish: finish)
        }
    }

    static func redact(_ url: URL, count: Int) async throws -> RedactSelection {
        if let value: RedactSelection = try EditorHost.injected("redact") { return value }
        let source = try await CanvasSource.load(url)
        return try await EditorHost.run("redact", title: L("editor.redact.title", url.lastPathComponent),
                                        size: windowSize(for: source, extra: 104)) { finish in
            RedactEditorView(source: source, count: count, finish: finish)
        }
    }
}

// MARK: - Crop

struct CropEditorView: View {
    let source: CanvasSource
    let count: Int
    let finish: (CropSelection?) -> Void
    @State private var rect = NormRect.full
    @State private var aspect: CropAspect = .free
    @State private var target: CanvasGeometry.Target?
    @State private var origin = NormRect.full
    @State private var dragging = false

    private var ratio: Double? {
        aspect.ratio(for: source.pixelSize).map { $0 * Double(source.pixelSize.height / max(source.pixelSize.width, 1)) }
    }

    var body: some View {
        VStack(spacing: 0) {
            GeometryReader { proxy in
                let frame = CanvasGeometry.fit(source.pixelSize, in: CGRect(origin: .zero, size: proxy.size).insetBy(dx: 24, dy: 20))
                ZStack(alignment: .topLeading) {
                    Color(nsColor: .underPageBackgroundColor)
                    Image(decorative: source.preview, scale: 1)
                        .resizable()
                        .interpolation(.high)
                        .frame(width: frame.width, height: frame.height)
                        .offset(x: frame.minX, y: frame.minY)
                    Canvas { context, _ in
                        let hole = rect.view(in: frame)
                        CanvasDrawing.dim(&context, frame: frame, hole: hole)
                        if dragging { CanvasDrawing.thirds(&context, rect: hole) }
                        context.stroke(Path(hole), with: .color(.white), lineWidth: 1.5)
                        CanvasDrawing.handles(&context, rect: hole)
                    }
                    .allowsHitTesting(false)
                }
                .contentShape(Rectangle())
                .gesture(drag(in: frame))
                .onContinuousHover { phase in
                    switch phase {
                    case .active(let point): CanvasGeometry.cursor(for: CanvasGeometry.hit(point, rect.view(in: frame))).set()
                    case .ended: NSCursor.arrow.set()
                    }
                }
            }
            EditorFooter(primary: L("crop.apply"), primaryEnabled: !rect.isFull,
                         onCancel: { finish(nil) }, onPrimary: { finish(CropSelection(rect: rect)) }) {
                Picker(L("crop.aspect"), selection: $aspect) {
                    ForEach(CropAspect.allCases) { Text($0.label).tag($0) }
                }
                .fixedSize()
                Button(L("crop.reset")) {
                    aspect = .free
                    rect = .full
                }
                .disabled(rect.isFull && aspect == .free)
                Text(sizeLabel).font(.system(size: 11).monospacedDigit()).foregroundStyle(.secondary)
                if count > 1 {
                    Text(LP("editor.applies_to", count)).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .onChange(of: aspect) { _, _ in fitAspect() }
    }

    private var sizeLabel: String {
        let px = rect.pixels(in: source.pixelSize)
        return "\(Int(px.width)) × \(Int(px.height))"
    }

    private func fitAspect() {
        guard let ratio else { return }
        var w = 1.0, h = 1.0 / ratio
        if h > 1 { h = 1; w = ratio }
        let cx = rect.x + rect.w / 2, cy = rect.y + rect.h / 2
        rect = NormRect(x: cx - w / 2, y: cy - h / 2, w: w, h: h).clamped()
    }

    private func drag(in frame: CGRect) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                let start = CanvasGeometry.normalize(value.startLocation, in: frame)
                let point = CanvasGeometry.normalize(value.location, in: frame)
                if !dragging {
                    dragging = true
                    origin = rect
                    target = CanvasGeometry.hit(value.startLocation, rect.view(in: frame)) ?? .create
                }
                switch target {
                case .move:
                    rect = CanvasGeometry.moved(origin, from: start, to: point)
                case .corner(let index):
                    rect = CanvasGeometry.span(from: CanvasGeometry.corner(origin, (index + 2) % 4), to: point, ratio: ratio)
                case .edge(let side):
                    rect = CanvasGeometry.edge(side, of: origin, to: point, ratio: ratio)
                case .create, nil:
                    if hypot(value.location.x - value.startLocation.x, value.location.y - value.startLocation.y) > 4 {
                        rect = CanvasGeometry.span(from: start, to: point, ratio: ratio)
                    }
                }
            }
            .onEnded { _ in
                dragging = false
                target = nil
            }
    }
}

// MARK: - Redact

struct RedactEditorView: View {
    let source: CanvasSource
    let count: Int
    let finish: (RedactSelection?) -> Void
    @State private var regions: [NormRect] = []
    @State private var selected: Int?
    @State private var style: RedactStyle = .pixelate
    @State private var preview: CGImage
    @State private var draft: NormRect?
    @State private var target: CanvasGeometry.Target?
    @State private var origin = NormRect.full
    @State private var dragging = false
    @State private var busy = false
    @State private var note: String?

    init(source: CanvasSource, count: Int, finish: @escaping (RedactSelection?) -> Void) {
        self.source = source
        self.count = count
        self.finish = finish
        _preview = State(initialValue: source.preview)
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            EditorDivider()
            GeometryReader { proxy in
                let frame = CanvasGeometry.fit(source.pixelSize, in: CGRect(origin: .zero, size: proxy.size).insetBy(dx: 24, dy: 20))
                ZStack(alignment: .topLeading) {
                    Color(nsColor: .underPageBackgroundColor)
                    Image(decorative: preview, scale: 1)
                        .resizable()
                        .interpolation(.high)
                        .frame(width: frame.width, height: frame.height)
                        .offset(x: frame.minX, y: frame.minY)
                    Canvas { context, _ in
                        for (index, region) in regions.enumerated() {
                            let box = region.view(in: frame)
                            if index == selected {
                                context.stroke(Path(box), with: .color(Theme.accent), lineWidth: 2)
                                CanvasDrawing.handles(&context, rect: box)
                            } else {
                                context.stroke(Path(box), with: .color(.white.opacity(0.9)),
                                               style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                            }
                        }
                        if let draft {
                            context.stroke(Path(draft.view(in: frame)), with: .color(Theme.accent),
                                           style: StrokeStyle(lineWidth: 1.5, dash: [5, 3]))
                        }
                    }
                    .allowsHitTesting(false)
                    if regions.isEmpty, draft == nil {
                        Text(L("redact.empty"))
                            .font(.system(size: 12, weight: .medium))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 6)
                            .background(Capsule().fill(Color.black.opacity(0.55)))
                            .position(x: frame.midX, y: frame.maxY - 24)
                            .allowsHitTesting(false)
                    }
                }
                .contentShape(Rectangle())
                .gesture(drag(in: frame))
                .onContinuousHover { phase in
                    switch phase {
                    case .active(let point):
                        let onSelected = selected.flatMap { CanvasGeometry.hit(point, regions[$0].view(in: frame), edges: false) }
                        let onAny = regions.contains { $0.view(in: frame).contains(point) }
                        CanvasGeometry.cursor(for: onSelected ?? (onAny ? .move : .create)).set()
                    case .ended:
                        NSCursor.arrow.set()
                    }
                }
            }
            EditorFooter(primary: L("redact.apply"), primaryEnabled: !regions.isEmpty,
                         onCancel: { finish(nil) }, onPrimary: { finish(RedactSelection(regions: regions, style: style)) }) {
                if let note {
                    Text(note).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                } else {
                    Text(LP("redact.count", regions.count)).font(.system(size: 11)).foregroundStyle(.secondary)
                }
                if count > 1 {
                    Text(LP("editor.applies_to", count)).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .onChange(of: regions) { _, _ in refresh() }
        .onChange(of: style) { _, _ in refresh() }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Button { detect(.faces) } label: { Label(L("redact.faces"), systemImage: "person.crop.square") }
            Button { detect(.text) } label: { Label(L("redact.text"), systemImage: "text.viewfinder") }
            if busy { ProgressView().controlSize(.small) }
            Spacer(minLength: 10)
            Picker("", selection: $style) {
                ForEach(RedactStyle.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            Button {
                if let index = selected { regions.remove(at: index); selected = nil }
            } label: { Image(systemName: "minus.square") }
            .help(L("redact.delete"))
            .keyboardShortcut(.delete, modifiers: [])
            .disabled(selected == nil)
            Button(L("redact.clear")) { regions.removeAll(); selected = nil }
                .disabled(regions.isEmpty)
        }
        .disabled(busy)
        .padding(.horizontal, 16)
        .padding(.vertical, 9)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private func refresh() {
        preview = ImageEngine.applyRedaction(source.preview, regions: regions, style: style) ?? source.preview
    }

    private func detect(_ kind: ImageEngine.Detection) {
        busy = true
        note = nil
        let image = source.preview
        Task { @MainActor in
            let found = await Task.detached(priority: .userInitiated) { ImageEngine.detect(kind, in: image) }.value
            var added = 0
            for box in found where !regions.contains(where: { $0.overlap(box) > 0.4 }) {
                regions.append(box)
                added += 1
            }
            note = added == 0 ? L(kind == .faces ? "redact.no_faces" : "redact.no_text") : LP("redact.found", added)
            busy = false
        }
    }

    private func drag(in frame: CGRect) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                let start = CanvasGeometry.normalize(value.startLocation, in: frame)
                let point = CanvasGeometry.normalize(value.location, in: frame)
                if !dragging {
                    dragging = true
                    note = nil
                    if let index = selected, let hit = CanvasGeometry.hit(value.startLocation, regions[index].view(in: frame), edges: false),
                       case .corner = hit {
                        target = hit
                        origin = regions[index]
                    } else if let index = regions.lastIndex(where: { $0.view(in: frame).contains(value.startLocation) }) {
                        selected = index
                        target = .move
                        origin = regions[index]
                    } else {
                        selected = nil
                        target = .create
                    }
                }
                switch target {
                case .move:
                    if let index = selected { regions[index] = CanvasGeometry.moved(origin, from: start, to: point) }
                case .corner(let corner):
                    if let index = selected {
                        regions[index] = CanvasGeometry.span(from: CanvasGeometry.corner(origin, (corner + 2) % 4), to: point, ratio: nil)
                    }
                default:
                    if hypot(value.location.x - value.startLocation.x, value.location.y - value.startLocation.y) > 3 {
                        draft = NormRect.between(start, point)
                    }
                }
            }
            .onEnded { _ in
                if target == .create, let draft, draft.w * frame.width > 6, draft.h * frame.height > 6 {
                    regions.append(draft)
                    selected = regions.count - 1
                }
                draft = nil
                dragging = false
                target = nil
            }
    }
}
