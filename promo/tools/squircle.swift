// Prints the SVG path of SwiftUI's continuous rounded rectangle used by the app icon tile.
import SwiftUI

let rect = CGRect(x: 100, y: 100, width: 824, height: 824)
let path = RoundedRectangle(cornerRadius: 824 * 0.225, style: .continuous).path(in: rect).cgPath
var d = ""
func f(_ v: CGFloat) -> String { String(format: "%.2f", v) }
path.applyWithBlock { element in
    let e = element.pointee
    let p = e.points
    switch e.type {
    case .moveToPoint: d += "M\(f(p[0].x)) \(f(p[0].y)) "
    case .addLineToPoint: d += "L\(f(p[0].x)) \(f(p[0].y)) "
    case .addQuadCurveToPoint: d += "Q\(f(p[0].x)) \(f(p[0].y)) \(f(p[1].x)) \(f(p[1].y)) "
    case .addCurveToPoint: d += "C\(f(p[0].x)) \(f(p[0].y)) \(f(p[1].x)) \(f(p[1].y)) \(f(p[2].x)) \(f(p[2].y)) "
    case .closeSubpath: d += "Z"
    @unknown default: break
    }
}
print(d)

