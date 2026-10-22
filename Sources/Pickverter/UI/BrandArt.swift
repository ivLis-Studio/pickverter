import AppKit
import SwiftUI

enum BrandArt {
    /// Template menu bar glyph: a six-slice ring with the top slice lit.
    static func menuBarIcon() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            let center = NSPoint(x: rect.midX, y: rect.midY)
            let outer: CGFloat = 8.2, inner: CGFloat = 3.9
            for index in 0..<6 {
                let mid = 90.0 - Double(index) * 60
                let start = mid - 24, end = mid + 24
                let path = NSBezierPath()
                path.appendArc(withCenter: center, radius: outer, startAngle: start, endAngle: end, clockwise: false)
                path.appendArc(withCenter: center, radius: inner, startAngle: end, endAngle: start, clockwise: true)
                path.close()
                NSColor.black.withAlphaComponent(index == 0 ? 1 : 0.42).setFill()
                path.fill()
            }
            return true
        }
        image.isTemplate = true
        return image
    }
}

/// Flat app icon: graphite tile, quiet ring, one lit slice, a document in the middle.
struct AppIconArt: View {
    var body: some View {
        GeometryReader { proxy in
            let s = proxy.size.width
            let tile = s * 0.805
            ZStack {
                RoundedRectangle(cornerRadius: tile * 0.225, style: .continuous)
                    .fill(Color(hex: 0x1C1C1F))
                    .overlay(RoundedRectangle(cornerRadius: tile * 0.225, style: .continuous)
                        .strokeBorder(Color.white.opacity(0.08), lineWidth: max(1, s * 0.004)))
                    .frame(width: tile, height: tile)
                    .shadow(color: .black.opacity(0.3), radius: s * 0.015, y: s * 0.01)
                ForEach(0..<6) { index in
                    let mid = -90.0 + Double(index) * 60
                    RingSegment(startDegrees: mid - 30, endDegrees: mid + 30, inner: s * 0.15, outer: s * 0.32,
                                gap: s * 0.024, corner: s * 0.022)
                        .fill(index == 0 ? Color(hex: 0x0A84FF) : Color.white.opacity(0.13))
                }
                DocumentGlyph()
                    .fill(Color.white)
                    .frame(width: s * 0.12, height: s * 0.155)
            }
            .frame(width: s, height: s)
        }
    }
}

struct DocumentGlyph: Shape {
    func path(in rect: CGRect) -> Path {
        let fold = rect.width * 0.34
        let r = rect.width * 0.12
        var path = Path()
        path.move(to: CGPoint(x: rect.minX + r, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX - fold, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY + fold))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY - r))
        path.addQuadCurve(to: CGPoint(x: rect.maxX - r, y: rect.maxY), control: CGPoint(x: rect.maxX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.minX + r, y: rect.maxY))
        path.addQuadCurve(to: CGPoint(x: rect.minX, y: rect.maxY - r), control: CGPoint(x: rect.minX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.minX, y: rect.minY + r))
        path.addQuadCurve(to: CGPoint(x: rect.minX + r, y: rect.minY), control: CGPoint(x: rect.minX, y: rect.minY))
        path.closeSubpath()
        var cut = Path()
        cut.move(to: CGPoint(x: rect.maxX - fold, y: rect.minY))
        cut.addLine(to: CGPoint(x: rect.maxX - fold, y: rect.minY + fold))
        cut.addLine(to: CGPoint(x: rect.maxX, y: rect.minY + fold))
        return Path(path.cgPath.subtracting(cut.strokedPath(StrokeStyle(lineWidth: rect.width * 0.07, lineJoin: .round)).cgPath))
    }
}

struct WheelGlyph: View {
    var size: CGFloat = 28
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.24, style: .continuous).fill(Color(hex: 0x1C1C1F))
            ForEach(0..<6) { index in
                let mid = -90.0 + Double(index) * 60
                RingSegment(startDegrees: mid - 30, endDegrees: mid + 30, inner: size * 0.14, outer: size * 0.36,
                            gap: size * 0.05, corner: size * 0.025)
                    .fill(index == 0 ? Color(hex: 0x0A84FF) : Color.white.opacity(0.3))
            }
        }
        .frame(width: size, height: size)
    }
}
