import AppKit
import SwiftUI

/// Annular sector with rounded corners and constant-width gaps between neighbours.
/// Degrees follow SwiftUI: 0° = 3 o'clock, increasing clockwise.
struct RingSegment: Shape {
    var startDegrees: Double
    var endDegrees: Double
    var inner: CGFloat
    var outer: CGFloat
    var gap: CGFloat = 3
    var corner: CGFloat = 8

    func path(in rect: CGRect) -> Path {
        let c = CGPoint(x: rect.midX, y: rect.midY)
        let ri = inner + corner, ro = outer - corner
        let half = gap / 2 + corner
        let s = startDegrees * .pi / 180, e = endDegrees * .pi / 180
        let dso = Double(half / ro), dsi = Double(half / ri)
        guard ro > ri, e - s > 2 * dsi + 0.02 else { return Path() }
        let core = CGMutablePath()
        core.addArc(center: c, radius: ro, startAngle: s + dso, endAngle: e - dso, clockwise: false)
        core.addArc(center: c, radius: ri, startAngle: e - dsi, endAngle: s + dsi, clockwise: true)
        core.closeSubpath()
        guard corner > 0 else { return Path(core) }
        let rounded = core.copy(strokingWithWidth: corner * 2, lineCap: .round, lineJoin: .round, miterLimit: 10)
        return Path(rounded.union(core))
    }
}

/// Radians clockwise from 12 o'clock -> SwiftUI degrees.
func swiftUIDegrees(_ radians: Double) -> Double { radians * 180 / .pi - 90 }

struct WheelView: View {
    @ObservedObject var model: WheelController
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let g = model.geometry
        ZStack {
            Circle()
                .fill(Color.black.opacity(scheme == .dark ? 0.32 : 0.12))
                .frame(width: g.discRadius * 2, height: g.discRadius * 2)
                .blur(radius: 14)
                .offset(y: 6)
                .position(x: g.center.x, y: g.center.y)
            if let ring = model.subRing {
                SubRingView(ring: ring, highlighted: model.subHighlighted, geometry: g)
                    .frame(width: g.boxSide, height: g.boxSide)
                    .id(ring.parentIndex)
                    .transition(.opacity.combined(with: .scale(scale: 0.97)))
            }
            disc
                .frame(width: g.discRadius * 2, height: g.discRadius * 2)
                .position(x: g.center.x, y: g.center.y)
            Group {
                if model.choice != nil {
                    ChoiceCaption()
                } else {
                    WheelTabBar(model: model)
                        .opacity(model.tabsHidden ? 0 : 1)
                }
            }
            .position(x: g.center.x, y: g.center.y + g.tabBarOffset)
        }
        .frame(width: g.windowSize.width, height: g.windowSize.height)
        .scaleEffect(model.appeared ? 1 : 0.9, anchor: .center)
        .opacity(model.appeared ? 1 : 0)
    }

    private var disc: some View {
        let g = model.geometry
        return ZStack {
            GlassSurface(shape: .circle)
            Circle().strokeBorder(Theme.hairline(scheme), lineWidth: 0.5)
            ZStack {
                ForEach(Array(model.segments.enumerated()), id: \.element.id) { index, segment in
                    SegmentView(segment: segment, index: index, count: model.segments.count,
                                highlighted: model.highlighted == index, dimmed: model.subHighlighted != nil && model.highlighted == index,
                                geometry: g)
                        .equatable()
                }
                if model.segments.isEmpty {
                    Circle()
                        .strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [4, 5]))
                        .foregroundStyle(Color.primary.opacity(0.2))
                        .frame(width: (g.outerRadius - 12) * 2, height: (g.outerRadius - 12) * 2)
                }
            }
            .id("\(model.mode.rawValue)-\(model.page)-\(model.choice == nil)")
            .transition(.opacity)
            HubView(model: model, pointer: model.pointer)
                .frame(width: (g.innerRadius - 5) * 2, height: (g.innerRadius - 5) * 2)
        }
    }
}

struct SegmentView: View, Equatable {
    let segment: SegmentData
    let index: Int
    let count: Int
    let highlighted: Bool
    /// The slot stays selected but quieter while one of its outer options is targeted.
    let dimmed: Bool
    let geometry: WheelGeometry
    @Environment(\.colorScheme) private var scheme

    static func == (lhs: SegmentView, rhs: SegmentView) -> Bool {
        lhs.segment == rhs.segment && lhs.index == rhs.index && lhs.count == rhs.count
            && lhs.highlighted == rhs.highlighted && lhs.dimmed == rhs.dimmed
    }

    var body: some View {
        let g = geometry
        let span = 360.0 / Double(max(count, 1))
        let mid = -90.0 + span * Double(index)
        let radians = mid * .pi / 180
        let shape = RingSegment(startDegrees: mid - span / 2, endDegrees: mid + span / 2, inner: g.innerRadius, outer: g.outerRadius)
        let labelRadius = (g.innerRadius + g.outerRadius) / 2 + 2
        let arc = CGFloat(2 * Double.pi) * labelRadius / CGFloat(max(count, 1))
        let labelWidth = min(arc - 12, g.outerRadius - g.innerRadius - 8)
        let side = g.discRadius * 2
        let selected = highlighted && !dimmed
        ZStack {
            shape.fill(selected ? Theme.accent : (highlighted ? Theme.accent.opacity(0.22) : Theme.tileFill(scheme)))
            if segment.hasVariants {
                Capsule()
                    .fill(selected ? Color.white.opacity(0.6) : Color.primary.opacity(0.15))
                    .frame(width: 10, height: 2)
                    .rotationEffect(.degrees(mid + 90))
                    .position(x: side / 2 + CGFloat(cos(radians)) * (g.outerRadius - 7),
                              y: side / 2 + CGFloat(sin(radians)) * (g.outerRadius - 7))
            }
            SegmentLabel(segment: segment, selected: selected)
                .frame(width: max(labelWidth, 40))
                .position(x: side / 2 + CGFloat(cos(radians)) * labelRadius, y: side / 2 + CGFloat(sin(radians)) * labelRadius)
        }
        .frame(width: side, height: side)
        .opacity(segment.unavailable ? (segment.needsDownload ? 0.6 : 0.35) : 1)
        .animation(.easeOut(duration: 0.12), value: highlighted)
        .animation(.easeOut(duration: 0.12), value: dimmed)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(segment.title)
        .accessibilityAddTraits(highlighted ? [.isButton, .isSelected] : .isButton)
    }
}

struct SegmentLabel: View {
    let segment: SegmentData
    let selected: Bool
    var compact = false

    var body: some View {
        Group {
            if segment.isMore {
                VStack(spacing: 3) {
                    Image(systemName: "ellipsis").font(.system(size: 14, weight: .medium))
                    Text(segment.title).font(.system(size: 11, weight: .medium))
                    if let label = segment.pageLabel { Text(label).font(.system(size: 9)).opacity(0.6) }
                }
            } else if segment.isFormat {
                Text(segment.title)
                    .font(.system(size: segment.title.count > 4 ? 12.5 : 14, weight: .semibold))
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            } else {
                VStack(spacing: compact ? 2 : 4) {
                    if !segment.symbol.isEmpty {
                        Image(systemName: segment.symbol).font(.system(size: compact ? 11.5 : 14, weight: .regular))
                    }
                    Text(segment.title)
                        .font(.system(size: compact ? 10 : 11, weight: .medium))
                        .multilineTextAlignment(.center)
                        .lineLimit(2)
                        .minimumScaleFactor(0.72)
                }
            }
        }
        .foregroundStyle(selected ? Color.white : Color.primary.opacity(0.85))
    }
}

/// Options for the hovered slot, fanned out just outside the wheel.
struct SubRingView: View {
    let ring: SubRingData
    let highlighted: Int?
    let geometry: WheelGeometry
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let g = geometry
        let spanDegrees = ring.span * 180 / .pi
        let first = swiftUIDegrees(ring.angle(of: 0)) - spanDegrees / 2
        let last = first + spanDegrees * Double(ring.items.count)
        let band = GlassShape.ring(start: first - 1.2, end: last + 1.2, inner: g.subInner - 4, outer: g.subOuter + 4)
        let labelRadius = (g.subInner + g.subOuter) / 2
        let arc = CGFloat(ring.span) * labelRadius
        ZStack {
            GlassSurface(shape: band)
            GlassFill(shape: band).stroke(Theme.hairline(scheme), lineWidth: 0.5)
            ForEach(Array(ring.items.enumerated()), id: \.element.id) { index, item in
                let start = first + spanDegrees * Double(index)
                let radians = (start + spanDegrees / 2) * .pi / 180
                let selected = highlighted == index
                let tile = RingSegment(startDegrees: start, endDegrees: start + spanDegrees, inner: g.subInner, outer: g.subOuter,
                                       gap: 3, corner: 7)
                tile.fill(selected ? Theme.accent : Theme.tileFill(scheme))
                if item.isDefault, !selected {
                    // What a plain click on the slot runs: an outline never collides with the label.
                    tile.stroke(Theme.accent.opacity(0.6), lineWidth: 1.3)
                }
                SegmentLabel(segment: item, selected: selected, compact: true)
                    .frame(width: Self.labelWidth(angle: radians, arc: arc, thickness: g.subOuter - g.subInner))
                    .position(x: g.center.x + CGFloat(cos(radians)) * labelRadius, y: g.center.y + CGFloat(sin(radians)) * labelRadius)
                    .opacity(item.unavailable ? (item.needsDownload ? 0.6 : 0.35) : 1)
            }
        }
        .animation(.easeOut(duration: 0.1), value: highlighted)
    }

    /// Labels are laid out horizontally, so near 3 and 9 o'clock their width runs across the band.
    /// `angle` is in SwiftUI radians (0 = 3 o'clock).
    static func labelWidth(angle: Double, arc: CGFloat, thickness: CGFloat) -> CGFloat {
        let across = abs(cos(angle)), along = abs(sin(angle))
        let byBand = across > 0.05 ? (thickness - 6) / CGFloat(across) : .greatestFiniteMagnitude
        let byArc = along > 0.05 ? (arc - 8) / CGFloat(along) : .greatestFiniteMagnitude
        return max(40, min(byBand, byArc, arc + 20))
    }
}

struct HubView: View {
    @ObservedObject var model: WheelController
    @ObservedObject var pointer: PointerModel
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        ZStack {
            Circle().fill(scheme == .dark ? Color.black.opacity(0.14) : Color.white.opacity(0.55))
            Circle().strokeBorder(Theme.hairline(scheme), lineWidth: 0.5)
            if let angle = pointer.angle, model.highlighted != nil {
                Circle()
                    .trim(from: 0, to: 0.08)
                    .stroke(Theme.accent, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                    .rotationEffect(.degrees(swiftUIDegrees(angle) - 0.04 * 360))
                    .padding(2)
                    .animation(.interactiveSpring(response: 0.16, dampingFraction: 0.85), value: angle)
            }
            content.padding(.horizontal, 11)
        }
    }

    @ViewBuilder
    private var content: some View {
        if let flash = model.flash {
            VStack(spacing: 4) {
                Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
                Text(flash).font(.system(size: 9.5)).multilineTextAlignment(.center).lineLimit(4)
            }
        } else if let choice = model.choice {
            let destination: ResultDestination = {
                if case .destination(let value) = model.highlightedItem { return value }
                return .file
            }()
            VStack(spacing: 3) {
                Image(systemName: destination.symbol).font(.system(size: 17))
                Text(destination.label).font(.system(size: 11.5, weight: .semibold)).multilineTextAlignment(.center).lineLimit(2)
                Text(choice.option.isFormat ? choice.option.title : choice.option.title)
                    .font(.system(size: 9.5)).foregroundStyle(.secondary).lineLimit(1)
            }
        } else if let active = model.activeOption {
            let option = active.option
            let parent = active.parent
            VStack(spacing: 2) {
                if (parent ?? option).isFormat {
                    Text((parent ?? option).title).font(.system(size: 18, weight: .semibold)).lineLimit(1).minimumScaleFactor(0.6)
                } else {
                    Image(systemName: option.symbol.isEmpty ? (parent?.symbol ?? "circle") : option.symbol)
                        .font(.system(size: 17))
                }
                if parent != nil {
                    Text(option.title).font(.system(size: 11, weight: .semibold)).foregroundStyle(Theme.accent)
                        .lineLimit(1).minimumScaleFactor(0.7)
                }
                let reason = option.unavailable ?? parent?.unavailable
                let offersDownload = Tools.isDownload(reason)
                Text(offersDownload ? Tools.downloadPrompt(reason) : (reason ?? model.previewLine(for: option, parent: parent)))
                    .font(.system(size: 9.5))
                    .foregroundStyle(offersDownload ? Theme.accent : (reason != nil ? Color.orange : Color.secondary))
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .truncationMode(.middle)
                if let parent, active.implied {
                    Text("↑↓ " + LP("hint.options", parent.variants.count))
                        .font(.system(size: 8.5)).foregroundStyle(.tertiary).lineLimit(1).minimumScaleFactor(0.7)
                } else if parent == nil, !option.variants.isEmpty {
                    Text(LP("hint.options", option.variants.count)).font(.system(size: 8.5)).foregroundStyle(.tertiary)
                        .lineLimit(1).minimumScaleFactor(0.7)
                } else if let hint = model.destinationHint(for: option) {
                    Text(hint).font(.system(size: 8.5)).foregroundStyle(.tertiary).lineLimit(1).minimumScaleFactor(0.7)
                }
            }
        } else if case .more = model.highlightedItem {
            VStack(spacing: 3) {
                Image(systemName: "arrow.forward.circle").font(.system(size: 17))
                Text(model.origin == .drag ? L("wheel.dwell_next") : L("wheel.next_page")).font(.system(size: 9.5))
            }
            .foregroundStyle(.secondary)
        } else {
            VStack(spacing: 4) {
                ThumbnailStack(image: model.thumbnail, count: model.files.count)
                    .frame(width: 44, height: 44)
                Text(model.filesTitle)
                    .font(.system(size: 10.5, weight: .medium))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(model.segments.isEmpty ? L("wheel.empty") : model.hint)
                    .font(.system(size: 8.5))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
        }
    }
}

struct ThumbnailStack: View {
    let image: NSImage?
    let count: Int

    var body: some View {
        ZStack {
            if count > 1 {
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(Color.primary.opacity(0.08))
                    .frame(width: 36, height: 36)
                    .rotationEffect(.degrees(-8))
                    .offset(x: -5, y: 1)
            }
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(width: 40, height: 40)
            }
            if count > 1 {
                Text("\(count)")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 5)
                    .frame(minWidth: 18, minHeight: 18)
                    .background(Capsule().fill(Theme.accent))
                    .offset(x: 18, y: -17)
            }
        }
    }
}

struct WheelTabBar: View {
    @ObservedObject var model: WheelController
    @Namespace private var selection
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let g = model.geometry
        HStack(spacing: g.tabSpacing) {
            ForEach(WheelMode.allCases) { mode in
                let active = model.mode == mode
                let empty = (model.counts[mode] ?? 0) == 0
                HStack(spacing: 5) {
                    Text(mode.keyGlyph).font(.system(size: 10.5, weight: .medium)).foregroundStyle(.secondary)
                    Text(mode.title).font(.system(size: 12, weight: active ? .semibold : .regular))
                        .lineLimit(1).minimumScaleFactor(0.7)
                }
                .foregroundStyle(empty ? Color.primary.opacity(0.3) : Color.primary)
                .frame(width: g.tabWidth, height: g.tabHeight)
                .background {
                    if active {
                        Capsule().fill(Theme.selectedTab(scheme))
                            .shadow(color: .black.opacity(0.12), radius: 1, y: 0.5)
                            .matchedGeometryEffect(id: "tab", in: selection)
                    } else if model.hoveredTab == mode {
                        Capsule().fill(Color.primary.opacity(0.06))
                    }
                }
            }
        }
        .padding(g.tabPadding)
        .background(GlassSurface(shape: .capsule))
        .overlay(Capsule().strokeBorder(Theme.hairline(scheme), lineWidth: 0.5))
        .animation(.spring(response: 0.28, dampingFraction: 0.86), value: model.mode)
    }
}

struct ChoiceCaption: View {
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        HStack(spacing: 8) {
            Text(L("dest.title")).font(.system(size: 12, weight: .semibold))
            Text("↩︎ · ⌘C · ⌘S · esc").font(.system(size: 10.5)).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .frame(height: 30)
        .background(GlassSurface(shape: .capsule))
        .overlay(Capsule().strokeBorder(Theme.hairline(scheme), lineWidth: 0.5))
    }
}
