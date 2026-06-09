namespace YomiBridge.Core.Options;

public sealed class YomiBridgeOptions
{
    public string DefaultProvider { get; set; } = "ollama";
    public string DefaultMode { get; set; } = "game_dialogue";
    public string DefaultSource { get; set; } = "ja";
    public string DefaultTarget { get; set; } = "zh-TW";
    public string PresetsDirectory { get; set; } = "presets";
    public string GlossariesDirectory { get; set; } = "glossaries";
    public string CachePath { get; set; } = "data/translations.sqlite";
    public OllamaOptions Ollama { get; set; } = new();
    public OcrOptions Ocr { get; set; } = new();
    public SpeechOptions Speech { get; set; } = new();
    public ContextCompressionOptions ContextCompression { get; set; } = new();
}

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "yomibridge-mort-qwen2.5-0.5b:latest";
    public string[] Models { get; set; } = [];
    public int ModelDiscoveryTimeoutSeconds { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 60;
    public int NumContext { get; set; } = 1024;
    public int NumPredict { get; set; } = 64;
    public double Temperature { get; set; } = 0;
    public string KeepAlive { get; set; } = "30m";
}

public sealed class OcrOptions
{
    public string DefaultProvider { get; set; } = "external";
    public string DefaultLanguage { get; set; } = "ja";
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;
    public bool NormalizeWhitespace { get; set; } = true;
    public ExternalOcrOptions External { get; set; } = new();
    public LocalOcrSetOptions LocalSet { get; set; } = new();
}

public sealed class ExternalOcrOptions
{
    public string FileName { get; set; } = "powershell";
    public string Arguments { get; set; } = "-NoProfile -ExecutionPolicy Bypass -File \"..\\..\\scripts\\windows_ocr_json.ps1\" -Image {image} -Language {language}";
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class LocalOcrSetOptions
{
    public string PythonFileName { get; set; } = "python";
    public string VenvPythonPath { get; set; } = "../../.ocr-set/venv/Scripts/python.exe";
    public string ScriptPath { get; set; } = "../../scripts/local_ocr_json.py";
    public int TimeoutSeconds { get; set; } = 180;
    public int CheckTimeoutSeconds { get; set; } = 5;
}

public sealed class SpeechOptions
{
    public string DefaultProvider { get; set; } = "funasr-http";
    public string DefaultLanguage { get; set; } = "ja";
    public int MaxAudioBytes { get; set; } = 64 * 1024 * 1024;
    public bool PreferCaptions { get; set; } = true;
    public FunAsrHttpOptions FunAsrHttp { get; set; } = new();
    public ExternalSpeechOptions External { get; set; } = new();
    public YouTubeSpeechOptions YouTube { get; set; } = new();
    public LiveSpeechOptions Live { get; set; } = new();
}

public sealed class FunAsrHttpOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string Model { get; set; } = "sensevoice";
    public string ResponseFormat { get; set; } = "verbose_json";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class ExternalSpeechOptions
{
    public string FileName { get; set; } = string.Empty;
    public string Arguments { get; set; } = "{audio} {language}";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class YouTubeSpeechOptions
{
    public string YtDlpFileName { get; set; } = "yt-dlp";
    public string FfmpegFileName { get; set; } = "ffmpeg";
    public string AudioFormat { get; set; } = "bestaudio[abr<=64]/bestaudio/best";
    public string[] CaptionLanguages { get; set; } = ["ja", "ja-JP", "zh-TW", "zh-Hant", "zh-Hans", "zh", "en"];
    public int TimeoutSeconds { get; set; } = 900;
    public int AudioChunkSeconds { get; set; } = 600;
}

public sealed class LiveSpeechOptions
{
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
    public int MaxSegmentSeconds { get; set; } = 8;
    public int SilenceDurationMs { get; set; } = 700;
    public double SilenceRmsThreshold { get; set; } = 0.01;
}

public sealed class ContextCompressionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxCharacters { get; set; } = 1800;
    public int HeadCharacters { get; set; } = 900;
    public int TailCharacters { get; set; } = 700;
}
