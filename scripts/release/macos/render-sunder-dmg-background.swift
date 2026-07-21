import AppKit
import CoreGraphics
import Foundation

private let logicalWidth: CGFloat = 820
private let logicalHeight: CGFloat = 460

private func color(_ hex: UInt32, alpha: CGFloat = 1) -> CGColor {
    CGColor(
        red: CGFloat((hex >> 16) & 0xff) / 255,
        green: CGFloat((hex >> 8) & 0xff) / 255,
        blue: CGFloat(hex & 0xff) / 255,
        alpha: alpha)
}

private func drawText(
    _ text: String,
    in rect: CGRect,
    font: NSFont,
    foreground: NSColor,
    tracking: CGFloat = 0)
{
    let paragraph = NSMutableParagraphStyle()
    paragraph.alignment = .center
    paragraph.lineBreakMode = .byTruncatingTail
    let attributes: [NSAttributedString.Key: Any] = [
        .font: font,
        .foregroundColor: foreground,
        .kern: tracking,
        .paragraphStyle: paragraph,
    ]
    (text as NSString).draw(in: rect, withAttributes: attributes)
}

private func render(scale: Int, output: URL) throws {
    let pixelWidth = Int(logicalWidth) * scale
    let pixelHeight = Int(logicalHeight) * scale
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    guard let context = CGContext(
        data: nil,
        width: pixelWidth,
        height: pixelHeight,
        bitsPerComponent: 8,
        bytesPerRow: pixelWidth * 4,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
    else {
        throw NSError(domain: "SunderDmgBackground", code: 1)
    }

    context.scaleBy(x: CGFloat(scale), y: CGFloat(scale))
    context.translateBy(x: 0, y: logicalHeight)
    context.scaleBy(x: 1, y: -1)

    let backgroundGradient = CGGradient(
        colorsSpace: colorSpace,
        colors: [color(0x121313), color(0x171818), color(0x1e1f1f)] as CFArray,
        locations: [0, 0.58, 1])!
    context.drawLinearGradient(
        backgroundGradient,
        start: CGPoint(x: 0, y: 0),
        end: CGPoint(x: logicalWidth, y: logicalHeight),
        options: [])

    let amberGlow = CGGradient(
        colorsSpace: colorSpace,
        colors: [color(0xd09132, alpha: 0.20), color(0xd09132, alpha: 0)] as CFArray,
        locations: [0, 1])!
    context.drawRadialGradient(
        amberGlow,
        startCenter: CGPoint(x: 705, y: 70),
        startRadius: 0,
        endCenter: CGPoint(x: 705, y: 70),
        endRadius: 300,
        options: [.drawsAfterEndLocation])

    context.saveGState()
    let upperRibbon = CGMutablePath()
    upperRibbon.move(to: CGPoint(x: 470, y: -55))
    upperRibbon.addCurve(
        to: CGPoint(x: 865, y: 205),
        control1: CGPoint(x: 590, y: 45),
        control2: CGPoint(x: 745, y: 20))
    context.addPath(upperRibbon)
    context.setStrokeColor(color(0xe7b765, alpha: 0.10))
    context.setLineWidth(96)
    context.setLineCap(.round)
    context.strokePath()

    let lowerRibbon = CGMutablePath()
    lowerRibbon.move(to: CGPoint(x: 425, y: 505))
    lowerRibbon.addCurve(
        to: CGPoint(x: 865, y: 220),
        control1: CGPoint(x: 570, y: 315),
        control2: CGPoint(x: 710, y: 475))
    context.addPath(lowerRibbon)
    context.setStrokeColor(color(0xd09132, alpha: 0.07))
    context.setLineWidth(120)
    context.strokePath()
    context.restoreGState()

    context.setStrokeColor(color(0x474949, alpha: 0.45))
    context.setLineWidth(1)
    context.move(to: CGPoint(x: 0, y: 0.5))
    context.addLine(to: CGPoint(x: logicalWidth, y: 0.5))
    context.strokePath()

    let appHalo = CGGradient(
        colorsSpace: colorSpace,
        colors: [color(0xe7b765, alpha: 0.07), color(0xe7b765, alpha: 0)] as CFArray,
        locations: [0, 1])!
    for center in [CGPoint(x: 220, y: 260), CGPoint(x: 600, y: 260)] {
        context.drawRadialGradient(
            appHalo,
            startCenter: center,
            startRadius: 0,
            endCenter: center,
            endRadius: 105,
            options: [.drawsAfterEndLocation])
    }

    context.saveGState()
    context.setStrokeColor(color(0xd09132, alpha: 0.92))
    context.setLineWidth(4)
    context.setLineCap(.round)
    context.move(to: CGPoint(x: 350, y: 260))
    context.addLine(to: CGPoint(x: 470, y: 260))
    context.strokePath()
    context.setFillColor(color(0xd09132, alpha: 0.92))
    let arrow = CGMutablePath()
    arrow.move(to: CGPoint(x: 486, y: 260))
    arrow.addLine(to: CGPoint(x: 465, y: 246))
    arrow.addLine(to: CGPoint(x: 465, y: 274))
    arrow.closeSubpath()
    context.addPath(arrow)
    context.fillPath()
    context.restoreGState()

    let graphicsContext = NSGraphicsContext(cgContext: context, flipped: true)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = graphicsContext
    drawText(
        "SUNDER",
        in: CGRect(x: 0, y: 38, width: logicalWidth, height: 28),
        font: .systemFont(ofSize: 15, weight: .semibold),
        foreground: NSColor(srgbRed: 231 / 255, green: 183 / 255, blue: 101 / 255, alpha: 1),
        tracking: 3.2)
    drawText(
        "Drag Sunder to Applications",
        in: CGRect(x: 70, y: 76, width: logicalWidth - 140, height: 36),
        font: .systemFont(ofSize: 25, weight: .semibold),
        foreground: NSColor(srgbRed: 216 / 255, green: 213 / 255, blue: 206 / 255, alpha: 1))
    drawText(
        "Install Sunder by dragging the app icon into the Applications folder.",
        in: CGRect(x: 95, y: 113, width: logicalWidth - 190, height: 24),
        font: .systemFont(ofSize: 13, weight: .regular),
        foreground: NSColor(srgbRed: 170 / 255, green: 166 / 255, blue: 159 / 255, alpha: 1))
    NSGraphicsContext.restoreGraphicsState()

    guard let image = context.makeImage() else {
        throw NSError(domain: "SunderDmgBackground", code: 2)
    }
    let representation = NSBitmapImageRep(cgImage: image)
    guard let png = representation.representation(using: .png, properties: [.compressionFactor: 1]) else {
        throw NSError(domain: "SunderDmgBackground", code: 3)
    }
    try png.write(to: output, options: .atomic)
}

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write(Data("Usage: render-sunder-dmg-background.swift <output-directory>\n".utf8))
    exit(2)
}

let outputDirectory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
try render(scale: 1, output: outputDirectory.appendingPathComponent("Sunder.png"))
try render(scale: 2, output: outputDirectory.appendingPathComponent("Sunder@2x.png"))
