using System.Text.Json;
using Verbeam.Core.Options;

namespace Verbeam.Api.Tray;

public sealed record ShellModeOption(string Id, string Label, string Description, string Restart);

public sealed record BrowserRegionQualityOption(string Id, string Label, string Description, int Width, int Height, int Fps);

public sealed record ShellSettingsView(
    int SchemaVersion,
    DateTimeOffset UpdatedAt,
    string WebView2GpuMode,
    string WebView2AdditionalArgs,
    string BrowserRegionQuality,
    IReadOnlyList<ShellModeOption> WebView2GpuModes,
    IReadOnlyList<BrowserRegionQualityOption> BrowserRegionQualities,
    bool RequiresRestart);

public sealed record ShellSettingsSaveRequest(
    string? WebView2GpuMode,
    string? WebView2AdditionalArgs,
    string? BrowserRegionQuality);

public sealed record ShellSettingsDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string WebView2GpuMode { get; init; } = "balanced";
    public string WebView2AdditionalArgs { get; init; } = string.Empty;
    public string BrowserRegionQuality { get; init; } = "balanced";
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ShellSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly ShellModeOption[] GpuModes =
    [
        new("saver", "VRAM Saver", "Disable WebView2 GPU paths. Best when Native Region is the main workflow.", "restart"),
        new("balanced", "Balanced preview", "Reduce GPU compositing while keeping Browser Region preview reliable.", "restart"),
        new("performance", "Performance", "Keep GPU acceleration for heavy Browser Region preview work.", "restart"),
        new("custom", "Custom", "Use the custom Chromium flags below.", "restart")
    ];

    private static readonly BrowserRegionQualityOption[] BrowserQualities =
    [
        new("saver", "720p / 30fps", "Lowest memory use; enough for large subtitles.", 1280, 720, 30),
        new("balanced", "1080p / 30fps", "Recommended default: visible preview with moderate memory use.", 1920, 1080, 30),
        new("ocr", "1440p / 30fps", "Sharper small text with lower cost than 4K.", 2560, 1440, 30),
        new("max", "4K / 60fps", "Maximum Browser Region preview quality; highest memory and GPU cost.", 3840, 2160, 60)
    ];

    private readonly string _path;
    private readonly ShellOptions _defaults;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ShellSettingsService(ShellOptions defaults, string path)
    {
        _defaults = defaults;
        _path = path;
    }

    public async Task ApplyPersistedAsync(ShellOptions options, CancellationToken cancellationToken = default)
    {
        var document = await LoadEffectiveDocumentAsync(cancellationToken);
        options.WebView2GpuMode = document.WebView2GpuMode;
        options.WebView2AdditionalArgs = document.WebView2AdditionalArgs;
        options.BrowserRegionQuality = document.BrowserRegionQuality;
    }

    public async Task<ShellSettingsView> GetAsync(CancellationToken cancellationToken = default)
    {
        var document = await LoadEffectiveDocumentAsync(cancellationToken);
        return ToView(document, requiresRestart: false);
    }

    public async Task<ShellSettingsView> SaveAsync(ShellSettingsSaveRequest request, CancellationToken cancellationToken = default)
    {
        var current = await LoadEffectiveDocumentAsync(cancellationToken);
        var document = Normalize(new ShellSettingsDocument
        {
            WebView2GpuMode = string.IsNullOrWhiteSpace(request.WebView2GpuMode)
                ? current.WebView2GpuMode
                : request.WebView2GpuMode!,
            WebView2AdditionalArgs = request.WebView2AdditionalArgs ?? current.WebView2AdditionalArgs,
            BrowserRegionQuality = string.IsNullOrWhiteSpace(request.BrowserRegionQuality)
                ? current.BrowserRegionQuality
                : request.BrowserRegionQuality!,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        finally
        {
            _gate.Release();
        }

        return ToView(document, RequiresWebViewRestart(current, document));
    }

    public async Task<ShellSettingsView> ResetAsync(CancellationToken cancellationToken = default)
    {
        var current = await LoadEffectiveDocumentAsync(cancellationToken);
        var document = Normalize(new ShellSettingsDocument
        {
            WebView2GpuMode = _defaults.WebView2GpuMode,
            WebView2AdditionalArgs = _defaults.WebView2AdditionalArgs,
            BrowserRegionQuality = _defaults.BrowserRegionQuality,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        finally
        {
            _gate.Release();
        }

        return ToView(document, RequiresWebViewRestart(current, document));
    }

    public void ApplyToOptions(ShellOptions options, ShellSettingsView view)
    {
        options.WebView2GpuMode = view.WebView2GpuMode;
        options.WebView2AdditionalArgs = view.WebView2AdditionalArgs;
        options.BrowserRegionQuality = view.BrowserRegionQuality;
    }

    public static string BuildWebView2Arguments(ShellOptions options)
    {
        var mode = NormalizeMode(options.WebView2GpuMode);
        var args = mode switch
        {
            "saver" => "--disable-gpu --disable-accelerated-video-decode --disable-accelerated-2d-canvas --enable-low-end-device-mode --renderer-process-limit=1",
            "performance" => "--renderer-process-limit=2",
            "custom" => string.Empty,
            _ => "--disable-gpu-compositing --disable-accelerated-2d-canvas --enable-low-end-device-mode --renderer-process-limit=1"
        };

        var custom = (options.WebView2AdditionalArgs ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(custom) ? args : $"{args} {custom}".Trim();
    }

    public static BrowserRegionQualityOption ResolveBrowserRegionQuality(string? id)
    {
        var normalized = NormalizeQuality(id);
        return BrowserQualities.FirstOrDefault(item => item.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? BrowserQualities[1];
    }

    private static bool RequiresWebViewRestart(ShellSettingsDocument before, ShellSettingsDocument after) =>
        !string.Equals(NormalizeMode(before.WebView2GpuMode), NormalizeMode(after.WebView2GpuMode), StringComparison.OrdinalIgnoreCase)
        || !string.Equals(
            (before.WebView2AdditionalArgs ?? string.Empty).Trim(),
            (after.WebView2AdditionalArgs ?? string.Empty).Trim(),
            StringComparison.Ordinal);

    private async Task<ShellSettingsDocument> LoadEffectiveDocumentAsync(CancellationToken cancellationToken)
    {
        ShellSettingsDocument? document = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
            {
                await using var stream = File.OpenRead(_path);
                document = await JsonSerializer.DeserializeAsync<ShellSettingsDocument>(stream, JsonOptions, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        document ??= new ShellSettingsDocument
        {
            WebView2GpuMode = _defaults.WebView2GpuMode,
            WebView2AdditionalArgs = _defaults.WebView2AdditionalArgs,
            BrowserRegionQuality = _defaults.BrowserRegionQuality,
            UpdatedAt = DateTimeOffset.MinValue
        };

        return Normalize(document);
    }

    private static ShellSettingsView ToView(ShellSettingsDocument document, bool requiresRestart)
        => new(
            document.SchemaVersion,
            document.UpdatedAt,
            document.WebView2GpuMode,
            document.WebView2AdditionalArgs,
            document.BrowserRegionQuality,
            GpuModes,
            BrowserQualities,
            requiresRestart);

    private static ShellSettingsDocument Normalize(ShellSettingsDocument document)
        => document with
        {
            WebView2GpuMode = NormalizeMode(document.WebView2GpuMode),
            WebView2AdditionalArgs = (document.WebView2AdditionalArgs ?? string.Empty).Trim(),
            BrowserRegionQuality = NormalizeQuality(document.BrowserRegionQuality),
            UpdatedAt = document.UpdatedAt == default ? DateTimeOffset.UtcNow : document.UpdatedAt
        };

    private static string NormalizeMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return GpuModes.Any(mode => mode.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : "balanced";
    }

    private static string NormalizeQuality(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return BrowserQualities.Any(quality => quality.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : "balanced";
    }
}
