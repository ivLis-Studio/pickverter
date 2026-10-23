import Compression
import Foundation

/// ZIP archives that open cleanly on Windows as well as on a Mac: composed (NFC) names marked as UTF-8, none of the
/// Mac's own clutter (.DS_Store, ._ files, __MACOSX), folders kept even when empty, ZIP64 past 4 GB or 65,535 items.
/// Reading takes the same care the other way round: names written by Korean Windows (CP949) or old DOS tools (CP437)
/// come out readable, and nothing can land outside the target folder.
enum ZipKit {
    /// Thrown for compression methods other than stored and deflate: the caller falls back to ditto.
    struct Unsupported: Error {}

    static func isJunk(_ name: String) -> Bool {
        let lower = name.lowercased()
        return name == ".DS_Store" || name.hasPrefix("._") || name == "__MACOSX" || name == "Icon\r"
            || lower == "thumbs.db" || lower == "desktop.ini"
    }

    /// Formats that are compressed already: deflating them again only costs time.
    static let storedExts: Set<String> = ["jpg", "jpeg", "png", "gif", "heic", "heif", "webp", "avif", "mp4", "m4v", "mov", "mkv",
                                          "webm", "mp3", "m4a", "aac", "ogg", "opus", "flac", "zip", "gz", "tgz", "bz2", "xz",
                                          "7z", "rar", "docx", "xlsx", "pptx", "hwpx", "epub", "dmg"]

    static func dosTime(_ date: Date) -> (time: UInt16, date: UInt16) {
        let parts = Calendar(identifier: .gregorian).dateComponents(in: .current, from: date)
        guard let year = parts.year, year >= 1980 else { return (0, 0x21) }
        let time = (parts.hour ?? 0) << 11 | (parts.minute ?? 0) << 5 | (parts.second ?? 0) / 2
        let day = (min(year, 2107) - 1980) << 9 | (parts.month ?? 1) << 5 | (parts.day ?? 1)
        return (UInt16(time), UInt16(day))
    }

    static func date(time: UInt16, date: UInt16) -> Date? {
        var parts = DateComponents()
        parts.year = Int(date >> 9) + 1980
        parts.month = Int((date >> 5) & 0xF)
        parts.day = Int(date & 0x1F)
        parts.hour = Int(time >> 11)
        parts.minute = Int((time >> 5) & 0x3F)
        parts.second = Int(time & 0x1F) * 2
        guard (1...12).contains(parts.month ?? 0), (1...31).contains(parts.day ?? 0) else { return nil }
        return Calendar(identifier: .gregorian).date(from: parts)
    }

    // MARK: Little-endian fields

    static func le16(_ value: UInt16) -> Data { withUnsafeBytes(of: value.littleEndian) { Data($0) } }
    static func le32(_ value: UInt32) -> Data { withUnsafeBytes(of: value.littleEndian) { Data($0) } }
    static func le64(_ value: UInt64) -> Data { withUnsafeBytes(of: value.littleEndian) { Data($0) } }

    static func u16(_ bytes: [UInt8], _ at: Int) -> UInt16 { UInt16(bytes[at]) | UInt16(bytes[at + 1]) << 8 }
    static func u32(_ bytes: [UInt8], _ at: Int) -> UInt32 { UInt32(u16(bytes, at)) | UInt32(u16(bytes, at + 2)) << 16 }
    static func u64(_ bytes: [UInt8], _ at: Int) -> UInt64 { UInt64(u32(bytes, at)) | UInt64(u32(bytes, at + 4)) << 32 }
}

struct CRC32 {
    private static let table: [UInt32] = (0..<256).map { index in
        var value = UInt32(index)
        for _ in 0..<8 { value = value & 1 != 0 ? 0xEDB8_8320 ^ (value >> 1) : value >> 1 }
        return value
    }

    private var state: UInt32 = 0xFFFF_FFFF

    mutating func update(_ bytes: UnsafeRawBufferPointer) {
        var value = state
        Self.table.withUnsafeBufferPointer { table in
            for byte in bytes { value = table[Int((value ^ UInt32(byte)) & 0xFF)] ^ (value >> 8) }
        }
        state = value
    }

    var value: UInt32 { state ^ 0xFFFF_FFFF }

    static func of<Bytes: Sequence>(_ bytes: Bytes) -> UInt32 where Bytes.Element == UInt8 {
        var crc = CRC32()
        Array(bytes).withUnsafeBytes { crc.update($0) }
        return crc.value
    }
}

/// Raw DEFLATE (RFC 1951, what ZIP stores) through the Compression framework, in either direction.
final class DeflateStream {
    private let stream = UnsafeMutablePointer<compression_stream>.allocate(capacity: 1)
    private let output = UnsafeMutablePointer<UInt8>.allocate(capacity: 1 << 16)
    private let empty = UnsafeMutablePointer<UInt8>.allocate(capacity: 1)
    private let capacity = 1 << 16
    private(set) var ended = false

    init(decode: Bool) throws {
        let status = compression_stream_init(stream, decode ? COMPRESSION_STREAM_DECODE : COMPRESSION_STREAM_ENCODE, COMPRESSION_ZLIB)
        guard status == COMPRESSION_STATUS_OK else {
            stream.deallocate()
            output.deallocate()
            empty.deallocate()
            throw PickError(L("err.archive_open"))
        }
    }

    deinit {
        compression_stream_destroy(stream)
        stream.deallocate()
        output.deallocate()
        empty.deallocate()
    }

    /// Feeds input (finish: flush everything, encoding only) and hands every produced chunk to sink.
    func process(_ input: UnsafeRawBufferPointer, finish: Bool = false, _ sink: (UnsafeRawBufferPointer) throws -> Void) throws {
        guard !ended else { return }
        stream.pointee.src_ptr = input.baseAddress?.assumingMemoryBound(to: UInt8.self) ?? UnsafePointer(empty)
        stream.pointee.src_size = input.count
        while true {
            stream.pointee.dst_ptr = output
            stream.pointee.dst_size = capacity
            let status = compression_stream_process(stream, finish ? Int32(COMPRESSION_STREAM_FINALIZE.rawValue) : 0)
            guard status != COMPRESSION_STATUS_ERROR else { throw PickError(L("err.archive_open")) }
            let produced = capacity - stream.pointee.dst_size
            if produced > 0 { try sink(UnsafeRawBufferPointer(start: output, count: produced)) }
            if status == COMPRESSION_STATUS_END {
                ended = true
                return
            }
            if !finish, stream.pointee.src_size == 0, stream.pointee.dst_size > 0 { return }
        }
    }
}

// MARK: - Writing

final class ZipWriter {
    private struct Entry {
        let name: [UInt8]
        let flags: UInt16
        let method: UInt16
        let time: UInt16
        let date: UInt16
        let crc: UInt32
        let compressed: UInt64
        let size: UInt64
        let offset: UInt64
        let attributes: UInt32
    }

    private let handle: FileHandle
    private var buffer = Data()
    private var position: UInt64 = 0
    private var entries: [Entry] = []
    private let progress: ((UInt64) -> Void)?
    private var bytesRead: UInt64 = 0

    /// progress receives the number of source bytes read so far.
    init(url: URL, progress: ((UInt64) -> Void)? = nil) throws {
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else { throw PickError(L("err.no_result")) }
        handle = try FileHandle(forWritingTo: url)
        self.progress = progress
    }

    /// Adds a file or a folder with everything inside, under its own name.
    func add(_ url: URL) throws {
        try add(url, as: url.lastPathComponent.precomposedStringWithCanonicalMapping)
    }

    /// Adds a file or a folder with everything inside under the given name.
    func add(_ url: URL, as name: String) throws {
        try Task.checkCancellation()
        let fm = FileManager.default
        let attributes = try fm.attributesOfItem(atPath: url.path)
        let mode = UInt16(truncatingIfNeeded: (attributes[.posixPermissions] as? NSNumber)?.intValue ?? 0o644) & 0o7777
        let modified = attributes[.modificationDate] as? Date ?? Date()
        let type = attributes[.type] as? FileAttributeType
        if type == .typeSymbolicLink {
            let target = try fm.destinationOfSymbolicLink(atPath: url.path)
            try addData(Data(target.utf8), name: name, mode: UInt16(S_IFLNK) | 0o755, modified: modified)
        } else if type == .typeDirectory {
            try addFolder(name, mode: mode, modified: modified)
            let children = try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)
                .map { ($0, $0.lastPathComponent.precomposedStringWithCanonicalMapping) }
                .filter { !ZipKit.isJunk($0.1) }
                .sorted { $0.1.compare($1.1, options: .literal) == .orderedAscending }
            for (child, childName) in children { try add(child, as: name + "/" + childName) }
        } else {
            try addFile(url, name: name, mode: mode, modified: modified)
        }
    }

    private func addFolder(_ name: String, mode: UInt16, modified: Date) throws {
        let bytes = [UInt8]((name + "/").utf8)
        let (time, date) = ZipKit.dosTime(modified)
        let offset = position
        try writeLocalHeader(bytes, method: 0, time: time, date: date, zip64: false, crc: 0, size: 0)
        entries.append(Entry(name: bytes, flags: flags(bytes), method: 0, time: time, date: date, crc: 0, compressed: 0, size: 0,
                             offset: offset, attributes: UInt32(UInt16(S_IFDIR) | (mode == 0 ? 0o755 : mode)) << 16 | 0x10))
    }

    private func addData(_ data: Data, name: String, mode: UInt16, modified: Date) throws {
        let bytes = [UInt8](name.utf8)
        let (time, date) = ZipKit.dosTime(modified)
        let offset = position
        let crc = CRC32.of(data)
        try writeLocalHeader(bytes, method: 0, time: time, date: date, zip64: false, crc: crc, size: UInt64(data.count))
        try write(data)
        entries.append(Entry(name: bytes, flags: flags(bytes), method: 0, time: time, date: date, crc: crc,
                             compressed: UInt64(data.count), size: UInt64(data.count), offset: offset,
                             attributes: UInt32(mode) << 16))
    }

    private func addFile(_ url: URL, name: String, mode: UInt16, modified: Date) throws {
        let bytes = [UInt8](name.utf8)
        let expected = UInt64(FileNames.size(of: url))
        let zip64 = expected >= 0xFFFF_FFFF
        let (time, date) = ZipKit.dosTime(modified)
        var method: UInt16 = expected == 0 || ZipKit.storedExts.contains(url.pathExtension.lowercased()) ? 0 : 8
        let offset = position
        try writeLocalHeader(bytes, method: method, time: time, date: date, zip64: zip64, crc: 0, size: 0)
        let dataStart = position
        var result = try copy(url, deflate: method == 8)
        if method == 8, result.written >= result.read {
            // It didn't get any smaller: store it as it is.
            bytesRead -= result.read
            try rewind(to: dataStart)
            method = 0
            result = try copy(url, deflate: false)
            try overwrite(at: offset + 8, ZipKit.le16(0))
        }
        guard zip64 || (result.read < 0xFFFF_FFFF && result.written < 0xFFFF_FFFF) else {
            // The file grew past 4 GB while it was being read.
            throw PickError(L("err.no_result"))
        }
        var sizes = ZipKit.le32(result.crc)
        sizes.append(ZipKit.le32(zip64 ? 0xFFFF_FFFF : UInt32(result.written)))
        sizes.append(ZipKit.le32(zip64 ? 0xFFFF_FFFF : UInt32(result.read)))
        try overwrite(at: offset + 14, sizes)
        if zip64 {
            try overwrite(at: offset + 30 + UInt64(bytes.count) + 4, ZipKit.le64(result.read) + ZipKit.le64(result.written))
        }
        entries.append(Entry(name: bytes, flags: flags(bytes), method: method, time: time, date: date, crc: result.crc,
                             compressed: result.written, size: result.read, offset: offset,
                             attributes: UInt32(UInt16(S_IFREG) | (mode == 0 ? 0o644 : mode)) << 16))
    }

    /// Bit 11: the name is UTF-8 (only needed past ASCII).
    private func flags(_ name: [UInt8]) -> UInt16 {
        name.contains { $0 >= 0x80 } ? 0x0800 : 0
    }

    private func writeLocalHeader(_ name: [UInt8], method: UInt16, time: UInt16, date: UInt16, zip64: Bool,
                                  crc: UInt32, size: UInt64) throws {
        var header = ZipKit.le32(0x0403_4B50)
        header.append(ZipKit.le16(zip64 ? 45 : 20))
        header.append(ZipKit.le16(flags(name)))
        header.append(ZipKit.le16(method))
        header.append(ZipKit.le16(time))
        header.append(ZipKit.le16(date))
        header.append(ZipKit.le32(crc))
        header.append(ZipKit.le32(zip64 ? 0xFFFF_FFFF : UInt32(size)))
        header.append(ZipKit.le32(zip64 ? 0xFFFF_FFFF : UInt32(size)))
        header.append(ZipKit.le16(UInt16(name.count)))
        header.append(ZipKit.le16(zip64 ? 20 : 0))
        header.append(contentsOf: name)
        if zip64 {
            header.append(ZipKit.le16(1))
            header.append(ZipKit.le16(16))
            header.append(ZipKit.le64(0))
            header.append(ZipKit.le64(0))
        }
        try write(header)
    }

    private func copy(_ url: URL, deflate: Bool) throws -> (crc: UInt32, read: UInt64, written: UInt64) {
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        var crc = CRC32()
        var read: UInt64 = 0
        var written: UInt64 = 0
        let deflater = deflate ? try DeflateStream(decode: false) : nil
        while true {
            try Task.checkCancellation()
            guard let chunk = try input.read(upToCount: 1 << 20), !chunk.isEmpty else { break }
            read += UInt64(chunk.count)
            bytesRead += UInt64(chunk.count)
            try chunk.withUnsafeBytes { raw in
                crc.update(raw)
                if let deflater {
                    try deflater.process(raw) { produced in
                        written += UInt64(produced.count)
                        try write(produced)
                    }
                } else {
                    written += UInt64(raw.count)
                    try write(raw)
                }
            }
            progress?(bytesRead)
        }
        if let deflater {
            try deflater.process(UnsafeRawBufferPointer(start: nil, count: 0), finish: true) { produced in
                written += UInt64(produced.count)
                try write(produced)
            }
        }
        return (crc.value, read, written)
    }

    /// Central directory and end record. The archive is complete once this returns.
    func finish() throws {
        let start = position
        for entry in entries {
            let bigSize = entry.size >= 0xFFFF_FFFF || entry.compressed >= 0xFFFF_FFFF
            let bigOffset = entry.offset >= 0xFFFF_FFFF
            var extra = Data()
            if bigSize || bigOffset {
                var fields = Data()
                if bigSize {
                    fields.append(ZipKit.le64(entry.size))
                    fields.append(ZipKit.le64(entry.compressed))
                }
                if bigOffset { fields.append(ZipKit.le64(entry.offset)) }
                extra = ZipKit.le16(1) + ZipKit.le16(UInt16(fields.count)) + fields
            }
            var record = ZipKit.le32(0x0201_4B50)
            record.append(ZipKit.le16(bigSize || bigOffset ? 0x032D : 0x031E))
            record.append(ZipKit.le16(bigSize || bigOffset ? 45 : 20))
            record.append(ZipKit.le16(entry.flags))
            record.append(ZipKit.le16(entry.method))
            record.append(ZipKit.le16(entry.time))
            record.append(ZipKit.le16(entry.date))
            record.append(ZipKit.le32(entry.crc))
            record.append(ZipKit.le32(bigSize ? 0xFFFF_FFFF : UInt32(entry.compressed)))
            record.append(ZipKit.le32(bigSize ? 0xFFFF_FFFF : UInt32(entry.size)))
            record.append(ZipKit.le16(UInt16(entry.name.count)))
            record.append(ZipKit.le16(UInt16(extra.count)))
            record.append(ZipKit.le16(0))
            record.append(ZipKit.le16(0))
            record.append(ZipKit.le16(0))
            record.append(ZipKit.le32(entry.attributes))
            record.append(ZipKit.le32(bigOffset ? 0xFFFF_FFFF : UInt32(entry.offset)))
            record.append(contentsOf: entry.name)
            record.append(extra)
            try write(record)
        }
        let size = position - start
        let count = UInt64(entries.count)
        if count >= 0xFFFF || size >= 0xFFFF_FFFF || start >= 0xFFFF_FFFF {
            let recordOffset = position
            var record = ZipKit.le32(0x0606_4B50)
            record.append(ZipKit.le64(44))
            record.append(ZipKit.le16(0x032D))
            record.append(ZipKit.le16(45))
            record.append(ZipKit.le32(0))
            record.append(ZipKit.le32(0))
            record.append(ZipKit.le64(count))
            record.append(ZipKit.le64(count))
            record.append(ZipKit.le64(size))
            record.append(ZipKit.le64(start))
            record.append(ZipKit.le32(0x0706_4B50))
            record.append(ZipKit.le32(0))
            record.append(ZipKit.le64(recordOffset))
            record.append(ZipKit.le32(1))
            try write(record)
        }
        var end = ZipKit.le32(0x0605_4B50)
        end.append(ZipKit.le16(0))
        end.append(ZipKit.le16(0))
        end.append(ZipKit.le16(UInt16(min(count, 0xFFFF))))
        end.append(ZipKit.le16(UInt16(min(count, 0xFFFF))))
        end.append(ZipKit.le32(size >= 0xFFFF_FFFF ? 0xFFFF_FFFF : UInt32(size)))
        end.append(ZipKit.le32(start >= 0xFFFF_FFFF ? 0xFFFF_FFFF : UInt32(start)))
        end.append(ZipKit.le16(0))
        try write(end)
        try flush()
        try handle.close()
    }

    // MARK: Output

    private func write(_ data: Data) throws {
        buffer.append(data)
        position += UInt64(data.count)
        if buffer.count >= 1 << 20 { try flush() }
    }

    private func write(_ bytes: UnsafeRawBufferPointer) throws {
        buffer.append(contentsOf: bytes)
        position += UInt64(bytes.count)
        if buffer.count >= 1 << 20 { try flush() }
    }

    private func flush() throws {
        guard !buffer.isEmpty else { return }
        try handle.write(contentsOf: buffer)
        buffer.removeAll(keepingCapacity: true)
    }

    private func overwrite(at offset: UInt64, _ data: Data) throws {
        try flush()
        try handle.seek(toOffset: offset)
        try handle.write(contentsOf: data)
        try handle.seek(toOffset: position)
    }

    private func rewind(to offset: UInt64) throws {
        try flush()
        try handle.truncate(atOffset: offset)
        position = offset
    }
}

// MARK: - Reading

struct ZipEntry {
    var name: String
    var flags: UInt16
    var method: UInt16
    var crc: UInt32
    var compressed: UInt64
    var size: UInt64
    var offset: UInt64
    var external: UInt32
    var madeBy: UInt16
    var time: UInt16
    var date: UInt16

    var isFolder: Bool { name.hasSuffix("/") || name.hasSuffix("\\") }
    var isEncrypted: Bool { flags & 1 != 0 }
    /// Unix permissions, when a Unix-like system (or macOS) wrote the archive.
    var unixMode: UInt16? {
        let host = madeBy >> 8
        guard host == 3 || host == 19, external >> 16 != 0 else { return nil }
        return UInt16(external >> 16)
    }
    var isSymlink: Bool { unixMode.map { $0 & UInt16(S_IFMT) == UInt16(S_IFLNK) } ?? false }
}

enum ZipReader {
    /// The central directory, names decoded and composed.
    static func entries(_ url: URL) throws -> [ZipEntry] {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        return try entries(handle)
    }

    static func entries(_ handle: FileHandle) throws -> [ZipEntry] {
        let damaged = PickError(L("err.archive_open"))
        let fileSize = try handle.seekToEnd()
        let tailLength = min(fileSize, 22 + 65_535)
        guard tailLength >= 22 else { throw damaged }
        try handle.seek(toOffset: fileSize - tailLength)
        let tail = [UInt8](try handle.read(upToCount: Int(tailLength)) ?? Data())
        guard tail.count >= 22, let end = stride(from: tail.count - 22, through: 0, by: -1).first(where: {
            tail[$0] == 0x50 && tail[$0 + 1] == 0x4B && tail[$0 + 2] == 0x05 && tail[$0 + 3] == 0x06
        }) else { throw damaged }
        var count = UInt64(ZipKit.u16(tail, end + 10))
        var size = UInt64(ZipKit.u32(tail, end + 12))
        var start = UInt64(ZipKit.u32(tail, end + 16))
        let endOffset = fileSize - tailLength + UInt64(end)
        if count == 0xFFFF || size == 0xFFFF_FFFF || start == 0xFFFF_FFFF, endOffset >= 20 {
            try handle.seek(toOffset: endOffset - 20)
            let locator = [UInt8](try handle.read(upToCount: 20) ?? Data())
            if locator.count == 20, ZipKit.u32(locator, 0) == 0x0706_4B50 {
                try handle.seek(toOffset: ZipKit.u64(locator, 8))
                let record = [UInt8](try handle.read(upToCount: 56) ?? Data())
                if record.count == 56, ZipKit.u32(record, 0) == 0x0606_4B50 {
                    count = ZipKit.u64(record, 32)
                    size = ZipKit.u64(record, 40)
                    start = ZipKit.u64(record, 48)
                }
            }
        }
        guard start + size <= fileSize else { throw damaged }
        try handle.seek(toOffset: start)
        let directory = [UInt8](try handle.read(upToCount: Int(size)) ?? Data())

        struct Raw {
            var entry: ZipEntry
            var name: [UInt8]
            var unicode: String?
        }
        var raws: [Raw] = []
        var at = 0
        while at + 46 <= directory.count, ZipKit.u32(directory, at) == 0x0201_4B50, UInt64(raws.count) < max(count, 1) * 2 {
            let nameLength = Int(ZipKit.u16(directory, at + 28))
            let extraLength = Int(ZipKit.u16(directory, at + 30))
            let commentLength = Int(ZipKit.u16(directory, at + 32))
            let nameStart = at + 46
            guard nameStart + nameLength + extraLength + commentLength <= directory.count else { throw damaged }
            let name = Array(directory[nameStart..<(nameStart + nameLength)])
            var entry = ZipEntry(name: "", flags: ZipKit.u16(directory, at + 8), method: ZipKit.u16(directory, at + 10),
                                 crc: ZipKit.u32(directory, at + 16), compressed: UInt64(ZipKit.u32(directory, at + 20)),
                                 size: UInt64(ZipKit.u32(directory, at + 24)), offset: UInt64(ZipKit.u32(directory, at + 42)),
                                 external: ZipKit.u32(directory, at + 38), madeBy: ZipKit.u16(directory, at + 4),
                                 time: ZipKit.u16(directory, at + 12), date: ZipKit.u16(directory, at + 14))
            var unicode: String?
            let extra = Array(directory[(nameStart + nameLength)..<(nameStart + nameLength + extraLength)])
            var field = 0
            while field + 4 <= extra.count {
                let id = ZipKit.u16(extra, field)
                let length = Int(ZipKit.u16(extra, field + 2))
                let body = field + 4
                guard body + length <= extra.count else { break }
                if id == 0x0001 {
                    var cursor = body
                    if entry.size == 0xFFFF_FFFF, cursor + 8 <= body + length { entry.size = ZipKit.u64(extra, cursor); cursor += 8 }
                    if entry.compressed == 0xFFFF_FFFF, cursor + 8 <= body + length { entry.compressed = ZipKit.u64(extra, cursor); cursor += 8 }
                    if entry.offset == 0xFFFF_FFFF, cursor + 8 <= body + length { entry.offset = ZipKit.u64(extra, cursor) }
                } else if id == 0x7075, length > 5, extra[body] == 1, ZipKit.u32(extra, body + 1) == CRC32.of(name) {
                    // Info-ZIP's Unicode path: the real name next to a legacy one.
                    unicode = String(bytes: extra[(body + 5)..<(body + length)], encoding: .utf8)
                }
                field = body + length
            }
            raws.append(Raw(entry: entry, name: name, unicode: unicode))
            at = nameStart + nameLength + extraLength + commentLength
        }

        // Names without the UTF-8 flag: UTF-8 when they all read as UTF-8, else the DOS code page of the interface
        // language (Japanese, Chinese, Russian Windows) or CP949 (Korean Windows) when they all read as that, else
        // each one on its own (UTF-8, that code page, CP949, CP437).
        let legacy = raws.filter { $0.unicode == nil && $0.entry.flags & 0x0800 == 0 && $0.name.contains { $0 >= 0x80 } }.map(\.name)
        let regional = TextCodec.regionalZipNames
        let shared: String.Encoding?
        if legacy.allSatisfy({ String(bytes: $0, encoding: .utf8) != nil }) {
            shared = .utf8
        } else if let regional, legacy.allSatisfy({ String(bytes: $0, encoding: regional) != nil }) {
            shared = regional
        } else if legacy.allSatisfy({ String(bytes: $0, encoding: TextCodec.cp949) != nil }) {
            shared = TextCodec.cp949
        } else {
            shared = nil
        }
        return raws.map { raw in
            var entry = raw.entry
            let decoded: String
            if let unicode = raw.unicode {
                decoded = unicode
            } else if entry.flags & 0x0800 != 0 {
                decoded = String(decoding: raw.name, as: UTF8.self)
            } else {
                decoded = decodeName(raw.name, preferring: shared)
            }
            entry.name = decoded.precomposedStringWithCanonicalMapping
            return entry
        }
    }

    static func decodeName(_ bytes: [UInt8], preferring encoding: String.Encoding?) -> String {
        if let encoding, let name = String(bytes: bytes, encoding: encoding) { return name }
        if let name = String(bytes: bytes, encoding: .utf8) { return name }
        if let regional = TextCodec.regionalZipNames, let name = String(bytes: bytes, encoding: regional) { return name }
        if let name = String(bytes: bytes, encoding: TextCodec.cp949) { return name }
        return String(bytes: bytes, encoding: TextCodec.cp437) ?? String(decoding: bytes, as: UTF8.self)
    }

    /// Where an entry goes inside the target folder: backslashes count as separators, leading slashes and drive letters
    /// are dropped, and anything climbing out with ".." (or Mac and Windows clutter) is left out.
    static func safeParts(_ name: String) -> [String]? {
        var parts = name.replacingOccurrences(of: "\\", with: "/").split(separator: "/").map(String.init).filter { $0 != "." }
        if let first = parts.first, first.count == 2, first.hasSuffix(":") { parts.removeFirst() }
        guard !parts.isEmpty, !parts.contains(".."), !parts.contains(where: ZipKit.isJunk) else { return nil }
        return parts
    }

    /// Unpacks into folder (which must exist), names composed exactly as decoded.
    static func extract(_ url: URL, into folder: URL, progress: ((Double) -> Void)? = nil) throws {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let entries = try entries(handle)
        if entries.contains(where: \.isEncrypted) { throw PickError(L("err.zip_encrypted")) }
        if entries.contains(where: { !$0.isFolder && $0.method != 0 && $0.method != 8 }) { throw ZipKit.Unsupported() }
        let root = NameFixer.pathBytes(folder)
        let total = Double(max(1, entries.reduce(0) { $0 + $1.size }))
        var done: UInt64 = 0
        var links: [(parts: [String], target: String)] = []
        var dates: [(path: [UInt8], date: Date)] = []
        for entry in entries {
            try Task.checkCancellation()
            guard let parts = safeParts(entry.name) else { continue }
            let folderPath = try makeFolders(root, parts.dropLast())
            let path = folderPath + [NameFixer.slash] + NameFixer.composedName(parts[parts.count - 1])
            if entry.isFolder {
                _ = NameFixer.withCPath(path) { mkdir($0, 0o755) }
                continue
            }
            if entry.isSymlink {
                var target = Data()
                _ = try read(entry, from: handle) { target.append(contentsOf: $0) }
                links.append((parts, String(decoding: target, as: UTF8.self)))
                continue
            }
            let mode = mode_t((entry.unixMode ?? 0o644) & 0o777 | 0o600)
            let descriptor = NameFixer.withCPath(path) { open($0, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW, mode) }
            guard descriptor >= 0 else { throw PickError(L("err.archive_open")) }
            let output = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
            var crc = CRC32()
            let written = try read(entry, from: handle) { bytes in
                crc.update(bytes)
                try output.write(contentsOf: Data(bytes))
                done += UInt64(bytes.count)
                progress?(Double(done) / total)
            }
            try output.close()
            guard crc.value == entry.crc, written == entry.size else { throw PickError(L("err.archive_open")) }
            if let date = ZipKit.date(time: entry.time, date: entry.date) { dates.append((path, date)) }
        }
        // Links last, and only ones that point somewhere inside the archive.
        for link in links where staysInside(link.target, from: Array(link.parts.dropLast())) {
            let folderPath = try makeFolders(root, link.parts.dropLast())
            let path = folderPath + [NameFixer.slash] + NameFixer.composedName(link.parts[link.parts.count - 1])
            _ = NameFixer.withCPath(path) { at in link.target.withCString { symlink($0, at) } }
        }
        for (path, date) in dates {
            let seconds = Int(date.timeIntervalSince1970)
            var times = [timeval(tv_sec: seconds, tv_usec: 0), timeval(tv_sec: seconds, tv_usec: 0)]
            _ = NameFixer.withCPath(path) { utimes($0, &times) }
        }
    }

    private static func makeFolders(_ root: [UInt8], _ parts: ArraySlice<String>) throws -> [UInt8] {
        var path = root
        for part in parts {
            path += [NameFixer.slash] + NameFixer.composedName(part)
            if NameFixer.withCPath(path, { mkdir($0, 0o755) }) != 0, errno != EEXIST { throw PickError(L("err.archive_open")) }
            guard NameFixer.isFolder(path) else { throw PickError(L("err.archive_open")) }
        }
        return path
    }

    static func staysInside(_ target: String, from folder: [String]) -> Bool {
        guard !target.hasPrefix("/"), !target.isEmpty else { return false }
        var stack = folder
        for part in target.split(separator: "/") where part != "." {
            if part == ".." {
                guard !stack.isEmpty else { return false }
                stack.removeLast()
            } else {
                stack.append(String(part))
            }
        }
        return true
    }

    /// Streams one entry's data (stored or deflated) to sink and returns how many bytes came out.
    private static func read(_ entry: ZipEntry, from handle: FileHandle, _ sink: (UnsafeRawBufferPointer) throws -> Void) throws -> UInt64 {
        let damaged = PickError(L("err.archive_open"))
        try handle.seek(toOffset: entry.offset)
        let header = [UInt8](try handle.read(upToCount: 30) ?? Data())
        guard header.count == 30, ZipKit.u32(header, 0) == 0x0403_4B50 else { throw damaged }
        let skip = UInt64(ZipKit.u16(header, 26)) + UInt64(ZipKit.u16(header, 28))
        try handle.seek(toOffset: entry.offset + 30 + skip)
        let inflater = entry.method == 8 ? try DeflateStream(decode: true) : nil
        var remaining = entry.compressed
        var produced: UInt64 = 0
        while remaining > 0 {
            guard let chunk = try handle.read(upToCount: Int(min(remaining, 1 << 20))), !chunk.isEmpty else { throw damaged }
            remaining -= UInt64(chunk.count)
            try chunk.withUnsafeBytes { raw in
                if let inflater {
                    try inflater.process(raw) { bytes in
                        produced += UInt64(bytes.count)
                        try sink(bytes)
                    }
                } else {
                    produced += UInt64(raw.count)
                    try sink(raw)
                }
            }
        }
        if let inflater, !inflater.ended, entry.size > 0 { throw damaged }
        return produced
    }
}
