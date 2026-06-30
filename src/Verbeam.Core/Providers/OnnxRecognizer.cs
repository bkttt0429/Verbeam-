using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Verbeam.Core.Providers;

public enum OnnxRecognizerDecodeMask
{
    None,
    Kana,
    Japanese
}

public sealed record OnnxRecognitionCandidate(
    int ClassIndex,
    string Text,
    double Score,
    double Probability);

public sealed record OnnxRecognitionStep(
    int TimeStep,
    int BestClassIndex,
    string BestText,
    double Probability,
    double Margin,
    IReadOnlyList<OnnxRecognitionCandidate> TopCandidates);

public sealed record OnnxRecognitionResult(
    string Text,
    double Confidence,
    IReadOnlyList<OnnxRecognitionStep> Steps);

/// <summary>
/// Self-contained PP-OCR text-line recognizer over raw ONNX Runtime, used to run
/// language-specific recognizers (e.g. <c>japan_PP-OCRv4_rec</c>) that the bundled
/// RapidOcrNet <c>TextRecognizer</c> cannot drive. The CTC convention here was
/// validated against the Python <c>rapidocr</c> output: input <c>1x3x48xW</c>,
/// normalized <c>(px/255 - 0.5) / 0.5</c>, blank = class 0, char index = argmax-1.
/// </summary>
public sealed class OnnxRecognizer : IDisposable
{
    private const int RecHeight = 48;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string[] _chars;
    private readonly bool[] _kanaClasses;
    private readonly bool[] _japaneseClasses;

    public OnnxRecognizer(string modelPath, string keysPath, string executionProvider = "cpu", int deviceId = 0)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"OnnxRecognizer model not found: {modelPath}");
        }

        if (!File.Exists(keysPath))
        {
            throw new FileNotFoundException($"OnnxRecognizer keys not found: {keysPath}");
        }

        // PP-OCR dictionary: one character per line; CTC class 0 is the blank, so the
        // class for argmax index i is dict[i-1]. Keep the newline-derived order exactly.
        _chars = File.ReadAllLines(keysPath);
        _kanaClasses = BuildClassMask(_chars, IsKana);
        _japaneseClasses = BuildClassMask(_chars, ch => IsKana(ch) || IsCjkIdeograph(ch));

        using var options = CreateSessionOptions(executionProvider, deviceId);
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputNames[0];
        _outputName = _session.OutputNames[0];
    }

    private static SessionOptions CreateSessionOptions(string executionProvider, int deviceId)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        switch ((executionProvider ?? "cpu").Trim().ToLowerInvariant())
        {
            case "dml":
            case "directml":
                // DirectML does not support the memory pattern optimizer or parallel
                // execution; configure the session accordingly before appending the EP.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(deviceId);
                break;
            case "cuda":
                options.AppendExecutionProvider_CUDA(deviceId);
                break;
            // "cpu" / unknown -> default CPU provider
        }

        return options;
    }

    public string RecognizeLine(SKBitmap crop)
    {
        // PP-OCR recognizers expect a horizontal line. Vertical (縦書き) detection boxes
        // come through tall-and-narrow; resizing them to height 48 would crush the width
        // to a few pixels. Rotate them flat first (RapidOcrNet's TextRecognizer does the
        // same internally) so the recognizer sees a normal horizontal strip.
        var rotated = crop.Height > crop.Width ? RotateCcw(crop) : null;
        var oriented = rotated ?? crop;
        try
        {
            return RecognizeOriented(oriented).Text;
        }
        finally
        {
            rotated?.Dispose();
        }
    }

    public OnnxRecognitionResult RecognizeConstrained(
        SKBitmap crop,
        OnnxRecognizerDecodeMask mask,
        int topK = 3,
        float blankPenalty = 0)
    {
        var rotated = crop.Height > crop.Width ? RotateCcw(crop) : null;
        var oriented = rotated ?? crop;
        try
        {
            return RecognizeOriented(oriented, mask, topK, blankPenalty);
        }
        finally
        {
            rotated?.Dispose();
        }
    }

    private OnnxRecognitionResult RecognizeOriented(
        SKBitmap crop,
        OnnxRecognizerDecodeMask mask = OnnxRecognizerDecodeMask.None,
        int topK = 0,
        float blankPenalty = 0)
    {
        var input = Preprocess(crop, out var width);
        var tensor = new DenseTensor<float>(input, new[] { 1, 3, RecHeight, width });
        using var results = _session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) },
            new[] { _outputName });

        var output = results.First().AsTensor<float>();
        return CtcDecode(output, mask, topK, blankPenalty);
    }

    public string[] Recognize(IReadOnlyList<SKBitmap> crops)
    {
        var lines = new string[crops.Count];
        for (var i = 0; i < crops.Count; i++)
        {
            lines[i] = crops[i] is null ? string.Empty : RecognizeLine(crops[i]);
        }

        return lines;
    }

    private static SKBitmap RotateCcw(SKBitmap crop)
    {
        var rotated = new SKBitmap(crop.Height, crop.Width, crop.ColorType, crop.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(0, rotated.Height);
        canvas.RotateDegrees(-90);
        canvas.DrawBitmap(crop, 0, 0);
        canvas.Flush();
        return rotated;
    }

    private static float[] Preprocess(SKBitmap crop, out int width)
    {
        var srcW = Math.Max(1, crop.Width);
        var srcH = Math.Max(1, crop.Height);
        width = Math.Max(1, (int)Math.Round((double)RecHeight * srcW / srcH));

        using var resized = crop.Resize(new SKImageInfo(width, RecHeight, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidOperationException("OnnxRecognizer could not resize the line crop.");

        var pixels = resized.Pixels; // SKColor[] row-major
        var plane = RecHeight * width;
        var data = new float[3 * plane];
        // CHW; channel order B,G,R (BGR), normalized (px/255 - 0.5)/0.5.
        for (var y = 0; y < RecHeight; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var c = pixels[rowOffset + x];
                var idx = rowOffset + x;
                data[idx] = (c.Blue / 255f - 0.5f) / 0.5f;
                data[plane + idx] = (c.Green / 255f - 0.5f) / 0.5f;
                data[2 * plane + idx] = (c.Red / 255f - 0.5f) / 0.5f;
            }
        }

        return data;
    }

    private OnnxRecognitionResult CtcDecode(
        Tensor<float> output,
        OnnxRecognizerDecodeMask mask,
        int topK,
        float blankPenalty)
    {
        // output shape [1, T, C]; blank = class 0; char for class p is _chars[p-1].
        var dims = output.Dimensions;
        var timeSteps = dims[1];
        var classes = dims[2];
        var sb = new StringBuilder();
        var confidenceSamples = new List<double>();
        var steps = topK > 0 ? new List<OnnxRecognitionStep>(timeSteps) : [];
        var prev = -1;
        for (var t = 0; t < timeSteps; t++)
        {
            var decoded = DecodeTimeStep(output, t, classes, mask, Math.Max(0, topK), blankPenalty);
            if (decoded.BestClass != 0 && decoded.BestClass != prev)
            {
                var charIndex = decoded.BestClass - 1;
                if (charIndex >= 0 && charIndex < _chars.Length)
                {
                    sb.Append(_chars[charIndex]);
                    confidenceSamples.Add(decoded.Probability);
                }
            }

            if (topK > 0)
            {
                steps.Add(new OnnxRecognitionStep(
                    t,
                    decoded.BestClass,
                    ClassText(decoded.BestClass),
                    decoded.Probability,
                    decoded.Margin,
                    decoded.TopCandidates));
            }

            prev = decoded.BestClass;
        }

        var confidence = confidenceSamples.Count == 0 ? 0 : confidenceSamples.Average();
        return new OnnxRecognitionResult(sb.ToString(), confidence, steps);
    }

    private DecodedStep DecodeTimeStep(
        Tensor<float> output,
        int timeStep,
        int classes,
        OnnxRecognizerDecodeMask mask,
        int topK,
        float blankPenalty)
    {
        var keep = Math.Max(2, topK);
        var topClasses = Enumerable.Repeat(-1, keep).ToArray();
        var topScores = Enumerable.Repeat(float.NegativeInfinity, keep).ToArray();
        var maxScore = float.NegativeInfinity;

        for (var c = 0; c < classes; c++)
        {
            if (!IsClassAllowed(c, mask))
            {
                continue;
            }

            var score = output[0, timeStep, c];
            if (c == 0)
            {
                score -= blankPenalty;
            }

            if (score > maxScore)
            {
                maxScore = score;
            }

            for (var i = 0; i < topScores.Length; i++)
            {
                if (score <= topScores[i])
                {
                    continue;
                }

                for (var j = topScores.Length - 1; j > i; j--)
                {
                    topScores[j] = topScores[j - 1];
                    topClasses[j] = topClasses[j - 1];
                }

                topScores[i] = score;
                topClasses[i] = c;
                break;
            }
        }

        var denominator = 0d;
        if (!float.IsNegativeInfinity(maxScore))
        {
            for (var c = 0; c < classes; c++)
            {
                if (!IsClassAllowed(c, mask))
                {
                    continue;
                }

                var score = output[0, timeStep, c];
                if (c == 0)
                {
                    score -= blankPenalty;
                }

                denominator += Math.Exp(score - maxScore);
            }
        }

        var bestClass = topClasses[0] < 0 ? 0 : topClasses[0];
        var bestScore = topScores[0];
        var secondScore = topScores.Length > 1 ? topScores[1] : float.NegativeInfinity;
        var bestProbability = Probability(bestScore, maxScore, denominator);
        var margin = float.IsNegativeInfinity(secondScore) ? 0 : bestScore - secondScore;
        var candidates = topK <= 0
            ? []
            : BuildTopCandidates(topClasses, topScores, topK, maxScore, denominator);

        return new DecodedStep(bestClass, bestProbability, margin, candidates);
    }

    private IReadOnlyList<OnnxRecognitionCandidate> BuildTopCandidates(
        int[] topClasses,
        float[] topScores,
        int topK,
        float maxScore,
        double denominator)
    {
        var count = Math.Min(topK, topClasses.Length);
        var candidates = new List<OnnxRecognitionCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            if (topClasses[i] < 0)
            {
                break;
            }

            candidates.Add(new OnnxRecognitionCandidate(
                topClasses[i],
                ClassText(topClasses[i]),
                topScores[i],
                Probability(topScores[i], maxScore, denominator)));
        }

        return candidates;
    }

    private static double Probability(float score, float maxScore, double denominator)
        => denominator <= 0 || float.IsNegativeInfinity(score) || float.IsNegativeInfinity(maxScore)
            ? 0
            : Math.Exp(score - maxScore) / denominator;

    private bool IsClassAllowed(int classIndex, OnnxRecognizerDecodeMask mask)
    {
        if (classIndex == 0 || mask == OnnxRecognizerDecodeMask.None)
        {
            return true;
        }

        var charIndex = classIndex - 1;
        if (charIndex < 0 || charIndex >= _chars.Length)
        {
            return false;
        }

        return mask switch
        {
            OnnxRecognizerDecodeMask.Kana => _kanaClasses[charIndex],
            OnnxRecognizerDecodeMask.Japanese => _japaneseClasses[charIndex],
            _ => true
        };
    }

    private string ClassText(int classIndex)
    {
        var charIndex = classIndex - 1;
        return charIndex >= 0 && charIndex < _chars.Length ? _chars[charIndex] : string.Empty;
    }

    private static bool[] BuildClassMask(string[] chars, Func<string, bool> predicate)
    {
        var mask = new bool[chars.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            mask[i] = predicate(chars[i]);
        }

        return mask;
    }

    private static bool IsKana(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!IsKana(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsKana(char ch)
        => (ch >= '\u3040' && ch <= '\u30ff') ||
           (ch >= '\u31f0' && ch <= '\u31ff') ||
           (ch >= '\uff66' && ch <= '\uff9f');

    private static bool IsCjkIdeograph(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!IsCjkIdeograph(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCjkIdeograph(char ch)
        => (ch >= '\u3400' && ch <= '\u4dbf') ||
           (ch >= '\u4e00' && ch <= '\u9fff') ||
           (ch >= '\uf900' && ch <= '\ufaff');

    private readonly record struct DecodedStep(
        int BestClass,
        double Probability,
        double Margin,
        IReadOnlyList<OnnxRecognitionCandidate> TopCandidates);

    public void Dispose() => _session.Dispose();
}
