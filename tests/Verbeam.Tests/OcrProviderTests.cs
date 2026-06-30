using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OcrProviderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "verbeam-ocr-provider-tests-" + Guid.NewGuid());

    [Fact]
    public void OcrRoutingService_ListsServerSideSmokeMatrixProvidersForLane()
    {
        var registry = new OcrProviderRegistry(
        [
            new NamedOcrProvider("oneocr"),
            new NamedOcrProvider("rapidocr-net"),
            new NamedOcrProvider("windows"),
            new NamedOcrProvider("external"),
            new NamedOcrProvider("rapidocr-ppocrv5"),
            new NamedOcrProvider("paddleocr"),
            new NamedOcrProvider("pix2text"),
            new NamedOcrProvider("pp-structure-v3"),
            new NamedOcrProvider("paddleocr-vl"),
            new NamedOcrProvider("dots-ocr")
        ]);
        var routing = new OcrRoutingService(new VerbeamOptions(), registry);

        var realtime = routing.ListSmokeMatrixProviders("dialogue", "speed");
        var structure = routing.ListSmokeMatrixProviders("table", "balanced");
        var highAccuracy = routing.ListSmokeMatrixProviders("document", "accuracy");

        Assert.Equal(["rapidocr-net", "oneocr", "windows", "rapidocr-ppocrv5", "external", "paddleocr"], realtime);
        Assert.Equal(["pp-structure-v3", "pix2text", "paddleocr", "external"], structure);
        Assert.Equal(["paddleocr-vl", "pp-structure-v3", "pix2text", "dots-ocr", "paddleocr", "external"], highAccuracy);
    }

    [Theory]
    [InlineData("ja", "rapidocr-net")]     // Japanese: PP-OCRv5 reads kana+kanji
    [InlineData("zh-TW", "rapidocr-net")]  // Chinese
    [InlineData("en", "rapidocr-net")]     // Latin
    [InlineData("auto", "rapidocr-net")]   // auto/unknown → the fast CJK+Latin default
    [InlineData(null, "rapidocr-net")]     // no language → same default
    [InlineData("ko", "oneocr")]           // Korean: not in the ch model → OneOCR coverage
    [InlineData("ru", "oneocr")]           // Cyrillic → OneOCR
    public void OcrRoutingService_RealtimeAutoPicksEngineByLanguage(string? language, string expected)
    {
        var registry = new OcrProviderRegistry([new NamedOcrProvider("oneocr"), new NamedOcrProvider("rapidocr-net")]);
        var routing = new OcrRoutingService(new VerbeamOptions(), registry);

        Assert.Equal(expected, routing.ResolveProviderName("auto", "dialogue", "speed", language));
    }

    [Fact]
    public void OcrRoutingService_RealtimeFallsBackToOneOcrWhenRapidOcrNetMissing()
    {
        // rapidocr-net not registered (e.g. its ONNX models aren't installed) → the CJK
        // pick degrades to OneOCR rather than throwing at OCR time.
        var registry = new OcrProviderRegistry([new NamedOcrProvider("oneocr")]);
        var routing = new OcrRoutingService(new VerbeamOptions(), registry);

        Assert.Equal("oneocr", routing.ResolveProviderName("auto", "dialogue", "speed", "ja"));
    }

    [Fact]
    public void OcrRoutingService_ExplicitProviderOverridesLanguageAutoPick()
    {
        var registry = new OcrProviderRegistry([new NamedOcrProvider("oneocr"), new NamedOcrProvider("rapidocr-net")]);
        var routing = new OcrRoutingService(new VerbeamOptions(), registry);

        Assert.Equal("oneocr", routing.ResolveProviderName("oneocr", "dialogue", "speed", "ja"));
    }

    [Fact]
    public async Task ExternalCommandOcrProvider_ParsesStructuredDocumentResult()
    {
        Directory.CreateDirectory(_tempDirectory);
        var scriptPath = Path.Combine(_tempDirectory, "structured-ocr.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            @'
            {
              "text": "x^2 + 1\nA B",
              "engine": "external:test",
              "document": {
                "version": "ocr-ir-v1",
                "pages": [
                  {
                    "pageIndex": 0,
                    "blocks": [
                      {
                        "id": "p0-b0",
                        "type": "formula",
                        "text": "$$x^2 + 1$$",
                        "confidence": 1,
                        "readingOrder": 0,
                        "engine": "external:test",
                        "shouldTranslate": false,
                        "children": [],
                        "formula": {
                          "latex": "x^2 + 1",
                          "sourceText": "$$x^2 + 1$$",
                          "shouldTranslate": false
                        }
                      },
                      {
                        "id": "p0-b1",
                        "type": "table",
                        "text": "| A | B |\n| --- | --- |\n| hello | 42 |",
                        "confidence": 1,
                        "readingOrder": 1,
                        "engine": "external:test",
                        "shouldTranslate": false,
                        "children": [],
                        "table": {
                          "rowCount": 2,
                          "columnCount": 2,
                          "cells": [
                            { "id": "r0-c0", "rowIndex": 0, "columnIndex": 0, "rowSpan": 1, "columnSpan": 1, "text": "A", "confidence": 1, "shouldTranslate": true },
                            { "id": "r0-c1", "rowIndex": 0, "columnIndex": 1, "rowSpan": 1, "columnSpan": 1, "text": "B", "confidence": 1, "shouldTranslate": true },
                            { "id": "r1-c0", "rowIndex": 1, "columnIndex": 0, "rowSpan": 1, "columnSpan": 1, "text": "hello", "confidence": 1, "shouldTranslate": true },
                            { "id": "r1-c1", "rowIndex": 1, "columnIndex": 1, "rowSpan": 1, "columnSpan": 1, "text": "42", "confidence": 1, "shouldTranslate": false }
                          ]
                        }
                      }
                    ]
                  }
                ]
              }
            }
            '@
            """);

        var provider = new ExternalCommandOcrProvider(
            new ExternalOcrOptions
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Image {{image}} -Language {{language}}",
                TimeoutSeconds = 15
            },
            _tempDirectory);

        var result = await provider.RecognizeAsync(
            new OcrProviderRequest(Encoding.UTF8.GetBytes("image"), "image/png", "ja", NormalizeWhitespace: true),
            CancellationToken.None);

        Assert.Equal("x^2 + 1\nA B", result.Text);
        Assert.Equal("external:test", result.Engine);
        var document = Assert.IsType<OcrDocumentResult>(result.Document);
        var page = Assert.Single(document.Pages);
        Assert.Equal(2, page.Blocks.Count);
        Assert.Equal(OcrBlockTypes.Formula, page.Blocks[0].Type);
        Assert.False(page.Blocks[0].ShouldTranslate);
        Assert.Equal("x^2 + 1", page.Blocks[0].Formula?.Latex);
        Assert.Equal(OcrBlockTypes.Table, page.Blocks[1].Type);
        Assert.False(page.Blocks[1].ShouldTranslate);
        Assert.Equal(4, page.Blocks[1].Table?.Cells.Count);
        Assert.False(page.Blocks[1].Table?.Cells.Last().ShouldTranslate);
    }

    [Fact]
    public async Task LocalPythonOcrProvider_UsesPersistentWorkerWhenEnabled()
    {
        Directory.CreateDirectory(_tempDirectory);
        var workerPath = Path.Combine(_tempDirectory, "fake_ocr_worker.py");
        await File.WriteAllTextAsync(
            workerPath,
            """
            import json
            import pathlib
            import sys

            for line in sys.stdin:
                request = json.loads(line)
                text = pathlib.Path(request["image"]).read_bytes().decode("utf-8")
                result = {
                    "text": text + "|" + request["engine"] + "|" + request["preprocess"],
                    "engine": "local:worker-test",
                    "blocks": [
                        {"text": text, "confidence": 1.0}
                    ]
                }
                print(json.dumps({"id": request["id"], "ok": True, "result": result}), flush=True)
            """);

        using var provider = new LocalPythonOcrProvider(
            new OcrProviderDescriptor(
                "paddleocr",
                "PaddleOCR / PP-OCR text",
                "local-python",
                "ja",
                RequiresExternalProcess: true,
                IsLocal: true),
            "paddleocr",
            new LocalOcrSetOptions
            {
                PythonFileName = "python",
                VenvPythonPath = string.Empty,
                ScriptPath = "missing-local-ocr-json.py",
                TimeoutSeconds = 5,
                Worker = new LocalOcrWorkerOptions
                {
                    Enabled = true,
                    ScriptPath = workerPath,
                    TimeoutSeconds = 5,
                    FallbackToOneShot = false,
                    Engines = ["paddleocr"]
                }
            },
            _tempDirectory);

        var result = await provider.RecognizeAsync(
            new OcrProviderRequest(
                Encoding.UTF8.GetBytes("worker image"),
                "image/png",
                "ja",
                NormalizeWhitespace: true,
                PreprocessingPreset: "text-line"),
            CancellationToken.None);

        Assert.Equal("local:worker-test/worker", result.Engine);
        Assert.Equal("worker image|paddleocr|text-line", result.Text);
        Assert.Single(result.Blocks);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class NamedOcrProvider(string name) : IOcrProvider
    {
        public OcrProviderDescriptor Descriptor { get; } = new(
            name,
            name,
            "test",
            "en",
            RequiresExternalProcess: false,
            IsLocal: true);

        public Task<OcrProviderResult> RecognizeAsync(
            OcrProviderRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
