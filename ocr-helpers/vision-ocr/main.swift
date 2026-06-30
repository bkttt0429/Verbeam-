// verbeam-vision-ocr — macOS Apple Vision OCR helper for Verbeam.
//
// Reads an image and prints the same stdout-JSON block shape the rest of Verbeam's OCR set uses:
//   {"text": "...", "blocks": [{"text": "...", "confidence": 0.0-1.0,
//                               "boundingBox": {"x","y","width","height"}}], "engine": "apple-vision"}
//
// Build (macOS 13+):  swiftc -O main.swift -o verbeam-vision-ocr
// Run:                ./verbeam-vision-ocr --image /path/to.png --language ja
//
// Vision uses the newest VNRecognizeTextRequest revision available; Revision 3 (macOS 13 Ventura)
// added ja/ko, and macOS 14 added ru/uk/th/vi alongside zh-Hans/zh-Hant/yue.

import Foundation
import Vision
import ImageIO
import CoreGraphics

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

// --- parse args: --image <path> --language <lang> ---
var imagePath: String?
var language = "ja"
let argv = Array(CommandLine.arguments.dropFirst())
var i = 0
while i < argv.count {
    switch argv[i] {
    case "--image":
        i += 1
        if i < argv.count { imagePath = argv[i] }
    case "--language":
        i += 1
        if i < argv.count { language = argv[i] }
    default:
        break
    }
    i += 1
}

guard let imagePath else {
    fail("usage: verbeam-vision-ocr --image <path> --language <lang>")
}

// --- load image -> CGImage ---
let url = URL(fileURLWithPath: imagePath)
guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
      let cgImage = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
    fail("failed to load image: \(imagePath)")
}
let width = CGFloat(cgImage.width)
let height = CGFloat(cgImage.height)

// --- map Verbeam language tags -> Vision recognitionLanguages (best-effort, en kept as a fallback) ---
func recognitionLanguages(_ lang: String) -> [String] {
    let l = lang.lowercased()
    if l.hasPrefix("ja") || l.hasPrefix("jp") { return ["ja-JP", "en-US"] }
    if l.hasPrefix("zh-tw") || l.hasPrefix("zh-hant") || l.hasPrefix("zh_tw") { return ["zh-Hant", "en-US"] }
    if l.hasPrefix("yue") { return ["yue-Hant", "zh-Hant"] }
    if l.hasPrefix("zh") { return ["zh-Hans", "en-US"] }
    if l.hasPrefix("ko") { return ["ko-KR", "en-US"] }
    if l.hasPrefix("ru") { return ["ru-RU"] }
    if l.hasPrefix("uk") { return ["uk-UA"] }
    if l.hasPrefix("th") { return ["th-TH"] }
    if l.hasPrefix("vi") { return ["vi-VT"] }
    if l.hasPrefix("en") { return ["en-US"] }
    return [lang]
}

// --- run Vision OCR ---
let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.usesLanguageCorrection = true
request.recognitionLanguages = recognitionLanguages(language)

let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
do {
    try handler.perform([request])
} catch {
    fail("vision request failed: \(error.localizedDescription)")
}

struct BBox: Encodable { let x: Int; let y: Int; let width: Int; let height: Int }
struct Block: Encodable { let text: String; let confidence: Double; let boundingBox: BBox }
struct OcrResult: Encodable { let text: String; let blocks: [Block]; let engine: String }

var blocks: [Block] = []
var lines: [String] = []
for observation in (request.results ?? []) {
    guard let candidate = observation.topCandidates(1).first else { continue }
    // Vision boundingBox is normalized [0,1] with the origin at the BOTTOM-left; convert to
    // top-left pixel coordinates to match the rest of Verbeam's bounding boxes.
    let r = observation.boundingBox
    let x = Int((r.minX * width).rounded())
    let w = Int((r.width * width).rounded())
    let y = Int(((1.0 - r.maxY) * height).rounded())
    let h = Int((r.height * height).rounded())
    blocks.append(Block(
        text: candidate.string,
        confidence: Double(candidate.confidence),
        boundingBox: BBox(x: max(0, x), y: max(0, y), width: max(1, w), height: max(1, h))))
    lines.append(candidate.string)
}

let result = OcrResult(text: lines.joined(separator: "\n"), blocks: blocks, engine: "apple-vision")
let encoder = JSONEncoder()
encoder.outputFormatting = [.withoutEscapingSlashes]
guard let data = try? encoder.encode(result) else {
    fail("failed to encode OCR result")
}
FileHandle.standardOutput.write(data)
