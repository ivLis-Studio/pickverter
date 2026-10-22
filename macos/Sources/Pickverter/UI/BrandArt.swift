import AppKit
import SwiftUI

enum BrandArt {
    /// Template menu bar glyph: the icon's eight-slot ring with the upper-right slot lit.
    static func menuBarIcon() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            let center = NSPoint(x: rect.midX, y: rect.midY)
            let outer: CGFloat = 8.4, inner: CGFloat = 3.7
            for index in 0..<AppIconArt.slots {
                // AppKit angles run counter-clockwise from 3 o'clock: slot 0 is 12 o'clock, slot 1 the upper right.
                let mid = 90.0 - Double(index) * 45
                let start = mid - 16.5, end = mid + 16.5
                let path = NSBezierPath()
                path.appendArc(withCenter: center, radius: outer, startAngle: start, endAngle: end, clockwise: false)
                path.appendArc(withCenter: center, radius: inner, startAngle: end, endAngle: start, clockwise: true)
                path.close()
                NSColor.black.withAlphaComponent(index == AppIconArt.picked ? 1 : 0.4).setFill()
                path.fill()
            }
            return true
        }
        image.isTemplate = true
        return image
    }
}

/// The app icon: a graphite tile, an eight-slot ring whose upper-right slot is picked and lifted, a document
/// in the hub and the pointer that picked it. Flat colours only. Units follow the 1024-point macOS icon grid;
/// promo/src/icon.js draws the same mark for the promo video, so keep the numbers in sync.
struct AppIconArt: View {
    static let slots = 8
    static let picked = 1
    static let tile = Color(hex: 0x16171A)
    static let slot = Color(hex: 0x2C2E34)
    static let accent = Color(hex: 0x0A84FF)

    var body: some View {
        GeometryReader { proxy in
            let s = proxy.size.width
            let u = s / 1024
            let side = 824 * u
            let span = 360.0 / Double(Self.slots)
            let pickMid = (-90.0 + span * Double(Self.picked)) * .pi / 180
            let tipRadius = (150 + 312) / 2 * u + 26 * u
            let tip = CGPoint(x: s / 2 + CGFloat(cos(pickMid)) * tipRadius - 4 * u,
                              y: s / 2 + CGFloat(sin(pickMid)) * tipRadius - 4 * u)
            let pointer = 190 * u
            ZStack {
                RoundedRectangle(cornerRadius: side * 0.225, style: .continuous)
                    .fill(Self.tile)
                    .overlay(RoundedRectangle(cornerRadius: side * 0.225, style: .continuous)
                        .strokeBorder(Color.white.opacity(0.09), lineWidth: max(0.5, 3 * u)))
                    .frame(width: side, height: side)
                    .shadow(color: .black.opacity(0.28), radius: 14 * u, y: 12 * u)
                ForEach(0..<Self.slots, id: \.self) { index in
                    let mid = -90.0 + Double(index) * span
                    let isPicked = index == Self.picked
                    let lift = isPicked ? 24 * u : 0
                    RingSegment(startDegrees: mid - span / 2, endDegrees: mid + span / 2, inner: 150 * u, outer: 312 * u,
                                gap: 22 * u, corner: 21 * u)
                        .fill(isPicked ? Self.accent : Self.slot)
                        .shadow(color: .black.opacity(isPicked ? 0.42 : 0), radius: 14 * u, y: 10 * u)
                        .offset(x: CGFloat(cos(mid * .pi / 180)) * lift, y: CGFloat(sin(mid * .pi / 180)) * lift)
                }
                Circle().fill(Color(hex: 0xF4F4F6)).frame(width: 204 * u, height: 204 * u)
                DocumentGlyph().fill(Self.tile).frame(width: 98 * u, height: 126 * u)
                ZStack {
                    PointerArrow().fill(Color.white)
                    PointerArrow().stroke(Self.tile, style: StrokeStyle(lineWidth: 15 * u, lineJoin: .round))
                }
                .frame(width: pointer * PointerArrow.aspect, height: pointer)
                .compositingGroup()
                .shadow(color: .black.opacity(0.35), radius: 10 * u, y: 8 * u)
                .position(x: tip.x + pointer * PointerArrow.aspect / 2, y: tip.y + pointer / 2)
            }
            .frame(width: s, height: s)
        }
    }
}

/// The classic macOS arrow, tip at the top-left corner of its frame.
struct PointerArrow: Shape {
    static let points: [CGPoint] = [
        CGPoint(x: 0, y: 0), CGPoint(x: 0, y: 0.852), CGPoint(x: 0.198, y: 0.67), CGPoint(x: 0.341, y: 1.0),
        CGPoint(x: 0.473, y: 0.945), CGPoint(x: 0.33, y: 0.621), CGPoint(x: 0.6, y: 0.621),
    ]
    static let aspect: CGFloat = 0.6

    func path(in rect: CGRect) -> Path {
        var path = Path()
        let h = rect.height
        path.addLines(Self.points.map { CGPoint(x: rect.minX + $0.x * h, y: rect.minY + $0.y * h) })
        path.closeSubpath()
        return path
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

/// Small in-app version of the mark (menu bar popover header).
struct WheelGlyph: View {
    var size: CGFloat = 28
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.24, style: .continuous).fill(AppIconArt.tile)
            ForEach(0..<AppIconArt.slots, id: \.self) { index in
                let mid = -90.0 + Double(index) * 45
                RingSegment(startDegrees: mid - 22.5, endDegrees: mid + 22.5, inner: size * 0.15, outer: size * 0.37,
                            gap: size * 0.045, corner: size * 0.02)
                    .fill(index == AppIconArt.picked ? AppIconArt.accent : Color.white.opacity(0.28))
            }
        }
        .frame(width: size, height: size)
    }
}

