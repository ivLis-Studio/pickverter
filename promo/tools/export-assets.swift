// Exports SF Symbols (as black masks) and macOS file-type icons used by the promo video.
// Run: swift promo/tools/export-assets.swift promo/assets
import AppKit
import UniformTypeIdentifiers

let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "promo/assets")
let symbolsDir = out.appendingPathComponent("symbols")
let iconsDir = out.appendingPathComponent("icons")
try FileManager.default.createDirectory(at: symbolsDir, withIntermediateDirectories: true)
try FileManager.default.createDirectory(at: iconsDir, withIntermediateDirectories: true)

func writePNG(_ rep: NSBitmapImageRep, _ url: URL) {
    guard let data = rep.representation(using: .png, properties: [:]) else { return }
    try? data.write(to: url)
}

func bitmap(_ size: NSSize, draw: () -> Void) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = size
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    draw()
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

let symbols = [
    "arrow.down.right.and.arrow.up.left", "arrow.down.forward.and.arrow.up.backward", "crop", "scissors",
    "checkerboard.rectangle", "text.viewfinder", "rotate.right", "rotate.left", "info.circle", "archivebox",
    "arrow.triangle.2.circlepath", "arrow.left.and.right.righttriangle.left.righttriangle.right",
    "arrow.up.and.down.righttriangle.up.righttriangle.down",
    "dot.radiowaves.left.and.right", "terminal", "link", "doc.on.doc", "folder", "plus.square.on.square", "pin",
    "square.and.arrow.up", "chevron.left.forwardslash.chevron.right", "number",
    "square.and.arrow.down", "arrow.up.forward.app", "doc.on.clipboard", "arrow.uturn.backward", "checkmark",
    "xmark", "photo", "option", "command", "control", "shift", "film", "waveform", "person.crop.square",
    "rectangle.stack", "square.grid.2x2", "book", "tablecells", "lock.shield", "globe", "arrow.down.circle",
    "timeline.selection", "photo.on.rectangle", "film.stack", "doc.richtext", "sparkles", "magnifyingglass",
    "rectangle.on.rectangle", "square.dashed", "checkerboard.shield", "person.and.background.dotted",
    "clock", "menubar.dock.rectangle", "doc", "chevron.left", "chevron.right", "list.bullet", "icloud", "app.dashed",
    "square.grid.2x2.fill", "rectangle.grid.1x2", "tag", "ellipsis.circle", "play.fill", "arrow.right", "keyboard",
    "cursorarrow.click.2", "hand.point.up.left", "laptopcomputer", "lock", "chevron.down", "macwindow",
]
for name in symbols {
    let config = NSImage.SymbolConfiguration(pointSize: 160, weight: .medium, scale: .large)
    guard let image = NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(config) else {
        print("missing symbol \(name)")
        continue
    }
    let size = NSSize(width: ceil(image.size.width) + 8, height: ceil(image.size.height) + 8)
    let rep = bitmap(size) {
        let tinted = NSImage(size: image.size, flipped: false) { rect in
            image.draw(in: rect)
            NSColor.black.set()
            rect.fill(using: .sourceAtop)
            return true
        }
        tinted.draw(in: NSRect(x: 4, y: 4, width: image.size.width, height: image.size.height))
    }
    writePNG(rep, symbolsDir.appendingPathComponent(name + ".png"))
}

let types: [(String, String)] = [
    ("pdf", "com.adobe.pdf"), ("mov", "com.apple.quicktime-movie"), ("mp3", "public.mp3"),
    ("key", "com.apple.keynote.key"), ("docx", "org.openxmlformats.wordprocessingml.document"),
    ("zip", "public.zip-archive"), ("xlsx", "org.openxmlformats.spreadsheetml.sheet"), ("heic", "public.heic"),
]
for (name, identifier) in types {
    guard let type = UTType(identifier) else { continue }
    let icon = NSWorkspace.shared.icon(for: type)
    let rep = bitmap(NSSize(width: 512, height: 512)) {
        icon.draw(in: NSRect(x: 0, y: 0, width: 512, height: 512))
    }
    writePNG(rep, iconsDir.appendingPathComponent(name + ".png"))
}
let folder = NSWorkspace.shared.icon(for: .folder)
writePNG(bitmap(NSSize(width: 512, height: 512)) { folder.draw(in: NSRect(x: 0, y: 0, width: 512, height: 512)) },
         iconsDir.appendingPathComponent("folder.png"))
for (name, path) in [("terminal-app", "/System/Applications/Utilities/Terminal.app"), ("finder-app", "/System/Library/CoreServices/Finder.app")] {
    let icon = NSWorkspace.shared.icon(forFile: path)
    writePNG(bitmap(NSSize(width: 512, height: 512)) { icon.draw(in: NSRect(x: 0, y: 0, width: 512, height: 512)) },
             iconsDir.appendingPathComponent(name + ".png"))
}
print("done")

