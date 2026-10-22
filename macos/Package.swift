// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "Pickverter",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Pickverter",
            path: "Sources/Pickverter"
        )
    ],
    swiftLanguageModes: [.v5]
)
