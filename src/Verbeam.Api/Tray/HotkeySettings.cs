using System.Text.Json;
using System.Windows.Forms;
using Verbeam.Core.Options;

namespace Verbeam.Api.Tray;

public enum HotkeyAction
{
    Snapshot,
    ToggleLoop,
    CaptureRegions,
    NextProfile,
    PrevProfile,
    ToggleOverlays
}

public sealed record HotkeyDefinition(
    string Id,
    string Group,
    string Label,
    string Description,
    string DefaultSpec);

public sealed record HotkeyBindingView(
    string Action,
    string Group,
    string Label,
    string Description,
    string Spec,
    string DefaultSpec,
    bool Enabled,
    bool IsDefault,
    string Status,
    string Message);

public sealed record HotkeySettingsView(
    int SchemaVersion,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<HotkeyBindingView> Bindings);

public sealed record HotkeySaveRequest(Dictionary<string, string?> Bindings);

public sealed record HotkeySettingsDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Dictionary<string, string> Bindings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record HotkeyRegistrationView(string Action, string Status, string Message);

public sealed class HotkeySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly HotkeyDefinitionTemplate[] DefinitionTemplates =
    [
        new(HotkeyAction.Snapshot, "Native region", "Region snapshot", "Capture one native region frame and reselect the source surface."),
        new(HotkeyAction.ToggleLoop, "Native region", "Toggle loop", "Start or pause continuous OCR and translation for the current native region."),
        new(HotkeyAction.CaptureRegions, "Profiles", "Capture profile regions", "Draw screen regions and save them to the active game profile."),
        new(HotkeyAction.NextProfile, "Profiles", "Next profile", "Switch to the next game profile and apply its saved regions."),
        new(HotkeyAction.PrevProfile, "Profiles", "Previous profile", "Switch to the previous game profile and apply its saved regions."),
        new(HotkeyAction.ToggleOverlays, "Display", "Toggle overlays", "Show or hide native translation overlay windows.")
    ];

    private readonly string _path;
    private readonly Dictionary<string, string> _defaults;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HotkeySettingsService(HotkeyOptions defaults, string path)
    {
        _path = path;
        _defaults = BuildBindings(defaults);
    }

    public IReadOnlyList<HotkeyDefinition> Definitions
        => DefinitionTemplates
            .Select(template => new HotkeyDefinition(
                ActionId(template.Action),
                template.Group,
                template.Label,
                template.Description,
                _defaults.GetValueOrDefault(ActionId(template.Action), string.Empty)))
            .ToArray();

    public Dictionary<string, string> DefaultBindings => new(_defaults, StringComparer.OrdinalIgnoreCase);

    public static string ActionId(HotkeyAction action) => action.ToString();

    public static bool TryParseAction(string? value, out HotkeyAction action)
        => Enum.TryParse(value, ignoreCase: true, out action) && Enum.IsDefined(action);

    public async Task<Dictionary<string, string>> LoadEffectiveBindingsAsync(CancellationToken cancellationToken = default)
    {
        var bindings = DefaultBindings;
        var document = await LoadDocumentAsync(cancellationToken);
        if (document is not null)
        {
            foreach (var (action, spec) in document.Bindings)
            {
                if (TryParseAction(action, out var parsed))
                {
                    bindings[ActionId(parsed)] = NormalizeOrEmpty(spec);
                }
            }
        }

        return bindings;
    }

    public async Task ApplyPersistedAsync(HotkeyOptions options, CancellationToken cancellationToken = default)
    {
        var bindings = await LoadEffectiveBindingsAsync(cancellationToken);
        ApplyToOptions(options, bindings);
    }

    public async Task<Dictionary<string, string>> SaveAsync(
        IReadOnlyDictionary<string, string?> request,
        CancellationToken cancellationToken = default)
    {
        var bindings = DefaultBindings;
        foreach (var (action, value) in request)
        {
            if (!TryParseAction(action, out var parsed))
            {
                throw new InvalidOperationException($"Unknown hotkey action '{action}'.");
            }

            bindings[ActionId(parsed)] = NormalizeOrEmpty(value);
        }

        Validate(bindings);

        var document = new HotkeySettingsDocument
        {
            Bindings = bindings,
            UpdatedAt = DateTimeOffset.UtcNow
        };

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

        return bindings;
    }

    public async Task<Dictionary<string, string>> ResetAsync(CancellationToken cancellationToken = default)
    {
        var bindings = DefaultBindings;
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

        return bindings;
    }

    public void ApplyToOptions(HotkeyOptions options, IReadOnlyDictionary<string, string> bindings)
    {
        options.Snapshot = bindings.GetValueOrDefault(ActionId(HotkeyAction.Snapshot), string.Empty);
        options.ToggleLoop = bindings.GetValueOrDefault(ActionId(HotkeyAction.ToggleLoop), string.Empty);
        options.CaptureRegions = bindings.GetValueOrDefault(ActionId(HotkeyAction.CaptureRegions), string.Empty);
        options.NextProfile = bindings.GetValueOrDefault(ActionId(HotkeyAction.NextProfile), string.Empty);
        options.PrevProfile = bindings.GetValueOrDefault(ActionId(HotkeyAction.PrevProfile), string.Empty);
        options.ToggleOverlays = bindings.GetValueOrDefault(ActionId(HotkeyAction.ToggleOverlays), string.Empty);
    }

    public static string NormalizeOrEmpty(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return string.Empty;
        }

        if (!HotkeySpec.TryParse(spec, out var modifiers, out var virtualKey))
        {
            throw new InvalidOperationException($"'{spec}' is not a valid hotkey.");
        }

        var tokens = new List<string>(5);
        if ((modifiers & HotkeySpec.ModControl) != 0) tokens.Add("Ctrl");
        if ((modifiers & HotkeySpec.ModAlt) != 0) tokens.Add("Alt");
        if ((modifiers & HotkeySpec.ModShift) != 0) tokens.Add("Shift");
        if ((modifiers & HotkeySpec.ModWin) != 0) tokens.Add("Win");
        tokens.Add(((Keys)virtualKey).ToString());
        return string.Join("+", tokens);
    }

    public static void Validate(IReadOnlyDictionary<string, string> bindings)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (action, spec) in bindings)
        {
            if (string.IsNullOrWhiteSpace(spec))
            {
                continue;
            }

            if (!HotkeySpec.TryParse(spec, out var modifiers, out var virtualKey))
            {
                throw new InvalidOperationException($"'{spec}' is not a valid hotkey.");
            }

            var signature = $"{modifiers}:{virtualKey}";
            if (seen.TryGetValue(signature, out var otherAction))
            {
                throw new InvalidOperationException($"{spec} is already assigned to {otherAction}.");
            }

            seen[signature] = action;
        }
    }

    private async Task<HotkeySettingsDocument?> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<HotkeySettingsDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Dictionary<string, string> BuildBindings(HotkeyOptions options)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [ActionId(HotkeyAction.Snapshot)] = NormalizeOrEmpty(options.Snapshot),
            [ActionId(HotkeyAction.ToggleLoop)] = NormalizeOrEmpty(options.ToggleLoop),
            [ActionId(HotkeyAction.CaptureRegions)] = NormalizeOrEmpty(options.CaptureRegions),
            [ActionId(HotkeyAction.NextProfile)] = NormalizeOrEmpty(options.NextProfile),
            [ActionId(HotkeyAction.PrevProfile)] = NormalizeOrEmpty(options.PrevProfile),
            [ActionId(HotkeyAction.ToggleOverlays)] = NormalizeOrEmpty(options.ToggleOverlays)
        };

    private sealed record HotkeyDefinitionTemplate(
        HotkeyAction Action,
        string Group,
        string Label,
        string Description);
}

public sealed class HotkeyRuntimeService
{
    private readonly HotkeySettingsService _settings;
    private readonly object _gate = new();
    private Dictionary<string, HotkeyRegistrationView> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public HotkeyRuntimeService(HotkeySettingsService settings)
    {
        _settings = settings;
    }

    public event Action<IReadOnlyDictionary<string, string>>? BindingsChanged;

    public async Task<HotkeySettingsView> GetAsync(CancellationToken cancellationToken = default)
    {
        var bindings = await _settings.LoadEffectiveBindingsAsync(cancellationToken);
        return BuildView(bindings);
    }

    public async Task<HotkeySettingsView> SaveAsync(HotkeySaveRequest request, CancellationToken cancellationToken = default)
    {
        var bindings = await _settings.SaveAsync(request.Bindings, cancellationToken);
        BindingsChanged?.Invoke(bindings);
        return BuildView(bindings);
    }

    public async Task<HotkeySettingsView> ResetAsync(CancellationToken cancellationToken = default)
    {
        var bindings = await _settings.ResetAsync(cancellationToken);
        BindingsChanged?.Invoke(bindings);
        return BuildView(bindings);
    }

    public void SetRegistrationStatus(IEnumerable<HotkeyRegistrationView> registrations)
    {
        lock (_gate)
        {
            _registrations = registrations.ToDictionary(item => item.Action, StringComparer.OrdinalIgnoreCase);
        }
    }

    private HotkeySettingsView BuildView(IReadOnlyDictionary<string, string> bindings)
    {
        Dictionary<string, HotkeyRegistrationView> registrations;
        lock (_gate)
        {
            registrations = new Dictionary<string, HotkeyRegistrationView>(_registrations, StringComparer.OrdinalIgnoreCase);
        }

        var views = _settings.Definitions.Select(definition =>
        {
            var spec = bindings.GetValueOrDefault(definition.Id, string.Empty);
            var enabled = !string.IsNullOrWhiteSpace(spec);
            var registration = registrations.GetValueOrDefault(definition.Id);
            var status = enabled ? registration?.Status ?? "pending" : "disabled";
            var message = enabled
                ? registration?.Message ?? "Will register when tray mode is active."
                : "Disabled. Assign a shortcut to enable this action.";
            return new HotkeyBindingView(
                definition.Id,
                definition.Group,
                definition.Label,
                definition.Description,
                spec,
                definition.DefaultSpec,
                enabled,
                string.Equals(spec, definition.DefaultSpec, StringComparison.OrdinalIgnoreCase),
                status,
                message);
        }).ToArray();

        return new HotkeySettingsView(1, DateTimeOffset.UtcNow, views);
    }
}
