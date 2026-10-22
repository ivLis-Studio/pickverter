import AppKit
import SwiftUI

extension Color {
    init(hex: UInt32, opacity: Double = 1) {
        self.init(.sRGB, red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255, opacity: opacity)
    }
}

/// Flat, system-native styling: the user's accent color for selection, neutral fills everywhere else.
enum Theme {
    static let accent = Color.accentColor

    static func tileFill(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.07) : Color.black.opacity(0.045)
    }

    static func hairline(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.14) : Color.black.opacity(0.1)
    }

    static func selectedTab(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.17) : Color.white
    }
}

// MARK: - Glass

private struct SnapshotRenderingKey: EnvironmentKey { static let defaultValue = false }
private struct GlassInWindowKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    /// ImageRenderer cannot draw NSVisualEffectView; snapshots use a flat fill instead.
    var snapshotRendering: Bool {
        get { self[SnapshotRenderingKey.self] }
        set { self[SnapshotRenderingKey.self] = newValue }
    }

    /// Blur window content instead of the desktop (used inside regular windows).
    var glassWithinWindow: Bool {
        get { self[GlassInWindowKey.self] }
        set { self[GlassInWindowKey.self] = newValue }
    }
}

enum GlassShape: Equatable {
    case circle
    case capsule
    case rounded(CGFloat)
    /// Annular band in SwiftUI degrees (0° = 3 o'clock, clockwise), centered in the view.
    case ring(start: Double, end: Double, inner: CGFloat, outer: CGFloat)

    func path(in rect: CGRect) -> CGPath {
        switch self {
        case .circle:
            return CGPath(ellipseIn: rect, transform: nil)
        case .capsule:
            let radius = min(rect.width, rect.height) / 2
            return CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)
        case .rounded(let radius):
            return Path(roundedRect: rect, cornerRadius: radius, style: .continuous).cgPath
        case .ring(let start, let end, let inner, let outer):
            return RingSegment(startDegrees: start, endDegrees: end, inner: inner, outer: outer, gap: 0, corner: 10)
                .path(in: rect).cgPath
        }
    }
}

final class MaskedEffectView: NSVisualEffectView {
    var shape: GlassShape = .circle {
        didSet {
            guard shape != oldValue else { return }
            maskedSize = .zero
            needsLayout = true
        }
    }
    private var maskedSize: CGSize = .zero

    override func layout() {
        super.layout()
        let size = bounds.size
        guard size.width > 0, size.height > 0, size != maskedSize else { return }
        maskedSize = size
        let path = shape.path(in: CGRect(origin: .zero, size: size))
        maskImage = NSImage(size: size, flipped: true) { _ in
            NSColor.black.setFill()
            NSBezierPath(cgPath: path).fill()
            return true
        }
    }
}

struct VisualEffect: NSViewRepresentable {
    var shape: GlassShape
    var material: NSVisualEffectView.Material
    var withinWindow: Bool

    func makeNSView(context: Context) -> MaskedEffectView {
        let view = MaskedEffectView()
        view.state = .active
        return view
    }

    func updateNSView(_ view: MaskedEffectView, context: Context) {
        view.material = material
        view.blendingMode = withinWindow ? .withinWindow : .behindWindow
        view.shape = shape
    }
}

struct GlassSurface: View {
    var shape: GlassShape
    var material: NSVisualEffectView.Material = .popover
    @Environment(\.snapshotRendering) private var snapshot
    @Environment(\.glassWithinWindow) private var withinWindow
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        if snapshot {
            GlassFill(shape: shape)
                .fill(scheme == .dark ? Color(hex: 0x2B2B2E, opacity: 0.97) : Color(hex: 0xF2F2F4, opacity: 0.97))
        } else {
            VisualEffect(shape: shape, material: material, withinWindow: withinWindow)
        }
    }
}

/// SwiftUI shape twin of GlassShape (for flat fills and borders).
struct GlassFill: Shape {
    let shape: GlassShape
    func path(in rect: CGRect) -> Path { Path(shape.path(in: rect)) }
}

struct KeyCap: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .medium))
            .padding(.horizontal, 6)
            .frame(minWidth: 22, minHeight: 20)
            .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(Color.primary.opacity(0.06)))
            .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous).strokeBorder(Color.primary.opacity(0.14), lineWidth: 0.5))
    }
}
