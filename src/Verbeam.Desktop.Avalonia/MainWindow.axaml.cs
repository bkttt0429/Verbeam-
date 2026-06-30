using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using FluentIcons.Avalonia;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Svg.Skia;

namespace Verbeam.Desktop.Avalonia;

public partial class MainWindow : Window
{
    private static readonly IBrush TopTabActiveBrush = Brush.Parse("#93C5FD");
    private static readonly IBrush TopTabInactiveBrush = Brush.Parse("#8A98AA");
    private static readonly IBrush SideNavActiveBrush = Brush.Parse("#5EA0FF");
    private static readonly IBrush SideNavInactiveBrush = Brush.Parse("#B8C7DA");
    private static readonly string[] SupplierFallbackColors =
    [
        "#4F46E5",
        "#7C3AED",
        "#DB2777",
        "#0D9488",
        "#9333EA",
        "#E11D48"
    ];
    private static readonly Dictionary<string, string> SupplierIconByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aihubmix"] = "aihubmix-color.svg",
        ["aicodemirror"] = "aicodemirror.svg",
        ["alibaba"] = "alibaba.svg",
        ["api"] = "openai.svg",
        ["apicompatible"] = "openai.svg",
        ["anthropic"] = "anthropic.svg",
        ["baidu"] = "baidu.svg",
        ["bailian"] = "bailian.svg",
        ["bailiancoding"] = "bailian.svg",
        ["claude"] = "anthropic.svg",
        ["cohere"] = "cohere-color.svg",
        ["compsharecoding"] = "ucloud.svg",
        ["deepl"] = "deepl.svg",
        ["deepseek"] = "deepseek.svg",
        ["doubao"] = "doubao.svg",
        ["eflowcode"] = "eflowcode.png",
        ["gemini"] = "gemini-color.svg",
        ["gemma"] = "gemma-color.svg",
        ["google"] = "google-color.svg",
        ["huggingface"] = "huggingface-color.svg",
        ["huoshan"] = "huoshan.png",
        ["kimi"] = "kimi.svg",
        ["kimicoding"] = "kimi.svg",
        ["lmstudio"] = "lmstudio.svg",
        ["meta"] = "meta-color.svg",
        ["metaai"] = "metaai-color.svg",
        ["minimax"] = "minimax.svg",
        ["minimaxcoding"] = "minimax.svg",
        ["mistral"] = "mistral-color.svg",
        ["modelscope"] = "modelscope-color.svg",
        ["moonshot"] = "moonshot.svg",
        ["nvidia"] = "nvidia.svg",
        ["ollama"] = "ollama.svg",
        ["openai"] = "openai.svg",
        ["opencodego"] = "opencode.svg",
        ["openrouter"] = "openrouter.svg",
        ["packycode"] = "packycode.svg",
        ["perplexity"] = "perplexity-color.svg",
        ["pipellm"] = "pipellm.png",
        ["qianfancoding"] = "baidu.svg",
        ["qwen"] = "qwen.svg",
        ["relaxycode"] = "relaxcode.png",
        ["groq"] = "groq.svg",
        ["ai21"] = "ai21.svg",
        ["together"] = "together-color.svg",
        ["azure"] = "azure-color.svg",
        ["aws"] = "aws-color.svg",
        ["bedrock"] = "bedrock-color.svg",
        ["rightcode"] = "rc.svg",
        ["siliconflow"] = "siliconflow.svg",
        ["siliconcloud"] = "siliconcloud-color.svg",
        ["sssaicode"] = "sssaicode.svg",
        ["stepfun"] = "stepfun.svg",
        ["sudocode"] = "sudocode.png",
        ["tencent"] = "tencent-color.svg",
        ["tencentcloud"] = "tencentcloud-color.svg",
        ["zhipu"] = "zhipu.svg"
    };
    private static readonly Dictionary<string, string> SupplierColorByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api"] = "#047857",
        ["apicompatible"] = "#047857",
        ["deepl"] = "#0F6F9E",
        ["deepseek"] = "#0284C7",
        ["openrouter"] = "#4F46E5",
        ["openai"] = "#047857",
        ["anthropic"] = "#B45309",
        ["google"] = "#2563EB",
        ["gemma"] = "#2563EB",
        ["qwen"] = "#4F46E5",
        ["tencent"] = "#2563EB",
        ["tencentcloud"] = "#2563EB",
        ["zhipu"] = "#1D4ED8",
        ["kimi"] = "#4338CA",
        ["moonshot"] = "#4338CA",
        ["lmstudio"] = "#111827",
        ["metaai"] = "#2563EB",
        ["minimax"] = "#E11D48",
        ["modelscope"] = "#4F46E5",
        ["stepfun"] = "#0F766E",
        ["doubao"] = "#1D4ED8",
        ["nvidia"] = "#4D7C0F",
        ["ollama"] = "#334155",
        ["llamacpp"] = "#1D4ED8",
        ["mock"] = "#475569",
        ["custom"] = "#7C3AED"
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private bool _isRefreshing;
    private bool _suppressProviderReload;
    private bool _suppressSettingsSync;
    private bool _suppressDocumentSelection;
    private string _defaultProvider = "llama-cpp";
    private string _defaultModel = string.Empty;
    private string _defaultSource = "auto";
    private string _defaultTarget = "zh-TW";
    private string _defaultMode = "game_dialogue";
    private string _defaultOcrProvider = "auto";
    private string _defaultSpeechProvider = "funasr-http";
    private string _ocrImageBase64 = string.Empty;
    private string _ocrImageMimeType = string.Empty;
    private string _audioBase64 = string.Empty;
    private string _audioMimeType = string.Empty;
    private string _documentPath = string.Empty;
    private string _documentFileName = string.Empty;
    private string _documentContentType = string.Empty;
    private string _documentJobId = string.Empty;
    private string _documentPollingJobId = string.Empty;
    private bool _translateSplitStacked;
    private bool _ocrSplitStacked;
    private bool _audioSplitStacked;
    private bool _regionSplitStacked;
    private string _settingsSection = "general";
    private readonly List<ProviderOptionDto> _providerCatalog = [];
    private readonly List<ModelOptionDto> _modelCatalog = [];
    private readonly List<ApiSupplierPresetDto> _apiSupplierPresets = [];
    private readonly List<ApiSupplierProfileDto> _apiSuppliers = [];
    private readonly List<MemoryItemDto> _memoryLibraryItems = [];
    private readonly List<MemoryItemDto> _memoryReviewItems = [];
    private string _editingApiSupplierId = string.Empty;
    private string _activeApiSupplierId = string.Empty;
    private string _selectedMemoryId = string.Empty;
    private string _selectedMemoryReviewId = string.Empty;
    private string _memoryCenterSection = "library";
    private string _selectedProviderCatalogCardId = string.Empty;
    private bool _providerCardSelectionPinned;
    private bool _suppressMemoryLibraryRefresh;
    private bool _memoryAdvancedFiltersVisible;
    private bool _memoryEditorAdvancedVisible;
    private double _providerModelMb;
    private double _providerGpuUsedMb;
    private double _providerGpuTotalMb;
    private long _providerTokenTotal;

    public MainWindow()
        : this("translate")
    {
    }

    public MainWindow(string startupWorkspace)
        : this(startupWorkspace, "general")
    {
    }

    public MainWindow(string startupWorkspace, string startupSettingsSection)
    {
        _settingsSection = string.IsNullOrWhiteSpace(startupSettingsSection)
            ? "general"
            : startupSettingsSection.Trim().ToLowerInvariant();
        InitializeComponent();
        if (SettingsProviderFilterSelect.SelectedIndex < 0)
        {
            SettingsProviderFilterSelect.SelectedIndex = 0;
        }

        InitializeMemoryLibraryControls();

        SourceLanguageSelect.SelectionChanged += (_, _) =>
        {
            var source = SelectedId(SourceLanguageSelect, _defaultSource);
            SyncComboSelection(SettingsSourceLanguageSelect, source);
            SyncComboSelection(SettingsOcrLanguageSelect, source);
            SyncComboSelection(SettingsAudioLanguageSelect, source);
            UpdateTranslateSummary();
        };
        TargetLanguageSelect.SelectionChanged += (_, _) =>
        {
            SyncComboSelection(SettingsTargetLanguageSelect, SelectedId(TargetLanguageSelect, _defaultTarget));
            UpdateTranslateSummary();
        };
        ModelSelect.SelectionChanged += (_, _) =>
        {
            SyncComboSelection(SettingsModelSelect, SelectedId(ModelSelect, _defaultModel));
            UpdateTranslateSummary();
        };
        OcrProviderSelect.SelectionChanged += (_, _) =>
            SyncComboSelection(SettingsOcrProviderSelect, SelectedId(OcrProviderSelect, _defaultOcrProvider));
        AudioProviderSelect.SelectionChanged += (_, _) =>
            SyncComboSelection(SettingsAudioProviderSelect, SelectedId(AudioProviderSelect, _defaultSpeechProvider));
        SettingsSourceLanguageSelect.SelectionChanged += (_, _) => SettingsLanguageSelect_OnSelectionChanged(SourceLanguageSelect, SettingsSourceLanguageSelect, _defaultSource);
        SettingsTargetLanguageSelect.SelectionChanged += (_, _) => SettingsLanguageSelect_OnSelectionChanged(TargetLanguageSelect, SettingsTargetLanguageSelect, _defaultTarget);
        SettingsPresetSelect.SelectionChanged += (_, _) =>
        {
            if (_suppressSettingsSync)
            {
                return;
            }

            PresetBox.Text = ComboValue(SettingsPresetSelect, _defaultMode);
            _defaultMode = PresetBox.Text ?? _defaultMode;
            UpdateTranslateSummary();
        };
        ToolTip.SetTip(TranslationTokenText, "No token usage yet.");
        ToolTip.SetTip(PanelTokenText, "No token usage yet.");
        ToolTip.SetTip(ResultTokenPill, "No token usage yet.");
        UpdateCopyHoverState();
        ApplyWorkspace(startupWorkspace);
        ApplySettingsSection(_settingsSection);
        UpdateResponsiveState(Width);

        SizeChanged += MainWindow_OnSizeChanged;
        Opened += async (_, _) => await RefreshAsync();
    }

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveState(e.NewSize.Width);
    }

    private void WorkspaceScrollViewer_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer { Content: Control content })
        {
            content.Width = Math.Max(0, e.NewSize.Width);
        }
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void WorkspaceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string workspace)
        {
            ApplyWorkspace(workspace);
        }
    }

    private void SettingsSectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string section)
        {
            ApplySettingsSection(section);
        }
    }

    private async void SettingsRefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void SettingsShellSaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await SaveShellSettingsAsync();
    }

    private async void SettingsShellResetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ResetShellSettingsAsync();
    }

    private async void SettingsHotkeyRefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await LoadHotkeysAsync(NormalizeBaseUrl(ApiUrlBox.Text));
    }

    private async void SettingsHotkeyResetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ResetHotkeysAsync();
    }

    private async void SettingsMemoryScopeRefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await LoadMemoryScopeAsync(NormalizeBaseUrl(ApiUrlBox.Text));
    }

    private async void SettingsProviderSelect_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        var provider = SelectedId(SettingsProviderSelect, _defaultProvider);
        _suppressProviderReload = true;
        try
        {
            SyncComboSelection(ProviderSelect, provider);
        }
        finally
        {
            _suppressProviderReload = false;
        }

        _selectedProviderCatalogCardId = string.Empty;
        _providerCardSelectionPinned = false;
        RenderProviderCatalog();
        await LoadModelsForSelectedProviderAsync();
    }

    private void SettingsModelSelect_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        SyncComboSelection(ModelSelect, SelectedId(SettingsModelSelect, _defaultModel));
        UpdateProviderSettingsSummary();
        RenderModelCatalog();
        RenderProviderDetail();
        UpdateTranslateSummary();
    }

    private void SettingsProviderFilterSelect_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyProviderFilterButtonState();
        RenderProviderCatalog();
        RenderModelCatalog();
    }

    private void SettingsProviderFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter } || string.IsNullOrWhiteSpace(filter))
        {
            return;
        }

        SelectComboContent(SettingsProviderFilterSelect, filter);
        ApplyProviderFilterButtonState();
        RenderProviderCatalog();
        RenderModelCatalog();
    }

    private void ApplyProviderFilterButtonState()
    {
        var filter = NormalizeProviderCategory(ComboValue(SettingsProviderFilterSelect, "All"));
        SettingsProviderFilterAllButton.Classes.Set("active", filter is "all" or "");
        SettingsProviderFilterLocalButton.Classes.Set("active", filter.Equals("local", StringComparison.OrdinalIgnoreCase));
        SettingsProviderFilterOfficialButton.Classes.Set("active", filter.Equals("official", StringComparison.OrdinalIgnoreCase));
        SettingsProviderFilterApiButton.Classes.Set("active", filter.Equals("third_party", StringComparison.OrdinalIgnoreCase));
    }

    private async void SettingsRefreshSuppliersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RefreshApiSupplierPresetsAsync();
        await LoadApiSuppliersAsync(NormalizeBaseUrl(ApiUrlBox.Text));
    }

    private void SettingsAddApiSupplierButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenApiSupplierEditor(null);
    }

    private void SettingsApplyRecommendedModelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedCard = SelectedProviderCatalogCard();
        if (selectedCard?.Source.Equals("preset", StringComparison.OrdinalIgnoreCase) == true)
        {
            var supplier = SupplierForProviderCard(selectedCard);
            var supplierModel = supplier?.ModelCatalog.FirstOrDefault();
            if (supplierModel is not null)
            {
                SettingsApiSupplierModelBox.Text = Pick(supplierModel.Id, supplierModel.DisplayName);
                SettingsApiSupplierStatusText.Text = $"Recommended supplier model: {Pick(supplierModel.DisplayName, supplierModel.Id)}.";
            }
            return;
        }

        var model = _modelCatalog
            .OrderByDescending(IsRecommendedModel)
            .ThenByDescending(model => model.IsDefault)
            .ThenByDescending(model => model.IsInstalled)
            .FirstOrDefault();
        if (model is null)
        {
            SettingsRecommendedText.Text = "No recommended model is available for this provider.";
            return;
        }

        _suppressSettingsSync = true;
        try
        {
            SelectById(SettingsModelSelect, model.Name);
            SelectById(ModelSelect, model.Name);
        }
        finally
        {
            _suppressSettingsSync = false;
        }

        UpdateProviderSettingsSummary();
        RenderModelCatalog();
        RenderProviderDetail();
        UpdateTranslateSummary();
    }

    private void SettingsApiSupplierCancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _editingApiSupplierId = string.Empty;
        SettingsApiSupplierEditorPanel.IsVisible = false;
    }

    private void SettingsApiSupplierPresetSelect_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_editingApiSupplierId))
        {
            return;
        }

        ApplySelectedApiSupplierPreset();
    }

    private async void SettingsApiSupplierSaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await SaveApiSupplierAsync();
    }

    private void SettingsOcrProviderSelect_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        SyncComboSelection(OcrProviderSelect, SelectedId(SettingsOcrProviderSelect, _defaultOcrProvider));
        SettingsOcrStatusText.Text = "OCR engine updated. The next OCR run will use this route.";
    }

    private void SettingsAudioProviderSelect_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        SyncComboSelection(AudioProviderSelect, SelectedId(SettingsAudioProviderSelect, _defaultSpeechProvider));
        SettingsAudioStatusText.Text = "Audio engine updated. The next ASR run will use this route.";
    }

    private void SettingsRegionGapBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        RegionGapBox.Text = SettingsRegionGapBox.Text;
    }

    private void SettingsLanguageSelect_OnSelectionChanged(ComboBox target, ComboBox source, string fallback)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        SyncComboSelection(target, SelectedId(source, fallback));
        UpdateTranslateSummary();
    }

    private async void TranslateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await TranslateAsync();
    }

    private async void ProviderSelect_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderReload || _isRefreshing)
        {
            return;
        }

        await LoadModelsForSelectedProviderAsync();
        UpdateTranslateSummary();
    }

    private void TranslateSourceBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateTextStats();
    }

    private void TranslateResultBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateTextStats();
        UpdateCopyHoverState();
    }

    private void ResultTranslatePanel_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateResultHeaderDensity(e.NewSize.Width);
    }

    private void SourceTranslatePanel_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateSourceHeaderDensity(e.NewSize.Width);
    }

    private void SplitGrid_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            NormalizeSplitGrid(grid);
        }
    }

    private void SplitGrid_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid)
        {
            NormalizeSplitGrid(grid);
        }
    }

    private void Splitter_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { Parent: Grid grid })
        {
            NormalizeSplitGrid(grid);
        }
    }

    private void ClearButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TranslateSourceBox.Text = string.Empty;
        TranslateResultBox.Text = string.Empty;
        TranslationStatusText.Text = "Ready.";
        SetTokenUsage(null);
        UpdateTextStats();
        UpdateCopyHoverState();
    }

    private async void CopyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await CopyBoxTextAsync(TranslateResultBox, TranslationStatusText, "result");
    }

    private async void OcrCopyTextButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await CopyBoxTextAsync(OcrTextBox, OcrStatusText, "recognized text");
    }

    private async void OcrCopyTranslationButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await CopyBoxTextAsync(OcrTranslationBox, OcrStatusText, "OCR translation");
    }

    private async void AudioCopyTranscriptButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await CopyBoxTextAsync(AudioTranscriptBox, AudioStatusText, "transcript");
    }

    private async void AudioCopyTranslationButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await CopyBoxTextAsync(AudioTranslationBox, AudioStatusText, "audio translation");
    }

    private async void RegionCopyResultButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await CopyBoxTextAsync(RegionResultBox, RegionStatusPillText, "region translation");
    }

    private void SwapLanguagesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var source = SelectedId(SourceLanguageSelect, _defaultSource);
        var target = SelectedId(TargetLanguageSelect, _defaultTarget);

        if (source.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            SelectById(SourceLanguageSelect, target);
            SelectById(TargetLanguageSelect, PickTargetFallback(target));
        }
        else
        {
            SelectById(SourceLanguageSelect, target);
            SelectById(TargetLanguageSelect, source);
        }

        UpdateTranslateSummary();
    }

    private void OpenWebWorkbenchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenBrowserUrl($"{NormalizeBaseUrl(ApiUrlBox.Text)}/app");
    }

    private async void OcrChooseImageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var file = await PickSingleFileAsync("Choose image", FilePickerFileTypes.ImageAll);
        if (file is null)
        {
            return;
        }

        await LoadOcrImageAsync(file);
    }

    private async void OcrPasteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            OcrStatusText.Text = "Clipboard has no image/base64 text.";
            return;
        }

        var value = text.Trim();
        if (File.Exists(value))
        {
            await LoadOcrImageFromPathAsync(value);
            return;
        }

        var (base64, mime) = ParseDataOrBase64(value, "image/png");
        _ocrImageBase64 = base64;
        _ocrImageMimeType = mime;
        OcrImageMetaText.Text = $"clipboard / {mime}";
        OcrStatusText.Text = "Image payload loaded from clipboard.";
    }

    private async void OcrRunButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunOcrTranslateAsync();
    }

    private void OcrClearButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _ocrImageBase64 = string.Empty;
        _ocrImageMimeType = string.Empty;
        OcrTextBox.Text = string.Empty;
        OcrTranslationBox.Text = string.Empty;
        OcrImageMetaText.Text = "drop / paste / file";
        OcrEngineText.Text = "ocr";
        OcrStatusText.Text = "Ready.";
    }

    private async void DocumentChooseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var file = await PickSingleFileAsync(
            "Choose document",
            new FilePickerFileType("Documents")
            {
                Patterns = ["*.pdf", "*.doc", "*.docx", "*.ppt", "*.pptx", "*.xls", "*.xlsx", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.html", "*.htm", "*.md", "*.markdown", "*.txt"]
            });
        if (file is null)
        {
            return;
        }

        var localPath = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            DocumentJobText.Text = "Selected file cannot be read from a local path.";
            return;
        }

        _documentPath = localPath;
        _documentFileName = file.Name;
        _documentContentType = MimeFromPath(localPath, "application/octet-stream");
        var size = new FileInfo(localPath).Length;
        DocumentFileMetaText.Text = $"{_documentFileName} / {FormatBytes(size)}";
        DocumentJobText.Text = "job  -  ready to start";
        DocumentProgressBar.Value = 0;
        DocumentProgressText.Text = "0%";
        DocumentPreviewText.Text = "Document selected. Start queues a resumable OCR/translation job.";
    }

    private async void DocumentStartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await StartDocumentJobAsync();
    }

    private async void DocumentCancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_documentJobId))
        {
            DocumentJobText.Text = "No active document job.";
            return;
        }

        await PostRegionLikeAsync($"/ocr/document-jobs/{Uri.EscapeDataString(_documentJobId)}/cancel");
        DocumentJobText.Text = $"job {_documentJobId[..Math.Min(10, _documentJobId.Length)]}  -  cancel requested";
    }

    private async void DocumentRefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RefreshDocumentJobsAsync();
    }

    private async void DocumentRefreshSelectedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_documentJobId))
        {
            await RefreshDocumentJobsAsync();
            return;
        }

        await LoadDocumentJobAsync(_documentJobId);
    }

    private async void DocumentJobList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDocumentSelection || DocumentJobList.SelectedItem is not NativeDocumentJobStatus job)
        {
            return;
        }

        await LoadDocumentJobAsync(job.Id);
    }

    private void DocumentOpenEditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_documentJobId))
        {
            DocumentJobText.Text = "No document job selected.";
            return;
        }

        OpenBrowserUrl($"{NormalizeBaseUrl(ApiUrlBox.Text)}/pdf-editor?job={Uri.EscapeDataString(_documentJobId)}");
    }

    private void DocumentOpenArtifactButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_documentJobId))
        {
            DocumentJobText.Text = "No document job selected.";
            return;
        }

        if (DocumentArtifactsSelect.SelectedItem is not NativeDocumentArtifact artifact)
        {
            DocumentJobText.Text = "No artifact selected.";
            return;
        }

        OpenBrowserUrl($"{NormalizeBaseUrl(ApiUrlBox.Text)}/ocr/document-jobs/{Uri.EscapeDataString(_documentJobId)}/artifacts/{Uri.EscapeDataString(artifact.Id)}");
    }

    private async void AudioChooseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var file = await PickSingleFileAsync(
            "Choose audio or video",
            new FilePickerFileType("Audio / video")
            {
                Patterns = ["*.wav", "*.mp3", "*.m4a", "*.aac", "*.ogg", "*.opus", "*.flac", "*.mp4", "*.mkv", "*.webm", "*.mov"]
            });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenReadAsync();
        _audioBase64 = Convert.ToBase64String(await ReadAllBytesAsync(stream));
        _audioMimeType = MimeFromPath(file.Name, "audio/wav");
        AudioUrlBox.Text = string.Empty;
        AudioSourceMetaText.Text = $"{file.Name} / {_audioMimeType}";
        AudioStatusText.Text = "Audio file loaded.";
    }

    private async void AudioTranscribeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunAudioAsync(translate: false);
    }

    private async void AudioTranslateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunAudioAsync(translate: true);
    }

    private void AudioClearButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _audioBase64 = string.Empty;
        _audioMimeType = string.Empty;
        AudioUrlBox.Text = string.Empty;
        AudioTranscriptBox.Text = string.Empty;
        AudioTranslationBox.Text = string.Empty;
        AudioSourceMetaText.Text = "audio source  -  url or file";
        AudioStatusText.Text = "Ready.";
        SetTokenUsage(null);
    }

    private async void RegionSelectButton_OnClick(object? sender, RoutedEventArgs e)
    {
        RegionStatusPillText.Text = "selecting";
        RegionResultBox.Text = "Draw one or more regions on the desktop overlay.";
        await PostRegionActionAsync(BuildRegionNativePath("/region/native/select"));
    }

    private async void RegionSnapshotButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await PostRegionActionAsync("/region/native/snapshot");
    }

    private async void RegionStartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await PostRegionActionAsync(BuildRegionNativePath("/region/native/start"));
    }

    private async void RegionStopButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await PostRegionActionAsync("/region/native/stop");
    }

    private async void RegionClearButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await PostRegionActionAsync("/region/native/clear");
    }

    private void ApplyWorkspace(string workspace)
    {
        var active = workspace.Trim().ToLowerInvariant();
        if (active is not ("translate" or "ocr" or "document" or "audio" or "region" or "settings"))
        {
            active = "translate";
        }

        TranslateView.IsVisible = active == "translate";
        OcrView.IsVisible = active == "ocr";
        DocumentView.IsVisible = active == "document";
        AudioView.IsVisible = active == "audio";
        RegionView.IsVisible = active == "region";
        SettingsView.IsVisible = active == "settings";

        SetWorkspaceButtonState(TopTranslateButton, active, "translate");
        SetWorkspaceButtonState(TopOcrButton, active, "ocr");
        SetWorkspaceButtonState(TopDocumentButton, active, "document");
        SetWorkspaceButtonState(TopAudioButton, active, "audio");
        SetWorkspaceButtonState(TopRegionButton, active, "region");
        SetWorkspaceButtonState(TopSettingsButton, active, "settings");
        SetWorkspaceButtonState(SideTranslateButton, active, "translate");
        SetWorkspaceButtonState(SideOcrButton, active, "ocr");
        SetWorkspaceButtonState(SideDocumentButton, active, "document");
        SetWorkspaceButtonState(SideAudioButton, active, "audio");
        SetWorkspaceButtonState(SideRegionButton, active, "region");
        SetWorkspaceButtonState(SideSettingsButton, active, "settings");

        StatusPathText.Text = active == "translate" ? "/app / translate" : $"/app / {active}";
        LogText.Text = active switch
        {
            "translate" => "Translate workspace ready.",
            "ocr" => "OCR workspace restored as a native layout shell.",
            "document" => "Document workspace restored as a native layout shell.",
            "audio" => "Audio and Audio + Translate are merged into one native workspace.",
            "region" => "Region workspace restored as a native layout shell.",
            "settings" => "Settings workspace restored as a native layout shell.",
            _ => LogText.Text
        };

        if (active == "region")
        {
            _ = RefreshRegionStatusAsync();
        }
        else if (active == "audio")
        {
            _ = RefreshAudioSessionsAsync();
        }
        else if (active == "document")
        {
            _ = RefreshDocumentJobsAsync();
        }
        else if (active == "settings")
        {
            ApplySettingsSection(_settingsSection);
            _ = LoadSettingsAsync(NormalizeBaseUrl(ApiUrlBox.Text));
        }

        UpdateResponsiveState(Bounds.Width > 0 ? Bounds.Width : Width);
    }

    private void ApplySettingsSection(string section)
    {
        var active = section.Trim().ToLowerInvariant();
        if (active is not ("general" or "appearance" or "performance" or "providers" or "sound" or "ocr" or "audio" or "region" or "shortcuts" or "memory"))
        {
            active = "general";
        }

        _settingsSection = active;
        SettingsGeneralPanel.IsVisible = active == "general";
        SettingsAppearancePanel.IsVisible = active == "appearance";
        SettingsPerformancePanel.IsVisible = active == "performance";
        SettingsProvidersPanel.IsVisible = active == "providers";
        SettingsSoundPanel.IsVisible = active == "sound";
        SettingsOcrPanel.IsVisible = active == "ocr";
        SettingsAudioPanel.IsVisible = active == "audio";
        SettingsRegionPanel.IsVisible = active == "region";
        SettingsShortcutsPanel.IsVisible = active == "shortcuts";
        SettingsMemoryPanel.IsVisible = active == "memory";

        SetSettingsButtonState(SettingsGeneralNavButton, active, "general");
        SetSettingsButtonState(SettingsAppearanceNavButton, active, "appearance");
        SetSettingsButtonState(SettingsPerformanceNavButton, active, "performance");
        SetSettingsButtonState(SettingsProvidersNavButton, active, "providers");
        SetSettingsButtonState(SettingsSoundNavButton, active, "sound");
        SetSettingsButtonState(SettingsOcrShortcutButton, active, "ocr");
        SetSettingsButtonState(SettingsAudioShortcutButton, active, "audio");
        SetSettingsButtonState(SettingsRegionShortcutButton, active, "region");
        SetSettingsButtonState(SettingsShortcutsNavButton, active, "shortcuts");
        SetSettingsButtonState(SettingsMemoryNavButton, active, "memory");
    }

    private static void SetSettingsButtonState(Button button, string active, string buttonSection)
    {
        var isActive = string.Equals(active, buttonSection, StringComparison.OrdinalIgnoreCase);
        var foreground = isActive ? SideNavActiveBrush : TopTabInactiveBrush;
        button.Classes.Set("active", isActive);
        button.Foreground = foreground;
        if (button.Content is StackPanel stack)
        {
            foreach (var icon in stack.Children.OfType<FluentIcon>())
            {
                icon.Foreground = foreground;
            }

            SetNestedForeground(stack, foreground);
        }
    }

    private void UpdateResponsiveState(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            width = Bounds.Width > 0 ? Bounds.Width : Width;
        }

        var height = Bounds.Height > 0 ? Bounds.Height : Height;

        var iconOnlyTabs = width < 1160;
        var compactChrome = width < 1020;
        var compactSide = width < 880;
        var narrowActions = width < 820;
        var settingsActive = SettingsView.IsVisible;

        TopBarGrid.ColumnDefinitions[1].Width = width < 780 ? new GridLength(72) : new GridLength(190);
        SetTextDescendantsVisible(BrandBlock, width >= 780);
        RuntimeText.MaxWidth = compactChrome ? 92 : 150;

        foreach (var button in TopTabButtons())
        {
            SetButtonTextVisible(button, !iconOnlyTabs);
            button.Padding = iconOnlyTabs ? new global::Avalonia.Thickness(12, 0) : new global::Avalonia.Thickness(18, 0);
            button.MinWidth = iconOnlyTabs ? 42 : 0;
        }

        ApiUrlBox.IsVisible = width >= 980;
        SetButtonTextVisible(RefreshButton, width >= 1080);
        TopApiPanel.Margin = compactChrome ? new global::Avalonia.Thickness(0, 0, 6, 0) : new global::Avalonia.Thickness(0, 0, 12, 0);

        MainBodyGrid.ColumnDefinitions[1].Width = settingsActive
            ? new GridLength(0)
            : compactSide ? new GridLength(68) : new GridLength(190);
        SideWorkspacePanel.IsVisible = !settingsActive;
        SideWorkspaceTitle.IsVisible = !compactSide && !settingsActive;
        SideMetricsPanel.IsVisible = !compactSide && !settingsActive;
        foreach (var button in SideNavButtons())
        {
            SetButtonTextVisible(button, !compactSide);
            button.Padding = compactSide ? new global::Avalonia.Thickness(0, 7) : new global::Avalonia.Thickness(10, 7);
            button.HorizontalContentAlignment = compactSide ? global::Avalonia.Layout.HorizontalAlignment.Center : global::Avalonia.Layout.HorizontalAlignment.Left;
            button.MinWidth = compactSide ? 44 : 0;
        }

        ApplyTranslateOptionDensity(width);
        ApplyActionDensity(width);
        ApplyTranslateSplitLayout(width, height);
        ApplyWorkspaceSplitLayouts(width);
        ApplyModuleHeaderDensity(width);
        ApplySettingsDensity(width);

        UpdateSourceHeaderDensity(SourceTranslatePanel.Bounds.Width);
        UpdateResultHeaderDensity(ResultTranslatePanel.Bounds.Width);
        TranslationStatusText.IsVisible = !narrowActions;
        TranslateModePill.IsVisible = width >= 940;
    }

    private void ApplySettingsDensity(double width)
    {
        if (!SettingsView.IsVisible)
        {
            return;
        }

        var compactSettings = width < 760;
        if (SettingsLayoutGrid.ColumnDefinitions.Count > 0)
        {
            SettingsLayoutGrid.ColumnDefinitions[0].Width = compactSettings ? new GridLength(64) : new GridLength(220);
        }

        foreach (var button in SettingsNavButtons())
        {
            SetButtonTextVisible(button, !compactSettings);
            button.Padding = compactSettings ? new global::Avalonia.Thickness(0, 8) : new global::Avalonia.Thickness(10, 8);
            button.HorizontalContentAlignment = compactSettings
                ? global::Avalonia.Layout.HorizontalAlignment.Center
                : global::Avalonia.Layout.HorizontalAlignment.Left;
            button.MinWidth = compactSettings ? 44 : 0;
        }
    }

    private void ApplyTranslateOptionDensity(double width)
    {
        var defs = TranslateOptionsGrid.ColumnDefinitions;
        if (defs.Count < 6)
        {
            return;
        }

        var showPreset = width >= 1240;
        var showModel = width >= 1040;
        var showProvider = width >= 820;

        ProviderField.IsVisible = showProvider;
        ModelField.IsVisible = showModel;
        PresetField.IsVisible = showPreset;

        var compactLanguageWidth = width < 820;

        defs[0].Width = compactLanguageWidth ? new GridLength(170) : new GridLength(1.1, GridUnitType.Star);
        defs[1].Width = GridLength.Auto;
        defs[2].Width = compactLanguageWidth ? new GridLength(210) : new GridLength(1.1, GridUnitType.Star);
        defs[3].Width = showProvider ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0);
        defs[4].Width = showModel ? new GridLength(1.45, GridUnitType.Star) : new GridLength(0);
        defs[5].Width = showPreset ? new GridLength(180) : new GridLength(0);

        SetButtonTextVisible(SwapLanguagesButton, width >= 900);

        SourceLanguageSelect.MaxWidth = compactLanguageWidth ? 170 : double.PositiveInfinity;
        TargetLanguageSelect.MaxWidth = compactLanguageWidth ? 210 : double.PositiveInfinity;
        ProviderSelect.MaxWidth = width < 1040 ? 170 : double.PositiveInfinity;
        ModelSelect.MaxWidth = width < 1160 ? 260 : double.PositiveInfinity;
    }

    private void ApplyActionDensity(double width)
    {
        var showActionText = width >= 760;
        var showSecondaryText = width >= 940;

        SetButtonTextVisible(TranslateButton, showActionText);
        SetButtonTextVisible(ClearButton, showActionText);
        SetButtonTextVisible(OcrRunButton, showActionText);
        SetButtonTextVisible(OcrClearButton, showActionText);
        SetButtonTextVisible(DocumentStartButton, showActionText);
        SetButtonTextVisible(DocumentCancelButton, showSecondaryText);
        SetButtonTextVisible(DocumentRefreshButton, showSecondaryText);
        SetButtonTextVisible(AudioTranscribeButton, showActionText);
        SetButtonTextVisible(AudioTranslateButton, showSecondaryText);
        SetButtonTextVisible(AudioClearButton, showActionText);
        SetButtonTextVisible(RegionSelectButton, showActionText);
        SetButtonTextVisible(RegionStartButton, showActionText);
        SetButtonTextVisible(RegionSnapshotButton, showSecondaryText);
        SetButtonTextVisible(RegionStopButton, showActionText);
        SetButtonTextVisible(RegionClearButton, showActionText);

        SetButtonTextVisible(OcrPasteButton, showSecondaryText);
        SetButtonTextVisible(OcrChooseImageButton, showSecondaryText);
        SetButtonTextVisible(OcrRunButtonTop, showActionText);
        SetButtonTextVisible(DocumentJobsButton, showSecondaryText);
        SetButtonTextVisible(DocumentRefreshButtonTop, showSecondaryText);
        SetButtonTextVisible(DocumentOpenEditorButton, showSecondaryText);
        SetButtonTextVisible(DocumentOpenArtifactButton, showSecondaryText);
        SetButtonTextVisible(DocumentRefreshSelectedButton, showSecondaryText);
        SetButtonTextVisible(AudioChooseButton, showSecondaryText);
    }

    private void ApplyModuleHeaderDensity(double width)
    {
        var showHeaderActions = width >= 980;
        OcrHeaderActions.IsVisible = showHeaderActions;
        DocumentHeaderActions.IsVisible = showHeaderActions;
        AudioModePanel.IsVisible = showHeaderActions;
        RegionModePanel.IsVisible = showHeaderActions;
    }

    private void ApplyTranslateSplitLayout(double width, double height)
    {
        var stacked = width < 900;
        TranslateSplitGrid.MaxHeight = stacked
            ? Math.Clamp(height - 430, 340, 520)
            : Math.Max(260, height - 360);
        TranslateSplitGrid.MinHeight = stacked ? 360 : 300;

        if (stacked == _translateSplitStacked)
        {
            NormalizeSplitGrid(TranslateSplitGrid);
            return;
        }

        _translateSplitStacked = stacked;

        if (stacked)
        {
            TranslateSplitGrid.ColumnDefinitions.Clear();
            TranslateSplitGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TranslateSplitGrid.RowDefinitions.Clear();
            TranslateSplitGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            TranslateSplitGrid.RowDefinitions.Add(new RowDefinition(new GridLength(10)));
            TranslateSplitGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

            Grid.SetColumn(SourceTranslatePanel, 0);
            Grid.SetRow(SourceTranslatePanel, 0);
            Grid.SetColumn(TranslateGridSplitter, 0);
            Grid.SetRow(TranslateGridSplitter, 1);
            Grid.SetColumn(ResultTranslatePanel, 0);
            Grid.SetRow(ResultTranslatePanel, 2);

            TranslateGridSplitter.ResizeDirection = GridResizeDirection.Rows;
            TranslateGridSplitter.Width = double.NaN;
            TranslateGridSplitter.Height = 10;
        }
        else
        {
            TranslateSplitGrid.RowDefinitions.Clear();
            TranslateSplitGrid.ColumnDefinitions.Clear();
            TranslateSplitGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TranslateSplitGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
            TranslateSplitGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            Grid.SetColumn(SourceTranslatePanel, 0);
            Grid.SetRow(SourceTranslatePanel, 0);
            Grid.SetColumn(TranslateGridSplitter, 1);
            Grid.SetRow(TranslateGridSplitter, 0);
            Grid.SetColumn(ResultTranslatePanel, 2);
            Grid.SetRow(ResultTranslatePanel, 0);

            TranslateGridSplitter.ResizeDirection = GridResizeDirection.Columns;
            TranslateGridSplitter.Width = 10;
            TranslateGridSplitter.Height = double.NaN;
        }

        TranslateGridSplitter.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
        TranslateGridSplitter.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
        NormalizeSplitGrid(TranslateSplitGrid);
    }

    private void ApplyWorkspaceSplitLayouts(double width)
    {
        var stacked = width < 900;
        OcrSplitGrid.MinHeight = stacked ? 560 : 420;
        AudioSplitGrid.MinHeight = stacked ? 560 : 430;
        RegionSplitGrid.MinHeight = stacked ? 600 : 380;

        ApplyTwoPaneSplitLayout(OcrSplitGrid, OcrInputPanel, OcrOuterSplitter, OcrOutputStackGrid, stacked, ref _ocrSplitStacked);
        ApplyTwoPaneSplitLayout(AudioSplitGrid, AudioSourcePanel, AudioOuterSplitter, AudioOutputStackGrid, stacked, ref _audioSplitStacked);
        ApplyTwoPaneSplitLayout(RegionSplitGrid, RegionCapturePanel, RegionOuterSplitter, RegionResultPanel, stacked, ref _regionSplitStacked);
    }

    private static void ApplyTwoPaneSplitLayout(
        Grid grid,
        Control firstPane,
        GridSplitter splitter,
        Control secondPane,
        bool stacked,
        ref bool currentStacked)
    {
        if (stacked == currentStacked)
        {
            NormalizeSplitGrid(grid);
            return;
        }

        currentStacked = stacked;

        if (stacked)
        {
            grid.ColumnDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.RowDefinitions.Clear();
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(10)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

            Grid.SetColumn(firstPane, 0);
            Grid.SetRow(firstPane, 0);
            Grid.SetColumn(splitter, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetColumn(secondPane, 0);
            Grid.SetRow(secondPane, 2);

            splitter.ResizeDirection = GridResizeDirection.Rows;
            splitter.Width = double.NaN;
            splitter.Height = 10;
        }
        else
        {
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            Grid.SetColumn(firstPane, 0);
            Grid.SetRow(firstPane, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetRow(splitter, 0);
            Grid.SetColumn(secondPane, 2);
            Grid.SetRow(secondPane, 0);

            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.Width = 10;
            splitter.Height = double.NaN;
        }

        splitter.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
        splitter.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
        NormalizeSplitGrid(grid);
    }

    private Button[] TopTabButtons() => new[]
    {
        TopTranslateButton,
        TopOcrButton,
        TopDocumentButton,
        TopAudioButton,
        TopRegionButton,
        TopSettingsButton
    };

    private Button[] SideNavButtons() => new[]
    {
        SideTranslateButton,
        SideOcrButton,
        SideDocumentButton,
        SideAudioButton,
        SideRegionButton,
        SideSettingsButton
    };

    private Button[] SettingsNavButtons() => new[]
    {
        SettingsGeneralNavButton,
        SettingsAppearanceNavButton,
        SettingsPerformanceNavButton,
        SettingsProvidersNavButton,
        SettingsSoundNavButton,
        SettingsShortcutsNavButton,
        SettingsOcrShortcutButton,
        SettingsAudioShortcutButton,
        SettingsRegionShortcutButton,
        SettingsMemoryNavButton
    };

    private static void SetWorkspaceButtonState(Button button, string active, string workspace)
    {
        var isActive = active.Equals(workspace, StringComparison.OrdinalIgnoreCase);
        button.Classes.Set("active", isActive);

        var brush = button.Classes.Contains("nav")
            ? isActive ? SideNavActiveBrush : SideNavInactiveBrush
            : isActive ? TopTabActiveBrush : TopTabInactiveBrush;

        button.Foreground = brush;
        if (button.Content is Control content)
        {
            SetNestedForeground(content, brush);
        }
    }

    private static void SetNestedForeground(Control control, IBrush brush)
    {
        switch (control)
        {
            case FluentIcon icon:
                icon.Foreground = brush;
                break;
            case TextBlock text:
                text.Foreground = brush;
                break;
            case Panel panel:
                foreach (var child in panel.Children.OfType<Control>())
                {
                    SetNestedForeground(child, brush);
                }
                break;
            case ContentControl { Content: Control child }:
                SetNestedForeground(child, brush);
                break;
            case Decorator { Child: Control child }:
                SetNestedForeground(child, brush);
                break;
        }
    }

    private static void SetButtonTextVisible(Button button, bool visible)
    {
        if (button.Content is Control control)
        {
            SetTextDescendantsVisible(control, visible);
        }
    }

    private static void SetTextDescendantsVisible(Control control, bool visible)
    {
        switch (control)
        {
            case TextBlock text:
                text.IsVisible = visible;
                break;
            case Panel panel:
                foreach (var child in panel.Children.OfType<Control>())
                {
                    SetTextDescendantsVisible(child, visible);
                }
                break;
            case ContentControl { Content: Control child }:
                SetTextDescendantsVisible(child, visible);
                break;
            case Decorator { Child: Control child }:
                SetTextDescendantsVisible(child, visible);
                break;
        }
    }

    private static void SetButtonLabel(Button button, string label)
    {
        if (button.Content is Control control && FindFirstTextBlock(control) is { } text)
        {
            text.Text = label;
            return;
        }

        button.Content = label;
    }

    private static TextBlock? FindFirstTextBlock(Control control)
    {
        switch (control)
        {
            case TextBlock text:
                return text;
            case Panel panel:
                foreach (var child in panel.Children.OfType<Control>())
                {
                    var found = FindFirstTextBlock(child);
                    if (found is not null)
                    {
                        return found;
                    }
                }
                break;
            case ContentControl { Content: Control child }:
                return FindFirstTextBlock(child);
            case Decorator { Child: Control child }:
                return FindFirstTextBlock(child);
        }

        return null;
    }

    private async Task<IStorageFile?> PickSingleFileAsync(string title, FilePickerFileType fileType)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [fileType]
        });
        return files.Count > 0 ? files[0] : null;
    }

    private async Task LoadOcrImageAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        _ocrImageBase64 = Convert.ToBase64String(await ReadAllBytesAsync(stream));
        _ocrImageMimeType = MimeFromPath(file.Name, "image/png");
        OcrImageMetaText.Text = $"{file.Name} / {_ocrImageMimeType}";
        OcrStatusText.Text = "Image loaded.";
    }

    private async Task LoadOcrImageFromPathAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        _ocrImageBase64 = Convert.ToBase64String(bytes);
        _ocrImageMimeType = MimeFromPath(path, "image/png");
        OcrImageMetaText.Text = $"{Path.GetFileName(path)} / {_ocrImageMimeType}";
        OcrStatusText.Text = "Image file loaded.";
    }

    private async Task RunOcrTranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(_ocrImageBase64))
        {
            OcrStatusText.Text = "Choose or paste an image first.";
            return;
        }

        OcrRunButton.IsEnabled = false;
        OcrRunButtonTop.IsEnabled = false;
        OcrStatusText.Text = "Running /ocr/translate ...";
        var started = Stopwatch.StartNew();
        try
        {
            var request = new NativeOcrTranslateRequest
            {
                ImageBase64 = _ocrImageBase64,
                ImageMimeType = _ocrImageMimeType,
                OcrProvider = SelectedId(SettingsOcrProviderSelect, SelectedId(OcrProviderSelect, _defaultOcrProvider)),
                ContentType = ComboValue(SettingsOcrContentTypeSelect, "auto"),
                Preference = ComboValue(SettingsOcrPreferenceSelect, "auto"),
                Language = SelectedId(SettingsOcrLanguageSelect, SelectedId(SourceLanguageSelect, _defaultSource)),
                Profile = CleanOrDefault(SettingsOcrProfileBox.Text, "default"),
                PreprocessingPreset = ComboValue(SettingsOcrPreprocessSelect, "none"),
                TranslationProvider = SelectedId(ProviderSelect, _defaultProvider),
                Model = SelectedId(ModelSelect, _defaultModel),
                Source = SelectedId(SourceLanguageSelect, _defaultSource),
                Target = SelectedId(TargetLanguageSelect, _defaultTarget),
                Mode = CleanOrDefault(PresetBox.Text, _defaultMode),
                Surface = "ocr"
            };
            using var response = await Http.PostAsJsonAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/ocr/translate", request, JsonOptions);
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<NativeOcrTranslateResponse>(JsonOptions)
                ?? throw new InvalidOperationException("Empty OCR response.");
            started.Stop();

            OcrTextBox.Text = result.Ocr?.Text ?? string.Empty;
            OcrTranslationBox.Text = result.Translation?.Result ?? string.Empty;
            OcrEngineText.Text = $"{result.Ocr?.Provider ?? "ocr"} / {result.Ocr?.Engine ?? "-"}";
            OcrStatusText.Text = $"Done in {started.ElapsedMilliseconds} ms";
            SetTokenUsage(result.Translation?.TokenUsage ?? result.Structured?.TokenUsage);
            await RefreshMemoryAsync(NormalizeBaseUrl(ApiUrlBox.Text));
        }
        catch (Exception ex)
        {
            started.Stop();
            OcrStatusText.Text = $"OCR failed after {started.ElapsedMilliseconds} ms";
            LogText.Text = ex.Message;
        }
        finally
        {
            OcrRunButton.IsEnabled = true;
            OcrRunButtonTop.IsEnabled = true;
        }
    }

    private async Task StartDocumentJobAsync()
    {
        if (string.IsNullOrWhiteSpace(_documentPath) || !File.Exists(_documentPath))
        {
            DocumentJobText.Text = "Choose a document first.";
            return;
        }

        DocumentStartButton.IsEnabled = false;
        DocumentJobText.Text = "job  -  uploading";
        try
        {
            await using var stream = File.OpenRead(_documentPath);
            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(_documentContentType);
            form.Add(fileContent, "file", _documentFileName);
            form.Add(new StringContent("default"), "profile");
            form.Add(new StringContent(SelectedId(SourceLanguageSelect, _defaultSource)), "source");
            form.Add(new StringContent(SelectedId(TargetLanguageSelect, _defaultTarget)), "target");
            form.Add(new StringContent(CleanOrDefault(PresetBox.Text, _defaultMode)), "mode");
            form.Add(new StringContent(SelectedId(ProviderSelect, _defaultProvider)), "translationProvider");
            form.Add(new StringContent(SelectedId(ModelSelect, _defaultModel)), "model");
            form.Add(new StringContent(SelectedId(SettingsOcrProviderSelect, SelectedId(OcrProviderSelect, _defaultOcrProvider))), "ocrProvider");
            form.Add(new StringContent(SelectedId(SettingsOcrLanguageSelect, SelectedId(SourceLanguageSelect, _defaultSource))), "ocrLanguage");

            using var response = await Http.PostAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/ocr/document-jobs", form);
            await EnsureSuccessAsync(response);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            _documentJobId = GetString(json.RootElement, "id", string.Empty);
            var status = GetString(json.RootElement, "status", "queued");
            DocumentJobText.Text = string.IsNullOrWhiteSpace(_documentJobId)
                ? $"job  -  {status}"
                : $"job {_documentJobId[..Math.Min(10, _documentJobId.Length)]}  -  {status}";
            DocumentPreviewText.Text = "Document job queued. Use Refresh to update the job list.";
            await RefreshDocumentJobsAsync();
            if (!string.IsNullOrWhiteSpace(_documentJobId))
            {
                await LoadDocumentJobAsync(_documentJobId);
                _ = PollDocumentJobUntilTerminalAsync(_documentJobId);
            }
        }
        catch (Exception ex)
        {
            DocumentJobText.Text = "document job failed";
            LogText.Text = ex.Message;
        }
        finally
        {
            DocumentStartButton.IsEnabled = true;
        }
    }

    private async Task RefreshDocumentJobsAsync()
    {
        try
        {
            var jobs = await GetFromJsonOrDefaultAsync<List<NativeDocumentJobStatus>>(
                $"{NormalizeBaseUrl(ApiUrlBox.Text)}/ocr/document-jobs?profile=default&limit=10") ?? [];
            DocumentJobsSummaryText.Text = $"{jobs.Count} jobs";

            _suppressDocumentSelection = true;
            DocumentJobList.ItemsSource = jobs;
            var selected = jobs.FirstOrDefault(job => job.Id.Equals(_documentJobId, StringComparison.OrdinalIgnoreCase))
                ?? jobs.FirstOrDefault();
            DocumentJobList.SelectedItem = selected;
            _suppressDocumentSelection = false;

            if (selected is not null)
            {
                await LoadDocumentJobAsync(selected.Id);
            }
            else
            {
                _documentJobId = string.Empty;
                DocumentJobText.Text = "job  -  none";
                DocumentPreviewText.Text = "No document jobs yet.";
                DocumentArtifactsSelect.ItemsSource = Array.Empty<NativeDocumentArtifact>();
            }
        }
        catch (Exception ex)
        {
            _suppressDocumentSelection = false;
            DocumentJobsSummaryText.Text = "unavailable";
            LogText.Text = ex.Message;
        }
    }

    private async Task LoadDocumentJobAsync(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        try
        {
            var job = await GetFromJsonOrDefaultAsync<NativeDocumentJobStatus>(
                $"{NormalizeBaseUrl(ApiUrlBox.Text)}/ocr/document-jobs/{Uri.EscapeDataString(jobId)}");
            if (job is null)
            {
                DocumentJobText.Text = "Document job was not found.";
                return;
            }

            RenderDocumentJob(job);
            await RefreshDocumentPreviewAsync(job.Id);
        }
        catch (Exception ex)
        {
            DocumentJobText.Text = "document job unavailable";
            LogText.Text = ex.Message;
        }
    }

    private void RenderDocumentJob(NativeDocumentJobStatus job)
    {
        _documentJobId = job.Id;
        var shortId = job.Id.Length > 10 ? job.Id[..10] : job.Id;
        var progress = Math.Clamp(job.Progress * 100d, 0, 100);
        DocumentFileMetaText.Text = $"{Pick(job.InputFileName, "document")} / {Pick(job.SourceKind, "file")}";
        DocumentProgressBar.Value = progress;
        DocumentProgressText.Text = $"{progress:0}%";

        var total = job.TotalUnits is > 0 ? job.TotalUnits.Value.ToString(CultureInfo.InvariantCulture) : "?";
        var line = $"{job.Status} · {Pick(job.Stage, "-")} · {job.CompletedUnits}/{total} units · {job.ArtifactCount} artifacts · {shortId}";
        if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            line += $"{Environment.NewLine}{job.ErrorMessage}";
        }
        DocumentJobText.Text = line;

        var artifacts = job.Artifacts ?? [];
        DocumentArtifactsSelect.ItemsSource = artifacts;
        DocumentArtifactsSelect.SelectedItem =
            artifacts.FirstOrDefault(item => item.Kind.Equals("translated", StringComparison.OrdinalIgnoreCase))
            ?? artifacts.FirstOrDefault();

        var terminal = IsTerminalDocumentStatus(job.Status);
        DocumentCancelButton.IsEnabled = !terminal;
        DocumentOpenEditorButton.IsEnabled = !string.IsNullOrWhiteSpace(job.Id);
        DocumentOpenArtifactButton.IsEnabled = artifacts.Count > 0;
    }

    private async Task RefreshDocumentPreviewAsync(string jobId)
    {
        try
        {
            using var preview = await GetJsonAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/ocr/document-jobs/{Uri.EscapeDataString(jobId)}/preview");
            var root = preview.RootElement;
            var kind = GetString(root, "kind", "pending");
            DocumentPreviewText.Text = kind switch
            {
                "text" or "html" => PreviewContent(root, kind),
                "layout" => $"Layout preview ready. {GetNestedDouble(root, ["pageCount"]):0} page(s). Use Open editor for the full PDF/layout editor.",
                "file" => "Translated file artifact is ready. Choose an artifact below and open it.",
                "error" => Pick(GetString(root, "errorMessage", string.Empty), $"Document job ended with status {GetString(root, "status", "error")}."),
                _ => "Preview is not ready yet. Refresh the job to update progress."
            };
        }
        catch (Exception ex)
        {
            DocumentPreviewText.Text = "Preview unavailable.";
            LogText.Text = ex.Message;
        }
    }

    private async Task PollDocumentJobUntilTerminalAsync(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || _documentPollingJobId.Equals(jobId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _documentPollingJobId = jobId;
        try
        {
            for (var attempt = 0; attempt < 180; attempt++)
            {
                if (!_documentJobId.Equals(jobId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var job = await GetFromJsonOrDefaultAsync<NativeDocumentJobStatus>(
                    $"{NormalizeBaseUrl(ApiUrlBox.Text)}/ocr/document-jobs/{Uri.EscapeDataString(jobId)}");
                if (job is null)
                {
                    return;
                }

                RenderDocumentJob(job);
                if (IsTerminalDocumentStatus(job.Status))
                {
                    await RefreshDocumentJobsAsync();
                    await RefreshDocumentPreviewAsync(job.Id);
                    return;
                }

                DocumentPreviewText.Text = $"Job is running: {job.Stage} · {job.CompletedUnits}/{(job.TotalUnits?.ToString(CultureInfo.InvariantCulture) ?? "?")} units.";
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            LogText.Text = ex.Message;
        }
        finally
        {
            if (_documentPollingJobId.Equals(jobId, StringComparison.OrdinalIgnoreCase))
            {
                _documentPollingJobId = string.Empty;
            }
        }
    }

    private static string PreviewContent(JsonElement root, string kind)
    {
        var content = GetString(root, "content", string.Empty);
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"{kind} preview is empty.";
        }

        const int maxPreviewChars = 5000;
        var truncated = root.TryGetProperty("truncated", out var truncatedValue) && truncatedValue.ValueKind == JsonValueKind.True;
        if (content.Length > maxPreviewChars)
        {
            content = content[..maxPreviewChars];
            truncated = true;
        }

        return truncated
            ? $"{content}{Environment.NewLine}{Environment.NewLine}... preview truncated. Open artifact for the full output."
            : content;
    }

    private static bool IsTerminalDocumentStatus(string status)
    {
        return status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenBrowserUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private async Task RunAudioAsync(bool translate)
    {
        var sourceUrl = AudioUrlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceUrl) && string.IsNullOrWhiteSpace(_audioBase64))
        {
            AudioStatusText.Text = "Enter an audio URL or choose an audio file.";
            return;
        }

        AudioTranscribeButton.IsEnabled = false;
        AudioTranslateButton.IsEnabled = false;
        AudioStatusText.Text = translate ? "Running /asr/translate ..." : "Running /asr ...";
        var started = Stopwatch.StartNew();
        try
        {
            var speechProvider = SelectedId(SettingsAudioProviderSelect, SelectedId(AudioProviderSelect, _defaultSpeechProvider));
            var speechLanguage = SelectedId(SettingsAudioLanguageSelect, SelectedId(SourceLanguageSelect, _defaultSource));
            var speechProfile = CleanOrDefault(SettingsAudioProfileBox.Text, "default");

            if (translate)
            {
                var request = new NativeSpeechTranslateRequest
                {
                    AudioBase64 = string.IsNullOrWhiteSpace(sourceUrl) ? _audioBase64 : null,
                    AudioMimeType = string.IsNullOrWhiteSpace(sourceUrl) ? _audioMimeType : null,
                    SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl,
                    SpeechProvider = speechProvider,
                    Language = speechLanguage,
                    Profile = speechProfile,
                    Source = SelectedId(SourceLanguageSelect, _defaultSource),
                    Target = SelectedId(TargetLanguageSelect, _defaultTarget),
                    Mode = CleanOrDefault(PresetBox.Text, "subtitle"),
                    TranslationProvider = SelectedId(ProviderSelect, _defaultProvider),
                    Model = SelectedId(ModelSelect, _defaultModel)
                };
                using var response = await Http.PostAsJsonAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/asr/translate", request, JsonOptions);
                await EnsureSuccessAsync(response);
                var result = await response.Content.ReadFromJsonAsync<NativeSpeechTranslateResponse>(JsonOptions)
                    ?? throw new InvalidOperationException("Empty ASR translate response.");
                AudioTranscriptBox.Text = result.Speech?.Text ?? string.Empty;
                AudioTranslationBox.Text = string.Join(Environment.NewLine, result.Translations.Select(t => t.TranslatedText).Where(t => !string.IsNullOrWhiteSpace(t)));
                SetTokenUsage(result.TokenUsage);
            }
            else
            {
                var request = new NativeSpeechRequest
                {
                    AudioBase64 = string.IsNullOrWhiteSpace(sourceUrl) ? _audioBase64 : null,
                    AudioMimeType = string.IsNullOrWhiteSpace(sourceUrl) ? _audioMimeType : null,
                    SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl,
                    Provider = speechProvider,
                    Language = speechLanguage,
                    Profile = speechProfile
                };
                using var response = await Http.PostAsJsonAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/asr", request, JsonOptions);
                await EnsureSuccessAsync(response);
                var result = await response.Content.ReadFromJsonAsync<NativeSpeechResponse>(JsonOptions)
                    ?? throw new InvalidOperationException("Empty ASR response.");
                AudioTranscriptBox.Text = result.Text;
                AudioTranslationBox.Text = string.Empty;
                SetTokenUsage(new NativeTokenUsage(0, 0, 0, "asr:no-llm", true));
            }

            started.Stop();
            AudioStatusText.Text = $"Done in {started.ElapsedMilliseconds} ms";
            await RefreshMemoryAsync(NormalizeBaseUrl(ApiUrlBox.Text));
        }
        catch (Exception ex)
        {
            started.Stop();
            AudioStatusText.Text = $"Audio failed after {started.ElapsedMilliseconds} ms";
            LogText.Text = ex.Message;
        }
        finally
        {
            AudioTranscribeButton.IsEnabled = true;
            AudioTranslateButton.IsEnabled = true;
        }
    }

    private async Task RefreshAudioSessionsAsync()
    {
        var snapshot = await GetFromJsonOrDefaultAsync<JsonElement>($"{NormalizeBaseUrl(ApiUrlBox.Text)}/audio/sessions");
        if (snapshot.ValueKind == JsonValueKind.Undefined)
        {
            AudioSessionsText.Text = "sessions  -  unavailable";
            return;
        }

        var count = snapshot.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array
            ? sessions.GetArrayLength()
            : 0;
        AudioSessionsText.Text = $"sessions  -  {count} detected";
    }

    private async Task PostRegionActionAsync(string path)
    {
        try
        {
            RegionStatusPillText.Text = path.Split('/').LastOrDefault() ?? "working";
            using var response = await Http.PostAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}{path}", null);
            await EnsureSuccessAsync(response);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            RenderRegionStatus(json.RootElement);
        }
        catch (Exception ex)
        {
            RegionStatusPillText.Text = "error";
            RegionResultBox.Text = ex.Message;
        }
    }

    private async Task PostRegionLikeAsync(string path)
    {
        using var response = await Http.PostAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}{path}", null);
        await EnsureSuccessAsync(response);
    }

    private async Task RefreshRegionStatusAsync()
    {
        try
        {
            using var json = await GetJsonAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/region/native/status");
            RenderRegionStatus(json.RootElement);
        }
        catch (Exception ex)
        {
            RegionStatusPillText.Text = "offline";
            RegionResultBox.Text = ex.Message;
        }
    }

    private void RenderRegionStatus(JsonElement status)
    {
        var regionCount = GetNestedDouble(status, ["regionCount"]);
        if (double.IsNaN(regionCount))
        {
            regionCount = 0;
        }

        var running = GetNestedBool(status, ["loopActive"]) || GetNestedBool(status, ["running"]);
        var overlaysHidden = GetNestedBool(status, ["overlaysHidden"]);
        var profile = GetString(status, "profileName", GetString(status, "activeProfileName", "native"));
        var minOcrGapMs = GetNestedDouble(status, ["minOcrGapMs"]);
        RegionLoopText.Text = running ? "Loop on" : "Loop off";
        RegionStatusPillText.Text = $"{regionCount:0} region(s) / {(running ? "running" : "stopped")}";
        SettingsRegionStatusText.Text = $"capture: {regionCount:0} region(s)";
        SettingsRegionLoopStatusText.Text = $"loop: {(running ? "running" : "stopped")}";
        SettingsRegionSelectionStatusText.Text = $"selection: {profile}";
        if (!double.IsNaN(minOcrGapMs))
        {
            var gapText = $"{Math.Round(minOcrGapMs):0}";
            _suppressSettingsSync = true;
            SettingsRegionGapBox.Text = gapText;
            RegionGapBox.Text = gapText;
            _suppressSettingsSync = false;
            SettingsRegionLoopStatusText.Text += $" / min gap {gapText} ms";
        }
        RegionStageText.Text = overlaysHidden
            ? "Native regions are selected but overlays are hidden."
            : "Native region overlay is controlled by the backend service.";
        RegionResultBox.Text = $"{profile}{Environment.NewLine}{RegionStatusPillText.Text}";
    }

    private string BuildRegionNativePath(string path)
    {
        var gap = CleanOrDefault(SettingsRegionGapBox.Text, CleanOrDefault(RegionGapBox.Text, string.Empty));
        return int.TryParse(gap, out var minOcrGapMs)
            ? $"{path}?minOcrGapMs={Math.Clamp(minOcrGapMs, 500, 30000)}"
            : path;
    }

    private async Task RefreshAsync()
    {
        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        _isRefreshing = true;
        SetBusy(true);
        LogText.Text = $"Refreshing {baseUrl} ...";

        try
        {
            using var health = await GetJsonAsync($"{baseUrl}/health");
            RenderHealth(health.RootElement);

            var providers = await GetFromJsonOrDefaultAsync<List<ProviderOptionDto>>($"{baseUrl}/providers")
                ?? [];
            RenderProviderOptions(providers);

            var languages = await GetFromJsonOrDefaultAsync<List<LanguageOptionDto>>($"{baseUrl}/translation/languages")
                ?? [];
            RenderLanguageOptions(languages);

            await LoadOcrEnginesAsync(baseUrl);
            await LoadSpeechEnginesAsync(baseUrl);
            await LoadModelsForSelectedProviderAsync(_defaultModel);
            await RefreshMemoryAsync(baseUrl);
            await LoadSettingsAsync(baseUrl);

            LastUpdatedText.Text = $"Updated {DateTimeOffset.Now:HH:mm:ss}";
            LogText.Text = $"ready  Providers synced {DateTimeOffset.Now:HH:mm}  API ok · {RuntimeText.Text} loaded · local workspace";
        }
        catch (Exception ex)
        {
            ApiStatusText.Text = "offline";
            ApiDetailText.Text = baseUrl;
            RuntimeText.Text = "-";
            RuntimeDetailText.Text = "API unavailable";
            UpdateNativeMemoryText();
            BackendRamText.Text = "-";
            WebViewRamText.Text = "-";
            ModelRamText.Text = "-";
            GpuText.Text = "-";
            GpuDetailText.Text = "nvidia-smi unavailable or API offline";
            LogText.Text = ex.Message;
        }
        finally
        {
            _isRefreshing = false;
            SetBusy(false);
            UpdateTranslateSummary();
            UpdateTextStats();
        }
    }

    private async Task RefreshMemoryAsync(string baseUrl)
    {
        try
        {
            using var memory = await GetJsonAsync($"{baseUrl}/system/memory-summary");
            RenderMemory(memory.RootElement);
        }
        catch (Exception ex)
        {
            UpdateNativeMemoryText();
            BackendRamText.Text = "-";
            WebViewRamText.Text = "-";
            ModelRamText.Text = "-";
            GpuText.Text = "--";
            GpuDetailText.Text = ex.Message;
        }
    }

    private async Task LoadSettingsAsync(string baseUrl)
    {
        await LoadShellSettingsAsync(baseUrl);
        await LoadHotkeysAsync(baseUrl);
        await LoadApiSupplierPresetsAsync(baseUrl);
        await LoadApiSuppliersAsync(baseUrl);
        await LoadMemoryScopeAsync(baseUrl);
        await LoadMemoryLibraryAsync(baseUrl);
        UpdateProviderSettingsSummary();
    }

    private async Task LoadMemoryScopeAsync(string baseUrl)
    {
        var profile = CleanOrDefault(SettingsMemoryProfileBox.Text, "default");
        SettingsMemoryScopeStatusText.Text = $"Loading memory scope for {profile} ...";
        try
        {
            var scope = await GetFromJsonOrDefaultAsync<MemoryRuntimeScopeDto>(
                $"{baseUrl}/memory/scope?profile={Uri.EscapeDataString(profile)}");
            if (scope is null)
            {
                SettingsMemoryScopeStatusText.Text = "Memory scope unavailable.";
                return;
            }

            SettingsMemoryProfileBox.Text = scope.Profile;
            SettingsMemoryDatabaseScopeText.Text = scope.DatabaseScope;
            SettingsMemoryCountText.Text = scope.MemoryItemCount.ToString(CultureInfo.InvariantCulture);
            SettingsMemoryPendingReviewText.Text = scope.PendingReviewCount.ToString(CultureInfo.InvariantCulture);
            SettingsMemoryFailedJobsText.Text = scope.FailedJobsCount.ToString(CultureInfo.InvariantCulture);
            SettingsMemoryScopeStatusText.Text = $"Review {scope.PendingReviewCount} · failed {scope.FailedJobsCount}";
            ToolTip.SetTip(SettingsMemoryDatabaseScopeText, scope.DatabaseScope);
            ToolTip.SetTip(SettingsMemoryScopeStatusText, $"Database scope: {scope.DatabaseScope}");
            await LoadActiveMemoryCenterSectionAsync(baseUrl);
        }
        catch (Exception ex)
        {
            SettingsMemoryScopeStatusText.Text = $"Memory scope failed: {ex.Message}";
        }
    }

    private async Task LoadActiveMemoryCenterSectionAsync(string baseUrl)
    {
        if (_memoryCenterSection.Equals("review", StringComparison.OrdinalIgnoreCase))
        {
            await LoadMemoryReviewAsync(baseUrl);
            return;
        }

        if (_memoryCenterSection.Equals("library", StringComparison.OrdinalIgnoreCase))
        {
            await LoadMemoryLibraryAsync(baseUrl);
            return;
        }

        SettingsMemoryLibraryStatusText.Text = _memoryCenterSection.Equals("used", StringComparison.OrdinalIgnoreCase)
            ? "Used / Why translated will show recent memory usage when the API is connected."
            : "Conflicts will show competing translations when conflict detection is connected.";
    }

    private async void SettingsMemoryTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab } || string.IsNullOrWhiteSpace(tab))
        {
            return;
        }

        _memoryCenterSection = tab.Trim().ToLowerInvariant();
        ApplyMemoryCenterSection();
        await LoadActiveMemoryCenterSectionAsync(NormalizeBaseUrl(ApiUrlBox.Text));
    }

    private void ApplyMemoryCenterSection()
    {
        var section = string.IsNullOrWhiteSpace(_memoryCenterSection)
            ? "library"
            : _memoryCenterSection.Trim().ToLowerInvariant();
        var library = section == "library";
        var review = section == "review";
        var used = section == "used";
        var conflicts = section == "conflicts";

        SettingsMemoryLibraryFilterPanel.IsVisible = true;
        SettingsMemoryLibraryContent.IsVisible = library;
        SettingsMemoryReviewContent.IsVisible = review;
        SettingsMemoryUsedContent.IsVisible = used;
        SettingsMemoryConflictsContent.IsVisible = conflicts;

        ApplyMemoryTabState(SettingsMemoryLibraryTabButton, library);
        ApplyMemoryTabState(SettingsMemoryReviewTabButton, review);
        ApplyMemoryTabState(SettingsMemoryUsedTabButton, used);
        ApplyMemoryTabState(SettingsMemoryConflictsTabButton, conflicts);
        ApplyMemoryComplexityState();
    }

    private static void ApplyMemoryTabState(Button button, bool active)
    {
        button.Classes.Set("primary", active);
        button.Classes.Set("ghost", !active);
    }

    private void SettingsMemoryAdvancedFiltersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _memoryAdvancedFiltersVisible = !_memoryAdvancedFiltersVisible;
        ApplyMemoryComplexityState();
    }

    private void SettingsMemoryEditorAdvancedButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _memoryEditorAdvancedVisible = !_memoryEditorAdvancedVisible;
        ApplyMemoryComplexityState();
    }

    private void ApplyMemoryComplexityState()
    {
        SettingsMemoryAdvancedFiltersPanel.IsVisible = _memoryAdvancedFiltersVisible;
        SettingsMemoryAdvancedFiltersButton.Content = _memoryAdvancedFiltersVisible ? "Hide filters" : "Filters";
        SettingsMemoryEditorAdvancedPanel.IsVisible = _memoryEditorAdvancedVisible;
        SettingsMemoryEditorAdvancedButton.Content = _memoryEditorAdvancedVisible ? "Hide details" : "Details";
    }

    private void InitializeMemoryLibraryControls()
    {
        _suppressMemoryLibraryRefresh = true;
        try
        {
            var kindOptions = new List<SelectOption>
            {
                new("", "All types"),
                new("term", "Term"),
                new("ocr_correction", "OCR correction"),
                new("style", "Style"),
                new("translation", "Translation example"),
                new("scene_summary", "Scene summary")
            };
            SettingsMemoryKindFilterSelect.ItemsSource = kindOptions;
            SettingsMemoryKindFilterSelect.SelectedIndex = 0;
            SettingsMemoryEditorKindSelect.ItemsSource = kindOptions.Skip(1).ToList();
            SettingsMemoryEditorKindSelect.SelectedIndex = 0;

            var trustFilterOptions = new List<SelectOption>
            {
                new("", "All trust"),
                new("user_verified", "User verified"),
                new("trusted_import", "Trusted import"),
                new("local_generated", "Candidate"),
                new("untrusted_import", "Imported"),
                new("quarantined", "Quarantined")
            };
            SettingsMemoryTrustFilterSelect.ItemsSource = trustFilterOptions;
            SettingsMemoryTrustFilterSelect.SelectedIndex = 0;
            SettingsMemoryEditorTrustSelect.ItemsSource = trustFilterOptions.Skip(1).ToList();
            SettingsMemoryEditorTrustSelect.SelectedIndex = 0;

            var visibilityOptions = new List<SelectOption>
            {
                new("", "All visibility"),
                new("profile", "Profile"),
                new("shared", "Shared"),
                new("private", "Private")
            };
            SettingsMemoryVisibilityFilterSelect.ItemsSource = visibilityOptions;
            SettingsMemoryVisibilityFilterSelect.SelectedIndex = 0;
            SettingsMemoryEditorVisibilitySelect.ItemsSource = visibilityOptions.Skip(1).ToList();
            SettingsMemoryEditorVisibilitySelect.SelectedIndex = 0;

            SettingsMemoryActiveFilterSelect.ItemsSource = new List<SelectOption>
            {
                new("active", "Active"),
                new("all", "All"),
                new("inactive", "Inactive")
            };
            SettingsMemoryActiveFilterSelect.SelectedIndex = 0;

            ResetMemoryEditor();
            ResetMemoryReviewDetail();
            ApplyMemoryCenterSection();
            ApplyMemoryComplexityState();
        }
        finally
        {
            _suppressMemoryLibraryRefresh = false;
        }
    }

    private async Task LoadMemoryLibraryAsync(string baseUrl)
    {
        var profile = CleanOrDefault(SettingsMemoryProfileBox.Text, "default");
        SettingsMemoryLibraryStatusText.Text = $"Loading library for {profile} ...";
        try
        {
            var url = BuildMemoryLibraryUrl(baseUrl, profile);
            var items = await GetFromJsonOrDefaultAsync<List<MemoryItemDto>>(url) ?? [];
            var activeFilter = SelectedId(SettingsMemoryActiveFilterSelect, "active");
            if (activeFilter.Equals("inactive", StringComparison.OrdinalIgnoreCase))
            {
                items = items.Where(item => !item.IsActive).ToList();
            }

            _memoryLibraryItems.Clear();
            _memoryLibraryItems.AddRange(items);
            if (_selectedMemoryId.Length > 0 && _memoryLibraryItems.All(item => !item.Id.Equals(_selectedMemoryId, StringComparison.Ordinal)))
            {
                _selectedMemoryId = string.Empty;
            }

            RenderMemoryLibrary();
            SettingsMemoryLibraryStatusText.Text = $"{_memoryLibraryItems.Count} memory item(s) loaded for {profile}.";
        }
        catch (Exception ex)
        {
            SettingsMemoryLibraryStatusText.Text = $"Memory library failed: {ex.Message}";
        }
    }

    private string BuildMemoryLibraryUrl(string baseUrl, string profile)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("profile", profile),
            new("limit", "200")
        };
        AddQueryIfPresent(query, "type", SelectedId(SettingsMemoryKindFilterSelect, string.Empty));
        AddQueryIfPresent(query, "trust", SelectedId(SettingsMemoryTrustFilterSelect, string.Empty));
        AddQueryIfPresent(query, "visibility", SelectedId(SettingsMemoryVisibilityFilterSelect, string.Empty));
        AddQueryIfPresent(query, "q", SettingsMemoryLibrarySearchBox.Text);
        var activeFilter = SelectedId(SettingsMemoryActiveFilterSelect, "active");
        if (!activeFilter.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            query.Add(new("includeInactive", "true"));
        }

        return $"{baseUrl}/memories?{string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";
    }

    private static void AddQueryIfPresent(List<KeyValuePair<string, string>> query, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add(new(key, value.Trim()));
        }
    }

    private void RenderMemoryLibrary()
    {
        SettingsMemoryLibraryListPanel.Children.Clear();
        SettingsMemoryLibraryCountText.Text = $"{_memoryLibraryItems.Count} items";
        if (_memoryLibraryItems.Count == 0)
        {
            SettingsMemoryLibraryListPanel.Children.Add(new Border
            {
                Classes = { "memoryItem" },
                Child = new TextBlock
                {
                    Text = "No memories match this view. Try clearing filters or create a new memory.",
                    Classes = { "muted" },
                    TextWrapping = TextWrapping.Wrap
                }
            });
            if (_selectedMemoryId.Length == 0)
            {
                ResetMemoryEditor();
            }

            return;
        }

        foreach (var item in _memoryLibraryItems)
        {
            SettingsMemoryLibraryListPanel.Children.Add(CreateMemoryLibraryRow(item));
        }

        var selected = SelectedMemoryItem();
        if (selected is not null)
        {
            PopulateMemoryEditor(selected);
        }
        else if (_selectedMemoryId.Length == 0)
        {
            ResetMemoryEditor();
        }
    }

    private Control CreateMemoryLibraryRow(MemoryItemDto item)
    {
        var selected = item.Id.Equals(_selectedMemoryId, StringComparison.Ordinal);
        var border = new Border
        {
            Classes = { "memoryItem" },
            Tag = item.Id,
        };
        if (selected)
        {
            border.Classes.Add("selected");
        }

        border.PointerPressed += SettingsMemoryLibraryItem_OnPointerPressed;

        var title = string.IsNullOrWhiteSpace(item.SourceText)
            ? item.MemoryKind
            : OneLine(item.SourceText, 72);
        var target = string.IsNullOrWhiteSpace(item.TargetText)
            ? item.Note
            : OneLine(item.TargetText, 90);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8
        };
        header.Children.Add(CreateMemoryBadge(DisplayMemoryKind(item.MemoryKind), item.IsActive ? "good" : string.Empty));
        var titleBlock = new TextBlock
        {
            Text = title,
            Classes = { "value" },
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleBlock, 1);
        header.Children.Add(titleBlock);
        var state = new TextBlock
        {
            Text = item.IsActive ? "active" : "disabled",
            Classes = { item.IsActive ? "muted" : "faint" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(state, 2);
        header.Children.Add(state);

        var meta = new TextBlock
        {
            Text = $"{item.SourceLanguage} -> {item.TargetLanguage}  ·  {DisplayTrust(item.TrustLevel)}  ·  used {item.UseCount}",
            Classes = { "faint" },
            TextWrapping = TextWrapping.Wrap
        };
        var body = new TextBlock
        {
            Text = target,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 44
        };
        border.Child = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                header,
                meta,
                body
            }
        };

        ToolTip.SetTip(border, $"{item.SourceText}\n=> {item.TargetText}\n{item.Note}".Trim());
        return border;
    }

    private static Border CreateMemoryBadge(string text, string tone = "")
    {
        var badge = new Border
        {
            Classes = { "memoryBadge" },
            Child = new TextBlock
            {
                Text = text,
                Classes = { "memoryBadgeText" },
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 120
            }
        };
        if (!string.IsNullOrWhiteSpace(tone))
        {
            badge.Classes.Add(tone);
        }

        return badge;
    }

    private void SettingsMemoryLibraryItem_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string id } || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _selectedMemoryId = id;
        var selected = SelectedMemoryItem();
        if (selected is not null)
        {
            PopulateMemoryEditor(selected);
        }

        RenderMemoryLibrary();
    }

    private MemoryItemDto? SelectedMemoryItem()
        => _memoryLibraryItems.FirstOrDefault(item => item.Id.Equals(_selectedMemoryId, StringComparison.Ordinal));

    private void PopulateMemoryEditor(MemoryItemDto item)
    {
        _selectedMemoryId = item.Id;
        SettingsMemoryEditorTitleText.Text = DisplayMemoryKind(item.MemoryKind);
        SettingsMemoryEditorMetaText.Text = $"{ShortId(item.Id)} / updated {item.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        SelectById(SettingsMemoryEditorKindSelect, item.MemoryKind);
        SettingsMemoryEditorSourceBox.Text = item.SourceLanguage;
        SettingsMemoryEditorTargetBox.Text = item.TargetLanguage;
        SelectById(SettingsMemoryEditorTrustSelect, item.TrustLevel);
        SelectById(SettingsMemoryEditorVisibilitySelect, item.Visibility);
        SettingsMemoryEditorPriorityBox.Text = item.Priority.ToString(CultureInfo.InvariantCulture);
        SettingsMemoryEditorConfidenceBox.Text = item.Confidence.ToString("0.###", CultureInfo.InvariantCulture);
        SettingsMemoryEditorSourceTextBox.Text = item.SourceText;
        SettingsMemoryEditorTargetTextBox.Text = item.TargetText;
        SettingsMemoryEditorNoteBox.Text = item.Note;
        SettingsMemoryEditorActiveCheck.IsChecked = item.IsActive;
        SettingsMemoryEditorStatusText.Text = item.IsActive ? "Ready." : "This memory is disabled.";
    }

    private void ResetMemoryEditor()
    {
        _selectedMemoryId = string.Empty;
        SettingsMemoryEditorTitleText.Text = "New memory";
        SettingsMemoryEditorMetaText.Text = "Create a profile-scoped memory item.";
        SelectById(SettingsMemoryEditorKindSelect, "term");
        SettingsMemoryEditorSourceBox.Text = "auto";
        SettingsMemoryEditorTargetBox.Text = _defaultTarget;
        SelectById(SettingsMemoryEditorTrustSelect, "user_verified");
        SelectById(SettingsMemoryEditorVisibilitySelect, "profile");
        SettingsMemoryEditorPriorityBox.Text = "0";
        SettingsMemoryEditorConfidenceBox.Text = "1";
        SettingsMemoryEditorSourceTextBox.Text = string.Empty;
        SettingsMemoryEditorTargetTextBox.Text = string.Empty;
        SettingsMemoryEditorNoteBox.Text = string.Empty;
        SettingsMemoryEditorActiveCheck.IsChecked = true;
        SettingsMemoryEditorStatusText.Text = "Ready.";
    }

    private async void SettingsMemoryLibraryRefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await LoadMemoryScopeAsync(NormalizeBaseUrl(ApiUrlBox.Text));
    }

    private void SettingsMemoryLibraryNewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _memoryCenterSection = "library";
        ApplyMemoryCenterSection();
        ResetMemoryEditor();
        RenderMemoryLibrary();
    }

    private async void SettingsMemoryLibrarySaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        var profile = CleanOrDefault(SettingsMemoryProfileBox.Text, "default");
        try
        {
            var sourceText = CleanOrDefault(SettingsMemoryEditorSourceTextBox.Text, string.Empty);
            var targetText = CleanOrDefault(SettingsMemoryEditorTargetTextBox.Text, string.Empty);
            if (sourceText.Length == 0 || targetText.Length == 0)
            {
                SettingsMemoryEditorStatusText.Text = "Source and target text are required.";
                return;
            }

            var priority = ParseIntOrDefault(SettingsMemoryEditorPriorityBox.Text, 0);
            var confidence = Math.Clamp(ParseDoubleOrDefault(SettingsMemoryEditorConfidenceBox.Text, 1), 0, 1);
            if (string.IsNullOrWhiteSpace(_selectedMemoryId))
            {
                var request = new MemoryUpsertRequestDto
                {
                    Profile = profile,
                    MemoryKind = SelectedId(SettingsMemoryEditorKindSelect, "term"),
                    Source = CleanOrDefault(SettingsMemoryEditorSourceBox.Text, "auto"),
                    Target = CleanOrDefault(SettingsMemoryEditorTargetBox.Text, _defaultTarget),
                    SourceText = sourceText,
                    TargetText = targetText,
                    Note = SettingsMemoryEditorNoteBox.Text ?? string.Empty,
                    Priority = priority,
                    Confidence = confidence,
                    Origin = "manual",
                    TrustLevel = SelectedId(SettingsMemoryEditorTrustSelect, "user_verified"),
                    Visibility = SelectedId(SettingsMemoryEditorVisibilitySelect, "profile"),
                    CreatedBy = "native-memory-library",
                    ApprovedBy = "native-memory-library"
                };
                using var response = await Http.PostAsJsonAsync($"{baseUrl}/memories", request, JsonOptions);
                response.EnsureSuccessStatusCode();
                var created = await response.Content.ReadFromJsonAsync<MemoryItemDto>(JsonOptions);
                _selectedMemoryId = created?.Id ?? string.Empty;
                SettingsMemoryEditorStatusText.Text = "Created.";
            }
            else
            {
                var request = new MemoryUpdateRequestDto
                {
                    SourceText = sourceText,
                    TargetText = targetText,
                    Note = SettingsMemoryEditorNoteBox.Text ?? string.Empty,
                    Priority = priority,
                    Confidence = confidence,
                    TrustLevel = SelectedId(SettingsMemoryEditorTrustSelect, "user_verified"),
                    Visibility = SelectedId(SettingsMemoryEditorVisibilitySelect, "profile"),
                    IsActive = SettingsMemoryEditorActiveCheck.IsChecked == true,
                    ApprovedBy = "native-memory-library"
                };
                await PatchJsonAsync($"{baseUrl}/memories/{Uri.EscapeDataString(_selectedMemoryId)}?profile={Uri.EscapeDataString(profile)}", request);
                SettingsMemoryEditorStatusText.Text = "Saved.";
            }

            await LoadMemoryScopeAsync(baseUrl);
        }
        catch (Exception ex)
        {
            SettingsMemoryEditorStatusText.Text = ex.Message;
        }
    }

    private async void SettingsMemoryLibraryDisableButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedMemoryId))
        {
            SettingsMemoryEditorStatusText.Text = "Select a memory item first.";
            return;
        }

        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        var profile = CleanOrDefault(SettingsMemoryProfileBox.Text, "default");
        try
        {
            await PatchJsonAsync(
                $"{baseUrl}/memories/{Uri.EscapeDataString(_selectedMemoryId)}?profile={Uri.EscapeDataString(profile)}",
                new MemoryUpdateRequestDto { IsActive = false });
            SettingsMemoryEditorActiveCheck.IsChecked = false;
            SettingsMemoryEditorStatusText.Text = "Disabled.";
            await LoadMemoryScopeAsync(baseUrl);
        }
        catch (Exception ex)
        {
            SettingsMemoryEditorStatusText.Text = ex.Message;
        }
    }

    private async void SettingsMemoryFilter_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMemoryLibraryRefresh)
        {
            return;
        }

        await LoadMemoryLibraryAsync(NormalizeBaseUrl(ApiUrlBox.Text));
    }

    private async void SettingsMemoryLibrarySearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _memoryCenterSection = "library";
            ApplyMemoryCenterSection();
            await LoadMemoryLibraryAsync(NormalizeBaseUrl(ApiUrlBox.Text));
        }
    }

    private async Task LoadMemoryReviewAsync(string baseUrl)
    {
        var profile = CleanOrDefault(SettingsMemoryProfileBox.Text, "default");
        SettingsMemoryReviewStatusText.Text = $"Loading review queue for {profile} ...";
        try
        {
            var pending = new List<MemoryItemDto>();
            foreach (var trust in new[] { "local_generated", "untrusted_import", "quarantined" })
            {
                var url = $"{baseUrl}/memories?profile={Uri.EscapeDataString(profile)}&trust={Uri.EscapeDataString(trust)}&limit=200";
                var items = await GetFromJsonOrDefaultAsync<List<MemoryItemDto>>(url) ?? [];
                pending.AddRange(items.Where(item => item.IsActive));
            }

            _memoryReviewItems.Clear();
            _memoryReviewItems.AddRange(pending
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(item => ReviewRiskRank(item))
                .ThenByDescending(item => item.UpdatedAt));
            if (_selectedMemoryReviewId.Length > 0 && _memoryReviewItems.All(item => !item.Id.Equals(_selectedMemoryReviewId, StringComparison.Ordinal)))
            {
                _selectedMemoryReviewId = string.Empty;
            }

            RenderMemoryReview();
            SettingsMemoryReviewStatusText.Text = $"{_memoryReviewItems.Count} item(s) awaiting review for {profile}.";
        }
        catch (Exception ex)
        {
            SettingsMemoryReviewStatusText.Text = $"Review queue failed: {ex.Message}";
        }
    }

    private void RenderMemoryReview()
    {
        SettingsMemoryReviewListPanel.Children.Clear();
        SettingsMemoryReviewCountText.Text = $"{_memoryReviewItems.Count} items";
        if (_memoryReviewItems.Count == 0)
        {
            SettingsMemoryReviewListPanel.Children.Add(new Border
            {
                Classes = { "memoryItem" },
                Child = new TextBlock
                {
                    Text = "No pending memories need review.",
                    Classes = { "muted" },
                    TextWrapping = TextWrapping.Wrap
                }
            });
            ResetMemoryReviewDetail();
            return;
        }

        foreach (var item in _memoryReviewItems)
        {
            SettingsMemoryReviewListPanel.Children.Add(CreateMemoryReviewRow(item));
        }

        var selected = SelectedMemoryReviewItem();
        if (selected is not null)
        {
            PopulateMemoryReviewDetail(selected);
        }
        else
        {
            ResetMemoryReviewDetail();
        }
    }

    private Control CreateMemoryReviewRow(MemoryItemDto item)
    {
        var selected = item.Id.Equals(_selectedMemoryReviewId, StringComparison.Ordinal);
        var hasFlags = HasSecurityFlags(item);
        var border = new Border
        {
            Classes = { "memoryItem" },
            Tag = item.Id,
        };
        if (selected)
        {
            border.Classes.Add("selected");
        }
        if (hasFlags)
        {
            border.Classes.Add("warning");
        }

        border.PointerPressed += SettingsMemoryReviewItem_OnPointerPressed;

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8
        };
        header.Children.Add(CreateMemoryBadge(DisplayMemoryKind(item.MemoryKind), hasFlags ? "warning" : "good"));
        var sourceTitle = new TextBlock
        {
            Text = OneLine(item.SourceText, 72),
            Classes = { "value" },
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(sourceTitle, 1);
        header.Children.Add(sourceTitle);
        var risk = new TextBlock
        {
            Text = hasFlags ? "flagged" : DisplayTrust(item.TrustLevel),
            Classes = { hasFlags ? "value" : "muted" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(risk, 2);
        header.Children.Add(risk);

        border.Child = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                header,
                new TextBlock
                {
                    Text = $"{item.SourceLanguage} -> {item.TargetLanguage}  ·  {item.UpdatedAt.ToLocalTime():MM-dd HH:mm}",
                    Classes = { "faint" },
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = OneLine(item.TargetText, 90),
                    Classes = { "muted" },
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 44
                }
            }
        };

        ToolTip.SetTip(border, $"{item.SourceText}\n=> {item.TargetText}\n{ReadableSecurityFlags(item)}".Trim());
        return border;
    }

    private void SettingsMemoryReviewItem_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string id } || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _selectedMemoryReviewId = id;
        var selected = SelectedMemoryReviewItem();
        if (selected is not null)
        {
            PopulateMemoryReviewDetail(selected);
        }

        RenderMemoryReview();
    }

    private MemoryItemDto? SelectedMemoryReviewItem()
        => _memoryReviewItems.FirstOrDefault(item => item.Id.Equals(_selectedMemoryReviewId, StringComparison.Ordinal));

    private void PopulateMemoryReviewDetail(MemoryItemDto item)
    {
        _selectedMemoryReviewId = item.Id;
        SettingsMemoryReviewTitleText.Text = DisplayMemoryKind(item.MemoryKind);
        SettingsMemoryReviewMetaText.Text = $"{ShortId(item.Id)} / {item.ProfileId} / {DisplayTrust(item.TrustLevel)} / confidence {item.Confidence:0.###}";
        SettingsMemoryReviewFlagsText.Text = HasSecurityFlags(item) ? ReadableSecurityFlags(item) : "No security flags.";
        SettingsMemoryReviewSourceTextBox.Text = item.SourceText;
        SettingsMemoryReviewTargetTextBox.Text = item.TargetText;
        SettingsMemoryReviewNoteBox.Text = item.Note;
        SettingsMemoryReviewApproveButton.IsEnabled = !HasSecurityFlags(item);
        SettingsMemoryReviewActionStatusText.Text = HasSecurityFlags(item)
            ? "Flagged items require Edit & approve."
            : "Ready.";
    }

    private void ResetMemoryReviewDetail()
    {
        _selectedMemoryReviewId = string.Empty;
        SettingsMemoryReviewTitleText.Text = "Review detail";
        SettingsMemoryReviewMetaText.Text = "Select an item from the review queue.";
        SettingsMemoryReviewFlagsText.Text = "No security flags.";
        SettingsMemoryReviewSourceTextBox.Text = string.Empty;
        SettingsMemoryReviewTargetTextBox.Text = string.Empty;
        SettingsMemoryReviewNoteBox.Text = string.Empty;
        SettingsMemoryReviewApproveButton.IsEnabled = false;
        SettingsMemoryReviewActionStatusText.Text = "Ready.";
    }

    private async void SettingsMemoryReviewRefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await LoadMemoryScopeAsync(NormalizeBaseUrl(ApiUrlBox.Text));
    }

    private async void SettingsMemoryReviewApproveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var item = SelectedMemoryReviewItem();
        if (item is null)
        {
            SettingsMemoryReviewActionStatusText.Text = "Select a review item first.";
            return;
        }

        if (HasSecurityFlags(item))
        {
            SettingsMemoryReviewActionStatusText.Text = "Flagged items require Edit & approve.";
            return;
        }

        await ReviewSelectedMemoryAsync("approve", acknowledgeSecurityFlags: false);
    }

    private async void SettingsMemoryReviewEditApproveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var item = SelectedMemoryReviewItem();
        if (item is null)
        {
            SettingsMemoryReviewActionStatusText.Text = "Select a review item first.";
            return;
        }

        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        var profile = CleanOrDefault(SettingsMemoryProfileBox.Text, "default");
        try
        {
            await PatchJsonAsync(
                $"{baseUrl}/memories/{Uri.EscapeDataString(item.Id)}?profile={Uri.EscapeDataString(profile)}",
                new MemoryUpdateRequestDto
                {
                    SourceText = SettingsMemoryReviewSourceTextBox.Text ?? string.Empty,
                    TargetText = SettingsMemoryReviewTargetTextBox.Text ?? string.Empty,
                    Note = SettingsMemoryReviewNoteBox.Text ?? string.Empty,
                    IsActive = true,
                    AcknowledgeSecurityFlags = true,
                    ApprovedBy = "native-memory-review"
                });
            await ReviewSelectedMemoryAsync("approve", acknowledgeSecurityFlags: true);
        }
        catch (Exception ex)
        {
            SettingsMemoryReviewActionStatusText.Text = ex.Message;
        }
    }

    private async void SettingsMemoryReviewRejectButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ReviewSelectedMemoryAsync("reject", acknowledgeSecurityFlags: false);
    }

    private void SettingsMemoryReviewKeepButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SettingsMemoryReviewActionStatusText.Text = "Kept as candidate.";
    }

    private async Task ReviewSelectedMemoryAsync(string action, bool acknowledgeSecurityFlags)
    {
        var item = SelectedMemoryReviewItem();
        if (item is null)
        {
            SettingsMemoryReviewActionStatusText.Text = "Select a review item first.";
            return;
        }

        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        var profile = CleanOrDefault(SettingsMemoryProfileBox.Text, "default");
        try
        {
            using var response = await Http.PostAsJsonAsync(
                $"{baseUrl}/memories/{Uri.EscapeDataString(item.Id)}/review?profile={Uri.EscapeDataString(profile)}",
                new MemoryReviewRequestDto
                {
                    Action = action,
                    ReviewedBy = "native-memory-review",
                    AcknowledgeSecurityFlags = acknowledgeSecurityFlags
                },
                JsonOptions);
            response.EnsureSuccessStatusCode();
            SettingsMemoryReviewActionStatusText.Text = action.Equals("approve", StringComparison.OrdinalIgnoreCase)
                ? "Approved."
                : "Rejected.";
            _selectedMemoryReviewId = string.Empty;
            await LoadMemoryScopeAsync(baseUrl);
        }
        catch (Exception ex)
        {
            SettingsMemoryReviewActionStatusText.Text = ex.Message;
        }
    }

    private static int ReviewRiskRank(MemoryItemDto item)
        => HasSecurityFlags(item) ? 2 :
            item.TrustLevel.Equals("quarantined", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static bool HasSecurityFlags(MemoryItemDto item)
    {
        var flags = Pick(item.SecurityFlagsJson, string.Empty);
        return flags.Length > 0 && flags is not "[]" and not "{}" and not "null";
    }

    private static string ReadableSecurityFlags(MemoryItemDto item)
    {
        if (!HasSecurityFlags(item))
        {
            return "No security flags.";
        }

        var flags = item.SecurityFlagsJson.Trim();
        return $"Security flags: {flags}";
    }

    private void EnsureSelectedProviderCard(
        IReadOnlyList<ProviderCatalogCard> cards,
        IReadOnlyList<ProviderCatalogCard> visibleCards,
        string selectedProvider)
    {
        if (visibleCards.Any(card => card.Id.Equals(_selectedProviderCatalogCardId, StringComparison.OrdinalIgnoreCase)))
        {
            if (_providerCardSelectionPinned)
            {
                return;
            }
        }

        var activeVisible = visibleCards.FirstOrDefault(card => IsProviderCatalogCardActive(card, selectedProvider));
        var activeAny = cards.FirstOrDefault(card => IsProviderCatalogCardActive(card, selectedProvider));
        _selectedProviderCatalogCardId = (activeVisible ?? activeAny ?? visibleCards.FirstOrDefault() ?? cards.FirstOrDefault())?.Id ?? string.Empty;
    }

    private ProviderCatalogCard? SelectedProviderCatalogCard()
    {
        var cards = BuildProviderCatalog();
        if (cards.Count == 0)
        {
            return null;
        }

        var selectedProvider = SelectedId(SettingsProviderSelect, _defaultProvider);
        if (!string.IsNullOrWhiteSpace(_selectedProviderCatalogCardId))
        {
            var selected = cards.FirstOrDefault(card => card.Id.Equals(_selectedProviderCatalogCardId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return cards.FirstOrDefault(card => IsProviderCatalogCardActive(card, selectedProvider)) ?? cards[0];
    }

    private ApiSupplierProfileDto? SupplierForProviderCard(ProviderCatalogCard? card)
    {
        if (card is null || !card.Source.Equals("preset", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _apiSuppliers.FirstOrDefault(supplier => supplier.PresetId.Equals(card.PresetId, StringComparison.OrdinalIgnoreCase));
    }

    private void RenderProviderDetail()
    {
        var card = SelectedProviderCatalogCard();
        if (card is null)
        {
            SettingsProviderDetailAvatarHost.Content = null;
            SettingsProviderDetailNameText.Text = "Selected provider";
            SettingsProviderDetailMetaText.Text = "Select a provider to see details.";
            SettingsProviderDescriptionText.Text = "Provider catalog is not loaded.";
            SettingsProviderModelsDescriptionText.Text = "Available models for the selected provider.";
            return;
        }

        SettingsProviderDetailAvatarHost.Content = CreateProviderAvatar(card.IconKey, card.Name, small: false);
        SettingsProviderDetailNameText.Text = card.Name;
        var locality = card.RuntimeProvider.Equals("api-compatible", StringComparison.OrdinalIgnoreCase) ? "remote" : "local";
        SettingsProviderDetailMetaText.Text = $"{ProviderCategoryLabel(card.Category)} / {card.Source} / {locality} / {Pick(card.Endpoint, card.RuntimeProvider)}";

        var supplier = SupplierForProviderCard(card);
        if (card.Source.Equals("preset", StringComparison.OrdinalIgnoreCase))
        {
            var supplierState = supplier is null
                ? "Not configured yet. Use Add or select this card to create the supplier profile."
                : $"{supplier.Protocol} / active model {Pick(supplier.ActiveModel, card.Model)} / key {(supplier.HasApiKey ? "saved" : "missing")}";
            SettingsProviderDescriptionText.Text = $"{card.Description}\n{supplierState}";
            return;
        }

        SettingsProviderDescriptionText.Text = $"{card.Description}\nDefault route model: {Pick(card.Model, "default route")}.";
    }

    private void RenderProviderCatalog()
    {
        SettingsProviderCatalogPanel.Children.Clear();
        var cards = BuildProviderCatalog();
        if (cards.Count == 0)
        {
            AddEmptySettingsNote(SettingsProviderCatalogPanel, "Provider catalog is not loaded.");
            RenderProviderDetail();
            return;
        }

        var selectedProvider = SelectedId(SettingsProviderSelect, _defaultProvider);
        var filter = ComboValue(SettingsProviderFilterSelect, "All");
        var visibleCards = cards
            .Where(card => ProviderCatalogCardMatchesFilter(card, filter))
            .OrderBy(card => ProviderCategoryRank(card.Category))
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        EnsureSelectedProviderCard(cards, visibleCards, selectedProvider);

        var groups = visibleCards
            .GroupBy(card => NormalizeProviderCategory(card.Category))
            .ToList();

        if (groups.Count == 0)
        {
            AddEmptySettingsNote(SettingsProviderCatalogPanel, "No providers match this view.");
            RenderProviderDetail();
            return;
        }

        foreach (var group in groups)
        {
            AddSettingsGroupLabel(SettingsProviderCatalogPanel, ProviderCategoryLabel(group.Key));

            foreach (var card in group)
            {
                var isActiveRoute = IsProviderCatalogCardActive(card, selectedProvider);
                var isSelected = card.Id.Equals(_selectedProviderCatalogCardId, StringComparison.OrdinalIgnoreCase);
                var button = new Button
                {
                    Tag = card.Id,
                    MinHeight = 58,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new global::Avalonia.Thickness(0, 0, 0, 7)
                };
                button.Classes.Add("providerCard");
                button.Classes.Set("active", isActiveRoute || isSelected);
                button.Click += ProviderCatalogButton_OnClick;

                var header = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    ColumnSpacing = 8
                };
                header.Children.Add(CreateProviderAvatar(card.IconKey, card.Name, small: true));
                var titleStack = new StackPanel { Spacing = 2 };
                titleStack.Children.Add(new TextBlock { Text = card.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var count = card.ModelCount > 0 ? $" / {card.ModelCount} models" : string.Empty;
                var category = new TextBlock { Text = $"{ProviderCategoryLabel(card.Category)}{count}", TextTrimming = TextTrimming.CharacterEllipsis };
                category.Classes.Add("muted");
                titleStack.Children.Add(category);
                Grid.SetColumn(titleStack, 1);
                header.Children.Add(titleStack);
                var supplier = card.Source.Equals("preset", StringComparison.OrdinalIgnoreCase)
                    ? SupplierForProviderCard(card)
                    : null;
                var status = ProviderBadge(isActiveRoute
                    ? "active"
                    : card.Source.Equals("preset", StringComparison.OrdinalIgnoreCase)
                        ? supplier is null ? "add" : "ready"
                        : "ready");
                Grid.SetColumn(status, 2);
                header.Children.Add(status);
                button.Content = header;
                ToolTip.SetTip(button, CreateProviderCatalogTooltip(card, supplier));
                SettingsProviderCatalogPanel.Children.Add(button);
            }
        }

        RenderProviderDetail();
    }

    private static Control CreateProviderCatalogTooltip(ProviderCatalogCard card, ApiSupplierProfileDto? supplier)
    {
        var root = new StackPanel
        {
            Spacing = 4,
            MaxWidth = 260
        };
        root.Children.Add(new TextBlock
        {
            Text = card.Name,
            Foreground = Brush.Parse("#F8FBFF"),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(new TextBlock
        {
            Text = card.Description,
            Foreground = Brush.Parse("#B8C7DA"),
            TextWrapping = TextWrapping.Wrap
        });

        var route = card.Source.Equals("preset", StringComparison.OrdinalIgnoreCase)
            ? supplier is null
                ? $"Status: available to configure\nModel: {Pick(card.Model, "default model")}"
                : $"Status: configured\nModel: {Pick(supplier.ActiveModel, card.Model)}"
            : $"Route: {Pick(card.Endpoint, card.RuntimeProvider)}\nModel: {Pick(card.Model, "default route")}";

        root.Children.Add(new TextBlock
        {
            Text = route,
            Foreground = Brush.Parse("#8FA6C0"),
            FontFamily = new FontFamily("JetBrains Mono, Cascadia Code, Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = Brush.Parse("#0B1320"),
            BorderBrush = Brush.Parse("#2F4665"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = root
        };
    }

    private async void ProviderCatalogButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string cardId } || string.IsNullOrWhiteSpace(cardId))
        {
            return;
        }

        var card = BuildProviderCatalog().FirstOrDefault(item => item.Id.Equals(cardId, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            return;
        }

        _selectedProviderCatalogCardId = card.Id;
        _providerCardSelectionPinned = true;
        if (card.Source.Equals("preset", StringComparison.OrdinalIgnoreCase))
        {
            var supplier = _apiSuppliers.FirstOrDefault(item => item.PresetId.Equals(card.PresetId, StringComparison.OrdinalIgnoreCase));
            if (supplier is not null)
            {
                await ActivateApiSupplierAsync(supplier.Id);
            }
            else
            {
                OpenApiSupplierEditor(null, card.PresetId);
                SettingsApiSupplierStatusText.Text = $"Configure {card.Name} supplier.";
            }
            RenderProviderCatalog();
            RenderModelCatalog();
            return;
        }

        SelectById(SettingsProviderSelect, card.RuntimeProvider);
        SyncComboSelection(ProviderSelect, card.RuntimeProvider);
        RenderProviderCatalog();
        await LoadModelsForSelectedProviderAsync();
    }

    private void RenderModelCatalog()
    {
        SettingsModelListPanel.Children.Clear();
        var selectedCard = SelectedProviderCatalogCard();
        if (selectedCard?.Source.Equals("preset", StringComparison.OrdinalIgnoreCase) == true)
        {
            RenderPresetModelCatalog(selectedCard);
            return;
        }

        var selectedModel = SelectedId(SettingsModelSelect, _defaultModel);
        if (_modelCatalog.Count == 0)
        {
            var fallback = selectedModel;
            if (string.IsNullOrWhiteSpace(fallback))
            {
                fallback = "model list unavailable";
            }
            SettingsProviderModelsDescriptionText.Text = "No model catalog available.";
            AddEmptySettingsNote(SettingsModelListPanel, fallback);
            return;
        }

        var recommended = _modelCatalog
            .Where(IsRecommendedModel)
            .OrderByDescending(model => model.IsDefault)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var recommendedIds = recommended.Select(model => model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = _modelCatalog
            .Where(model => model.IsInstalled && !recommendedIds.Contains(model.Name))
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var available = _modelCatalog
            .Where(model => !model.IsInstalled && !recommendedIds.Contains(model.Name))
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var installedCount = _modelCatalog.Count(model => model.IsInstalled);
        SettingsProviderModelsDescriptionText.Text = $"{installedCount} installed, {_modelCatalog.Count} total. Hover for details.";
        AddModelGroup("Recommended", recommended, selectedModel);
        AddModelGroup("Installed", installed, selectedModel);
        AddModelGroup("Available", available, selectedModel);
    }

    private void RenderPresetModelCatalog(ProviderCatalogCard card)
    {
        var supplier = SupplierForProviderCard(card);
        if (supplier is null)
        {
            var preset = _apiSupplierPresets.FirstOrDefault(item => item.Id.Equals(card.PresetId, StringComparison.OrdinalIgnoreCase));
            if (preset?.RecommendedModels.Count > 0)
            {
                SettingsProviderModelsDescriptionText.Text = $"{preset.RecommendedModels.Count} recommended model(s). Hover for details.";
                AddSettingsGroupLabel(SettingsModelListPanel, $"Recommended ({preset.RecommendedModels.Count})");
                foreach (var model in preset.RecommendedModels)
                {
                    var title = Pick(model.DisplayName, model.Id);
                    var button = CreateModelChipButton(
                        model.Id,
                        title,
                        "recommended / supplier preset",
                        $"Create this supplier to use {title}.",
                        active: model.Id.Equals(card.Model, StringComparison.OrdinalIgnoreCase),
                        warning: false);
                    button.Click += (_, _) =>
                    {
                        OpenApiSupplierEditor(null, card.PresetId);
                        SettingsApiSupplierModelBox.Text = model.Id;
                        SettingsApiSupplierStatusText.Text = $"Selected supplier model: {title}.";
                    };
                    SettingsModelListPanel.Children.Add(button);
                }
                return;
            }

            SettingsProviderModelsDescriptionText.Text = "Create this supplier before fetching models.";
            AddEmptySettingsNote(SettingsModelListPanel, "Use + to configure this supplier.");
            return;
        }

        if (supplier.ModelCatalog.Count == 0)
        {
            SettingsProviderModelsDescriptionText.Text = "No fetched supplier models yet.";
            AddEmptySettingsNote(SettingsModelListPanel, "Use Fetch to load supplier models.");
            return;
        }

        SettingsProviderModelsDescriptionText.Text = $"{supplier.ModelCatalog.Count} fetched model(s). Hover for details.";
        AddSettingsGroupLabel(SettingsModelListPanel, $"Fetched ({supplier.ModelCatalog.Count})");
        foreach (var model in supplier.ModelCatalog)
        {
            var meta = $"{Pick(model.Id, "model")} / {Pick(model.Source, "supplier")}";
            var note = Pick(model.OwnedBy, "supplier model");
            var button = CreateModelChipButton(
                model.Id,
                Pick(model.DisplayName, model.Id),
                meta,
                note,
                active: false,
                warning: false);
            button.Click += SupplierModelButton_OnClick;
            ToolTip.SetTip(button, $"{Pick(model.DisplayName, model.Id)}\n{meta}\n{note}");
            SettingsModelListPanel.Children.Add(button);
        }
    }

    private void SupplierModelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string model } || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        if (!SettingsApiSupplierEditorPanel.IsVisible)
        {
            var supplier = SupplierForProviderCard(SelectedProviderCatalogCard());
            OpenApiSupplierEditor(supplier);
        }
        SettingsApiSupplierModelBox.Text = model;
        SettingsApiSupplierStatusText.Text = $"Selected supplier model: {model}.";
    }

    private void AddModelGroup(string label, IReadOnlyList<ModelOptionDto> models, string selectedModel)
    {
        if (models.Count == 0)
        {
            return;
        }

        AddSettingsGroupLabel(SettingsModelListPanel, $"{label} ({models.Count})");
        foreach (var model in models)
        {
            var title = Pick(model.DisplayName, model.Name);
            var badges = new List<string>();
            if (model.IsDefault)
            {
                badges.Add("default");
            }
            if (model.IsRecommended || IsRealtimeRecommendedModel(model))
            {
                badges.Add("recommended");
            }
            badges.Add(model.IsInstalled ? "installed" : "available");
            if (!string.IsNullOrWhiteSpace(model.SupplierName))
            {
                badges.Add(model.SupplierName);
            }

            var reason = Pick(model.RecommendedUse, Pick(model.RecommendationReason, Pick(model.Source, "translation model")));
            var button = CreateModelChipButton(
                model.Name,
                title,
                string.Join(" / ", badges),
                reason,
                model.Name.Equals(selectedModel, StringComparison.OrdinalIgnoreCase),
                !model.IsInstalled);
            button.Click += ModelCatalogButton_OnClick;
            ToolTip.SetTip(button, $"{title}\n{string.Join(" / ", badges)}\n{reason}\n{model.Name}");
            SettingsModelListPanel.Children.Add(button);
        }
    }

    private Button CreateModelChipButton(
        string tag,
        string title,
        string meta,
        string note,
        bool active,
        bool warning)
    {
        var button = new Button
        {
            Tag = tag,
            MinHeight = 46,
            Margin = new Thickness(0, 0, 0, 4)
        };
        button.Classes.Add("providerModelChip");
        button.Classes.Set("active", active);
        button.Classes.Set("warning", warning);

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 9
        };
        root.Children.Add(CreateModelVendorAvatar(title));

        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetColumn(titleText, 1);
        root.Children.Add(titleText);

        var badge = ProviderBadge(ModelBadgeText(meta, note, active, warning));
        Grid.SetColumn(badge, 2);
        root.Children.Add(badge);

        ToolTip.SetTip(button, $"{title}\n{meta}\n{note}");
        button.Content = root;
        return button;
    }

    private Control CreateModelVendorAvatar(string modelName)
    {
        var key = ModelVendorKey(modelName);
        var label = key switch
        {
            "qwen" => "Q",
            "gemini" => "G",
            "llamacpp" => "LL",
            "verbeam" => "V",
            _ => SupplierInitials(modelName, "M")
        };
        return CreateProviderAvatar(key, label, small: false);
    }

    private static string ModelVendorKey(string modelName)
    {
        var normalized = NormalizeSupplierKey(modelName);
        if (normalized.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("mort", StringComparison.OrdinalIgnoreCase) || normalized.Contains("verbeam", StringComparison.OrdinalIgnoreCase)
                ? "verbeam"
                : "qwen";
        }
        if (normalized.Contains("gemma", StringComparison.OrdinalIgnoreCase))
        {
            return "gemma";
        }
        if (normalized.Contains("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "gemini";
        }
        if (normalized.Contains("hymt", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("tencent", StringComparison.OrdinalIgnoreCase))
        {
            return "tencent";
        }
        if (normalized.Contains("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return "ollama";
        }
        if (normalized.Contains("moonshot", StringComparison.OrdinalIgnoreCase))
        {
            return "moonshot";
        }
        return "api";
    }

    private static string ModelBadgeText(string meta, string note, bool active, bool warning)
    {
        var joined = $"{meta} {note}";
        if (active)
        {
            return "best";
        }
        if (joined.Contains("default", StringComparison.OrdinalIgnoreCase)
            || joined.Contains("realtime best", StringComparison.OrdinalIgnoreCase))
        {
            return "best";
        }
        if (joined.Contains("slower", StringComparison.OrdinalIgnoreCase)
            || joined.Contains("slow", StringComparison.OrdinalIgnoreCase))
        {
            return "slow";
        }
        if (joined.Contains("installed", StringComparison.OrdinalIgnoreCase))
        {
            return "installed";
        }
        if (joined.Contains("available", StringComparison.OrdinalIgnoreCase))
        {
            return warning ? "available" : "optional";
        }
        return warning ? "available" : "model";
    }

    private void ModelCatalogButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string model } || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        _suppressSettingsSync = true;
        try
        {
            SelectById(SettingsModelSelect, model);
            SelectById(ModelSelect, model);
        }
        finally
        {
            _suppressSettingsSync = false;
        }

        UpdateProviderSettingsSummary();
        RenderModelCatalog();
        UpdateTranslateSummary();
    }

    private async Task LoadApiSupplierPresetsAsync(string baseUrl)
    {
        try
        {
            var result = await GetFromJsonOrDefaultAsync<ApiSupplierPresetCatalogDto>($"{baseUrl}/translation/api-supplier-presets");
            _apiSupplierPresets.Clear();
            _apiSupplierPresets.AddRange((result?.Presets ?? [])
                .OrderBy(preset => preset.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase));
            EnsureCustomApiSupplierPreset();

            _suppressSettingsSync = true;
            SetOptions(
                SettingsApiSupplierPresetSelect,
                _apiSupplierPresets.Select(preset => new SelectOption(preset.Id, preset.DisplayName, preset.Category)).ToList(),
                "custom");
            _suppressSettingsSync = false;
            RenderProviderCatalog();
        }
        catch (Exception ex)
        {
            EnsureCustomApiSupplierPreset();
            SettingsApiSupplierStatusText.Text = $"Supplier presets unavailable: {ex.Message}";
        }
    }

    private async Task RefreshApiSupplierPresetsAsync()
    {
        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        try
        {
            using var response = await Http.PostAsync($"{baseUrl}/translation/api-supplier-presets/refresh", null);
            await EnsureSuccessAsync(response);
            await LoadApiSupplierPresetsAsync(baseUrl);
            SettingsApiSupplierStatusText.Text = "Supplier presets refreshed.";
        }
        catch (Exception ex)
        {
            SettingsApiSupplierStatusText.Text = $"Preset refresh failed: {ex.Message}";
        }
    }

    private async Task LoadApiSuppliersAsync(string baseUrl)
    {
        try
        {
            var route = await GetFromJsonOrDefaultAsync<TranslationRouteDto>($"{baseUrl}/translation/routes/active");
            _activeApiSupplierId = route?.Provider.Equals("api-compatible", StringComparison.OrdinalIgnoreCase) == true
                ? route.SupplierId
                : string.Empty;

            var suppliers = await GetFromJsonOrDefaultAsync<List<ApiSupplierProfileDto>>($"{baseUrl}/translation/api-suppliers") ?? [];
            _apiSuppliers.Clear();
            _apiSuppliers.AddRange(suppliers.OrderBy(supplier => supplier.Name, StringComparer.OrdinalIgnoreCase));
            RenderApiSuppliers();
            RenderProviderCatalog();
            RenderModelCatalog();
        }
        catch (Exception ex)
        {
            _apiSuppliers.Clear();
            RenderApiSuppliers();
            RenderProviderCatalog();
            RenderModelCatalog();
            SettingsApiSupplierStatusText.Text = $"API suppliers unavailable: {ex.Message}";
        }
    }

    private void RenderApiSuppliers()
    {
        SettingsApiSupplierListPanel.Children.Clear();
        var activeSupplier = _apiSuppliers.FirstOrDefault(supplier => supplier.Id.Equals(_activeApiSupplierId, StringComparison.OrdinalIgnoreCase));
        SettingsApiSupplierStatusText.Text = _apiSuppliers.Count == 0
            ? "No suppliers"
            : activeSupplier is null
                ? $"{_apiSuppliers.Count} saved"
                : $"{activeSupplier.Name} active";
        ToolTip.SetTip(
            SettingsApiSupplierStatusText,
            _apiSuppliers.Count == 0
                ? "No API-compatible suppliers are configured. Use + to add one."
                : $"{_apiSuppliers.Count} API-compatible supplier(s). Active: {activeSupplier?.Name ?? "none"}.");

        if (_apiSuppliers.Count == 0)
        {
            AddEmptySettingsNote(SettingsApiSupplierListPanel, "Click Add to create an OpenAI-compatible supplier.");
            return;
        }

        foreach (var supplier in _apiSuppliers)
        {
            var card = new Border();
            card.Classes.Add("providerSupplierCard");
            var root = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
                RowSpacing = 10
            };

            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 8
            };
            header.Children.Add(CreateProviderAvatar(supplier.PresetId, supplier.Name, small: true));
            var title = new StackPanel { Spacing = 2 };
            title.Children.Add(new TextBlock { Text = supplier.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var subtitle = new TextBlock { Text = $"{supplier.Protocol} / {ShortenMiddle(supplier.BaseUrl, 34)}", TextWrapping = TextWrapping.Wrap };
            subtitle.Classes.Add("muted");
            title.Children.Add(subtitle);
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            var active = supplier.Id.Equals(_activeApiSupplierId, StringComparison.OrdinalIgnoreCase);
            var activeText = new TextBlock { Text = active ? "Active" : LastHealthLabel(supplier), VerticalAlignment = VerticalAlignment.Center };
            activeText.Classes.Add(active ? "value" : "faint");
            Grid.SetColumn(activeText, 2);
            header.Children.Add(activeText);
            root.Children.Add(header);

            var detail = new TextBlock
            {
                Text = $"model: {Pick(supplier.ActiveModel, "(none)")} / key: {(supplier.HasApiKey ? "saved" : "missing")}",
                TextWrapping = TextWrapping.Wrap
            };
            detail.Classes.Add("faint");
            Grid.SetRow(detail, 1);
            root.Children.Add(detail);

            var toolbar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto,Auto"),
                ColumnSpacing = 6
            };
            toolbar.Children.Add(ApiSupplierActionButton(active ? "Active" : "Activate", supplier.Id, ApiSupplierActivateButton_OnClick, active ? "ghost" : "primary"));
            var test = ApiSupplierActionButton("Test", supplier.Id, ApiSupplierTestButton_OnClick, "ghost");
            Grid.SetColumn(test, 1);
            toolbar.Children.Add(test);
            var fetch = ApiSupplierActionButton("Fetch", supplier.Id, ApiSupplierFetchButton_OnClick, "ghost");
            Grid.SetColumn(fetch, 2);
            toolbar.Children.Add(fetch);
            var edit = ApiSupplierActionButton("Edit", supplier.Id, ApiSupplierEditButton_OnClick, "ghost");
            Grid.SetColumn(edit, 3);
            toolbar.Children.Add(edit);
            var delete = ApiSupplierActionButton("Delete", supplier.Id, ApiSupplierDeleteButton_OnClick, "danger");
            Grid.SetColumn(delete, 4);
            toolbar.Children.Add(delete);
            Grid.SetRow(toolbar, 2);
            root.Children.Add(toolbar);

            card.Child = root;
            SettingsApiSupplierListPanel.Children.Add(card);
        }
    }

    private Button ApiSupplierActionButton(string text, string supplierId, EventHandler<RoutedEventArgs> handler, string style)
    {
        var button = new Button { Tag = supplierId, Content = text };
        button.Classes.Add("compact");
        button.Classes.Add(style);
        button.Click += handler;
        return button;
    }

    private async void ApiSupplierActivateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSupplierId(sender, out var id))
        {
            return;
        }

        await ActivateApiSupplierAsync(id);
    }

    private async Task ActivateApiSupplierAsync(string id)
    {
        try
        {
            using var response = await Http.PostAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/translation/api-suppliers/{Uri.EscapeDataString(id)}/activate", null);
            await EnsureSuccessAsync(response);
            SettingsApiSupplierStatusText.Text = "Supplier activated.";
            await LoadApiSuppliersAsync(NormalizeBaseUrl(ApiUrlBox.Text));
            await LoadModelsForSelectedProviderAsync();
            RenderProviderCatalog();
        }
        catch (Exception ex)
        {
            SettingsApiSupplierStatusText.Text = $"Activate failed: {ex.Message}";
        }
    }

    private async void ApiSupplierTestButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSupplierId(sender, out var id))
        {
            return;
        }

        try
        {
            using var response = await Http.PostAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/translation/api-suppliers/{Uri.EscapeDataString(id)}/test", null);
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<ApiSupplierTestResultDto>(JsonOptions);
            SettingsApiSupplierStatusText.Text = result is null
                ? "Supplier test completed."
                : $"Test: {result.Status} / {(result.LatencyMs.HasValue ? $"{result.LatencyMs.Value} ms" : result.Message)}";
            await LoadApiSuppliersAsync(NormalizeBaseUrl(ApiUrlBox.Text));
        }
        catch (Exception ex)
        {
            SettingsApiSupplierStatusText.Text = $"Test failed: {ex.Message}";
        }
    }

    private async void ApiSupplierFetchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSupplierId(sender, out var id))
        {
            return;
        }

        try
        {
            using var response = await Http.PostAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/translation/api-suppliers/{Uri.EscapeDataString(id)}/models/fetch", null);
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<ApiSupplierModelFetchResultDto>(JsonOptions);
            SettingsApiSupplierStatusText.Text = result is null
                ? "Model fetch completed."
                : $"Fetched {result.Models.Count} model(s): {result.Status}.";
            await LoadApiSuppliersAsync(NormalizeBaseUrl(ApiUrlBox.Text));
            await LoadModelsForSelectedProviderAsync();
        }
        catch (Exception ex)
        {
            SettingsApiSupplierStatusText.Text = $"Fetch failed: {ex.Message}";
        }
    }

    private void ApiSupplierEditButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetSupplierId(sender, out var id))
        {
            OpenApiSupplierEditor(_apiSuppliers.FirstOrDefault(supplier => supplier.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private async void ApiSupplierDeleteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSupplierId(sender, out var id))
        {
            return;
        }

        try
        {
            using var response = await Http.DeleteAsync($"{NormalizeBaseUrl(ApiUrlBox.Text)}/translation/api-suppliers/{Uri.EscapeDataString(id)}");
            await EnsureSuccessAsync(response);
            SettingsApiSupplierStatusText.Text = "Supplier deleted.";
            if (_editingApiSupplierId.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                SettingsApiSupplierEditorPanel.IsVisible = false;
                _editingApiSupplierId = string.Empty;
            }
            await LoadApiSuppliersAsync(NormalizeBaseUrl(ApiUrlBox.Text));
        }
        catch (Exception ex)
        {
            SettingsApiSupplierStatusText.Text = $"Delete failed: {ex.Message}";
        }
    }

    private void OpenApiSupplierEditor(ApiSupplierProfileDto? supplier, string preferredPresetId = "custom")
    {
        EnsureCustomApiSupplierPreset();
        _editingApiSupplierId = supplier?.Id ?? string.Empty;
        SettingsApiSupplierEditorPanel.IsVisible = true;
        SettingsApiSupplierEditorTitle.Text = supplier is null ? "Add API Supplier" : "Edit API Supplier";

        _suppressSettingsSync = true;
        try
        {
            SetOptions(
                SettingsApiSupplierPresetSelect,
                _apiSupplierPresets.Select(preset => new SelectOption(preset.Id, preset.DisplayName, preset.Category)).ToList(),
                supplier?.PresetId ?? preferredPresetId);
        }
        finally
        {
            _suppressSettingsSync = false;
        }

        SettingsApiSupplierNameBox.Text = supplier?.Name ?? string.Empty;
        SettingsApiSupplierBaseUrlBox.Text = supplier?.BaseUrl ?? string.Empty;
        SettingsApiSupplierModelBox.Text = supplier?.ActiveModel ?? string.Empty;
        SettingsApiSupplierApiKeyBox.Text = string.Empty;

        if (supplier is null)
        {
            ApplySelectedApiSupplierPreset();
        }
    }

    private void ApplySelectedApiSupplierPreset()
    {
        var presetId = SelectedId(SettingsApiSupplierPresetSelect, "custom");
        var preset = _apiSupplierPresets.FirstOrDefault(item => item.Id.Equals(presetId, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SettingsApiSupplierNameBox.Text))
        {
            SettingsApiSupplierNameBox.Text = preset.DisplayName;
        }
        if (string.IsNullOrWhiteSpace(SettingsApiSupplierBaseUrlBox.Text))
        {
            SettingsApiSupplierBaseUrlBox.Text = preset.BaseUrl;
        }
        if (string.IsNullOrWhiteSpace(SettingsApiSupplierModelBox.Text))
        {
            SettingsApiSupplierModelBox.Text = preset.DefaultModel;
        }
    }

    private async Task SaveApiSupplierAsync()
    {
        var name = CleanOrDefault(SettingsApiSupplierNameBox.Text, string.Empty);
        var baseUrl = CleanOrDefault(SettingsApiSupplierBaseUrlBox.Text, string.Empty);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseUrl))
        {
            SettingsApiSupplierStatusText.Text = "Name and Base URL are required.";
            return;
        }

        var request = new ApiSupplierUpsertRequestDto
        {
            PresetId = SelectedId(SettingsApiSupplierPresetSelect, "custom"),
            Name = name,
            BaseUrl = baseUrl,
            ApiKey = SettingsApiSupplierApiKeyBox.Text ?? string.Empty,
            ActiveModel = CleanOrDefault(SettingsApiSupplierModelBox.Text, string.Empty)
        };

        try
        {
            var baseApiUrl = NormalizeBaseUrl(ApiUrlBox.Text);
            using var response = string.IsNullOrWhiteSpace(_editingApiSupplierId)
                ? await Http.PostAsJsonAsync($"{baseApiUrl}/translation/api-suppliers", request, JsonOptions)
                : await Http.PutAsJsonAsync($"{baseApiUrl}/translation/api-suppliers/{Uri.EscapeDataString(_editingApiSupplierId)}", request, JsonOptions);
            await EnsureSuccessAsync(response);
            SettingsApiSupplierStatusText.Text = string.IsNullOrWhiteSpace(_editingApiSupplierId)
                ? "Supplier added."
                : "Supplier updated.";
            _editingApiSupplierId = string.Empty;
            SettingsApiSupplierEditorPanel.IsVisible = false;
            await LoadApiSuppliersAsync(baseApiUrl);
            await LoadModelsForSelectedProviderAsync();
        }
        catch (Exception ex)
        {
            SettingsApiSupplierStatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private static bool TryGetSupplierId(object? sender, out string id)
    {
        id = sender is Button { Tag: string value } ? value : string.Empty;
        return !string.IsNullOrWhiteSpace(id);
    }

    private async Task LoadShellSettingsAsync(string baseUrl)
    {
        try
        {
            using var settings = await GetJsonAsync($"{baseUrl}/shell-settings");
            RenderShellSettings(settings.RootElement);
        }
        catch (Exception ex)
        {
            SettingsShellStatusText.Text = $"Performance settings unavailable: {ex.Message}";
        }
    }

    private void RenderShellSettings(JsonElement settings)
    {
        var gpuMode = GetString(settings, "webView2GpuMode", "balanced");
        var browserQuality = GetString(settings, "browserRegionQuality", "balanced");
        var additionalArgs = GetString(settings, "webView2AdditionalArgs", string.Empty);

        SetOptions(SettingsGpuModeSelect, ReadOptionArray(settings, "webView2GpuModes"), gpuMode);
        SetOptions(SettingsRegionQualitySelect, ReadOptionArray(settings, "browserRegionQualities"), browserQuality);
        SettingsChromiumArgsBox.Text = additionalArgs;

        var requiresRestart = GetNestedBool(settings, ["requiresRestart"]);
        SettingsShellStatusText.Text = requiresRestart
            ? "Saved. Restart the App window to apply WebView2 rendering changes."
            : "Balanced preview and 1080p/30fps are recommended defaults.";
    }

    private async Task SaveShellSettingsAsync()
    {
        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        SettingsShellStatusText.Text = "Saving performance settings...";
        try
        {
            var payload = new
            {
                webView2GpuMode = SelectedId(SettingsGpuModeSelect, "balanced"),
                webView2AdditionalArgs = SettingsChromiumArgsBox.Text ?? string.Empty,
                browserRegionQuality = SelectedId(SettingsRegionQualitySelect, "balanced")
            };
            using var response = await Http.PutAsJsonAsync($"{baseUrl}/shell-settings", payload, JsonOptions);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var settings = await JsonDocument.ParseAsync(stream);
            RenderShellSettings(settings.RootElement);
            LogText.Text = "Performance settings saved.";
        }
        catch (Exception ex)
        {
            SettingsShellStatusText.Text = ex.Message;
        }
    }

    private async Task ResetShellSettingsAsync()
    {
        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        SettingsShellStatusText.Text = "Resetting performance settings...";
        try
        {
            using var response = await Http.PostAsync($"{baseUrl}/shell-settings/reset", null);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var settings = await JsonDocument.ParseAsync(stream);
            RenderShellSettings(settings.RootElement);
            LogText.Text = "Performance settings reset.";
        }
        catch (Exception ex)
        {
            SettingsShellStatusText.Text = ex.Message;
        }
    }

    private async Task LoadHotkeysAsync(string baseUrl)
    {
        try
        {
            using var hotkeys = await GetJsonAsync($"{baseUrl}/hotkeys");
            RenderHotkeys(hotkeys.RootElement);
        }
        catch (Exception ex)
        {
            SettingsHotkeyStatusText.Text = $"Hotkeys unavailable: {ex.Message}";
        }
    }

    private async Task ResetHotkeysAsync()
    {
        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        SettingsHotkeyStatusText.Text = "Resetting shortcuts...";
        try
        {
            using var response = await Http.PostAsync($"{baseUrl}/hotkeys/reset", null);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var hotkeys = await JsonDocument.ParseAsync(stream);
            RenderHotkeys(hotkeys.RootElement);
            LogText.Text = "Hotkeys reset to defaults.";
        }
        catch (Exception ex)
        {
            SettingsHotkeyStatusText.Text = ex.Message;
        }
    }

    private void RenderHotkeys(JsonElement hotkeys)
    {
        SettingsHotkeyListPanel.Children.Clear();
        if (!hotkeys.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
        {
            SettingsHotkeyStatusText.Text = "No shortcuts returned by API.";
            return;
        }

        var count = 0;
        foreach (var binding in bindings.EnumerateArray())
        {
            count++;
            var action = GetString(binding, "action", string.Empty);
            var label = GetString(binding, "label", action);
            var description = GetString(binding, "description", string.Empty);
            var spec = GetString(binding, "spec", string.Empty);
            var status = GetString(binding, "status", "pending");

            var row = new Border();
            row.Classes.Add("settingsStatusCard");
            row.Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1.2*,Auto,Auto"),
                ColumnSpacing = 10,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock { Text = label, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = description, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap }
                        }
                    },
                    new Border
                    {
                        Classes = { "pill" },
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(spec) ? "disabled" : spec,
                            Classes = { "value" },
                            MaxWidth = 180,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    },
                    new Button
                    {
                        Classes = { "ghost" },
                        Tag = action,
                        Content = status,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                    }
                }
            };

            if (row.Child is Grid grid && grid.Children[1] is Control specPill)
            {
                Grid.SetColumn(specPill, 1);
            }
            if (row.Child is Grid triggerGrid && triggerGrid.Children[2] is Button trigger)
            {
                Grid.SetColumn(trigger, 2);
                trigger.Click += HotkeyTriggerButton_OnClick;
            }

            SettingsHotkeyListPanel.Children.Add(row);
        }

        SettingsHotkeyStatusText.Text = $"{count} shortcut(s) loaded.";
    }

    private async void HotkeyTriggerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        try
        {
            using var response = await Http.PostAsync($"{baseUrl}/hotkeys/trigger/{Uri.EscapeDataString(action)}", null);
            response.EnsureSuccessStatusCode();
            LogText.Text = $"Triggered shortcut action: {action}";
        }
        catch (Exception ex)
        {
            LogText.Text = ex.Message;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        await using var stream = await Http.GetStreamAsync(url);
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task<T?> GetFromJsonOrDefaultAsync<T>(string url)
    {
        try
        {
            return await Http.GetFromJsonAsync<T>(url, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static async Task PatchJsonAsync<T>(string url, T payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private void RenderHealth(JsonElement health)
    {
        var status = GetString(health, "status", "unknown");
        _defaultProvider = GetString(health, "defaultProvider", _defaultProvider);
        _defaultSource = GetString(health, "defaultSource", _defaultSource);
        _defaultTarget = GetString(health, "defaultTarget", _defaultTarget);
        _defaultMode = GetString(health, "defaultMode", _defaultMode);
        _defaultOcrProvider = GetNestedString(health, ["ocr", "defaultProvider"], _defaultOcrProvider);
        _defaultSpeechProvider = GetNestedString(health, ["speech", "defaultProvider"], _defaultSpeechProvider);
        _defaultModel = GetNestedString(health, ["llamaCpp", "model"], _defaultModel);

        ApiStatusText.Text = status.Equals("ok", StringComparison.OrdinalIgnoreCase) ? "ok" : status;
        ApiDetailText.Text = $"{_defaultSource} -> {_defaultTarget}";
        RuntimeText.Text = _defaultProvider;
        RuntimeDetailText.Text = string.IsNullOrWhiteSpace(_defaultModel) ? "model pending" : _defaultModel;
        PresetBox.Text = _defaultMode;
        UpdateProviderUsageGauges();
    }

    private void RenderProviderOptions(IReadOnlyList<ProviderOptionDto> providers)
    {
        var options = providers
            .Select(provider =>
            {
                var tags = new List<string>();
                if (provider.IsLocal)
                {
                    tags.Add("local");
                }
                if (provider.RequiresNetwork)
                {
                    tags.Add("network");
                }
                if (!string.IsNullOrWhiteSpace(provider.Kind))
                {
                    tags.Add(provider.Kind);
                }

                var detail = string.Join(" / ", tags.Distinct(StringComparer.OrdinalIgnoreCase));
                return new SelectOption(
                    provider.Name,
                    Pick(provider.DisplayName, provider.Name),
                    detail);
            })
            .ToList();

        if (options.Count == 0)
        {
            options.Add(new SelectOption(_defaultProvider, _defaultProvider, "default"));
        }

        _suppressProviderReload = true;
        _suppressSettingsSync = true;
        SetOptions(ProviderSelect, options, _defaultProvider);
        SetOptions(SettingsProviderSelect, options, _defaultProvider);
        _suppressProviderReload = false;
        _suppressSettingsSync = false;
        _providerCatalog.Clear();
        _providerCatalog.AddRange(providers);
        RenderProviderCatalog();
        UpdateProviderSettingsSummary();
    }

    private void RenderLanguageOptions(IReadOnlyList<LanguageOptionDto> languages)
    {
        var ordered = languages
            .OrderByDescending(language => language.IsDefaultSource || language.IsDefaultTarget)
            .ThenBy(language => language.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sourceOptions = new List<SelectOption>
        {
            new("auto", "Auto detect", "source language")
        };
        sourceOptions.AddRange(ordered.Select(ToLanguageOption));

        var targetOptions = ordered
            .Where(language => !language.Code.Equals("auto", StringComparison.OrdinalIgnoreCase))
            .Select(ToLanguageOption)
            .ToList();

        if (!sourceOptions.Any(option => option.Id.Equals(_defaultSource, StringComparison.OrdinalIgnoreCase)))
        {
            sourceOptions.Add(new SelectOption(_defaultSource, _defaultSource));
        }
        if (!targetOptions.Any(option => option.Id.Equals(_defaultTarget, StringComparison.OrdinalIgnoreCase)))
        {
            targetOptions.Add(new SelectOption(_defaultTarget, _defaultTarget));
        }

        _suppressSettingsSync = true;
        SetOptions(SourceLanguageSelect, sourceOptions, _defaultSource);
        SetOptions(TargetLanguageSelect, targetOptions, _defaultTarget);
        SetOptions(SettingsSourceLanguageSelect, sourceOptions, _defaultSource);
        SetOptions(SettingsTargetLanguageSelect, targetOptions, _defaultTarget);
        SetOptions(SettingsOcrLanguageSelect, sourceOptions, _defaultSource);
        SetOptions(SettingsAudioLanguageSelect, sourceOptions, _defaultSource);
        _suppressSettingsSync = false;
    }

    private async Task LoadModelsForSelectedProviderAsync(string? preferredModel = null)
    {
        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        var provider = SelectedId(ProviderSelect, _defaultProvider);
        ModelSelect.IsEnabled = false;
        SettingsModelSelect.IsEnabled = false;
        ModelSummaryText.Text = "loading models";

        try
        {
            var models = await GetFromJsonOrDefaultAsync<List<ModelOptionDto>>(
                $"{baseUrl}/translation/models?provider={Uri.EscapeDataString(provider)}") ?? [];
            _modelCatalog.Clear();
            _modelCatalog.AddRange(models);

            var options = models
                .OrderByDescending(model => model.IsDefault)
                .ThenByDescending(model => model.IsRecommended)
                .ThenByDescending(model => model.IsInstalled)
                .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(model =>
                {
                    var badges = new List<string>();
                    if (model.IsDefault)
                    {
                        badges.Add("default");
                    }
                    if (model.IsRecommended)
                    {
                        badges.Add("recommended");
                    }
                    badges.Add(model.IsInstalled ? "installed" : "not installed");
                    var supplier = string.IsNullOrWhiteSpace(model.SupplierName) ? string.Empty : $" / {model.SupplierName}";
                    return new SelectOption(
                        model.Name,
                        Pick(model.DisplayName, model.Name),
                        $"{string.Join(", ", badges)}{supplier}");
                })
                .ToList();

            if (options.Count == 0)
            {
                var fallbackModel = Pick(preferredModel, _defaultModel);
                if (string.IsNullOrWhiteSpace(fallbackModel))
                {
                    fallbackModel = Pick(provider, "default");
                }
                options.Add(new SelectOption(fallbackModel, fallbackModel, "fallback"));
            }

            var selectedModel = Pick(
                preferredModel,
                models.FirstOrDefault(model => model.IsDefault)?.Name
                    ?? models.FirstOrDefault(model => model.IsInstalled)?.Name
                    ?? options[0].Id);
            _suppressSettingsSync = true;
            SetOptions(ModelSelect, options, selectedModel);
            SetOptions(SettingsModelSelect, options, selectedModel);
            _suppressSettingsSync = false;
            RenderModelCatalog();
        }
        catch (Exception ex)
        {
            _modelCatalog.Clear();
            var fallbackModel = Pick(preferredModel, _defaultModel);
            if (string.IsNullOrWhiteSpace(fallbackModel))
            {
                fallbackModel = "default";
            }
            _suppressSettingsSync = true;
            SetOptions(ModelSelect, [new SelectOption(fallbackModel, fallbackModel, "model list unavailable")], fallbackModel);
            SetOptions(SettingsModelSelect, [new SelectOption(fallbackModel, fallbackModel, "model list unavailable")], fallbackModel);
            _suppressSettingsSync = false;
            RenderModelCatalog();
            LogText.Text = $"Model list unavailable: {ex.Message}";
        }
        finally
        {
            ModelSelect.IsEnabled = true;
            SettingsModelSelect.IsEnabled = true;
            UpdateProviderSettingsSummary();
            UpdateTranslateSummary();
        }
    }

    private async Task LoadOcrEnginesAsync(string baseUrl)
    {
        var engines = await GetFromJsonOrDefaultAsync<List<EngineOptionDto>>($"{baseUrl}/ocr/engines") ?? [];
        var options = new List<SelectOption>
        {
            new("auto", "Auto route", "backend chooses the best OCR engine")
        };

        options.AddRange(engines.Select(engine =>
        {
            var status = engine.IsAvailable ? "available" : Pick(engine.Status, "unavailable");
            var locality = engine.IsLocal ? "local" : "api";
            return new SelectOption(
                engine.Name,
                Pick(engine.DisplayName, engine.Name),
                $"{status} / {locality} / {engine.Kind}");
        }));

        _suppressSettingsSync = true;
        SetOptions(OcrProviderSelect, options, _defaultOcrProvider);
        SetOptions(SettingsOcrProviderSelect, options, _defaultOcrProvider);
        _suppressSettingsSync = false;
    }

    private async Task LoadSpeechEnginesAsync(string baseUrl)
    {
        var engines = await GetFromJsonOrDefaultAsync<List<EngineOptionDto>>($"{baseUrl}/asr/engines") ?? [];
        var options = engines.Select(engine =>
        {
            var status = engine.IsAvailable ? "available" : Pick(engine.Status, "unavailable");
            var locality = engine.IsLocal ? "local" : "api";
            return new SelectOption(
                engine.Name,
                Pick(engine.DisplayName, engine.Name),
                $"{status} / {locality} / {engine.Kind}");
        }).ToList();

        if (options.Count == 0)
        {
            options.Add(new SelectOption(_defaultSpeechProvider, _defaultSpeechProvider, "default"));
        }

        _suppressSettingsSync = true;
        SetOptions(AudioProviderSelect, options, _defaultSpeechProvider);
        SetOptions(SettingsAudioProviderSelect, options, _defaultSpeechProvider);
        _suppressSettingsSync = false;
    }

    private void RenderMemory(JsonElement memory)
    {
        UpdateNativeMemoryText();
        BackendRamText.Text = FormatMemory(GetNestedDouble(memory, ["app", "workingSetMb"]));
        WebViewRamText.Text = FormatMemory(GetNestedDouble(memory, ["webView", "workingSetMb"]));
        _providerModelMb = GetNestedDouble(memory, ["model", "workingSetMb"]);
        ModelRamText.Text = FormatMemory(_providerModelMb);

        var gpuAvailable = GetNestedBool(memory, ["gpu", "available"]);
        if (!gpuAvailable)
        {
            GpuText.Text = "--";
            GpuDetailText.Text = GetNestedString(memory, ["gpu", "reason"], "unavailable");
            _providerGpuUsedMb = 0;
            _providerGpuTotalMb = 0;
            UpdateProviderUsageGauges();
            return;
        }

        var gpuUsed = GetNestedDouble(memory, ["gpu", "usedMb"]);
        var gpuTotal = GetNestedDouble(memory, ["gpu", "totalMb"]);
        var gpuPercent = GetNestedDouble(memory, ["gpu", "percent"]);
        var tracked = GetNestedDouble(memory, ["gpu", "trackedProcessMb"]);
        GpuText.Text = $"{FormatMemory(gpuUsed)} / {FormatMemory(gpuTotal)}";
        GpuDetailText.Text = tracked > 0
            ? $"tracked model {FormatMemory(tracked)} - {gpuPercent:0.#}%"
            : $"{gpuPercent:0.#}% used";
        _providerGpuUsedMb = gpuUsed;
        _providerGpuTotalMb = gpuTotal;
        UpdateProviderUsageGauges();
    }

    private void UpdateNativeMemoryText()
    {
        using var current = Process.GetCurrentProcess();
        current.Refresh();
        AppRamText.Text = FormatMemory(current.WorkingSet64 / 1024d / 1024d);
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        SetButtonLabel(RefreshButton, busy ? "Refreshing..." : "Refresh");
        UpdateResponsiveState(Bounds.Width);
    }

    private async Task TranslateAsync()
    {
        var text = TranslateSourceBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            TranslationStatusText.Text = "Source text is empty.";
            return;
        }

        var baseUrl = NormalizeBaseUrl(ApiUrlBox.Text);
        var model = SelectedId(ModelSelect, _defaultModel);
        var request = new NativeTranslateRequest
        {
            Text = text,
            Source = SelectedId(SourceLanguageSelect, _defaultSource),
            Target = SelectedId(TargetLanguageSelect, _defaultTarget),
            Mode = CleanOrDefault(PresetBox.Text, _defaultMode),
            Surface = "text",
            Provider = SelectedId(ProviderSelect, _defaultProvider),
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            ClientRequestStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        TranslateButton.IsEnabled = false;
        SetButtonLabel(TranslateButton, "Running");
        TranslationStatusText.Text = "Sending /translate ...";
        SetTokenUsage(null);

        var elapsed = Stopwatch.StartNew();
        try
        {
            using var response = await Http.PostAsJsonAsync($"{baseUrl}/translate", request, JsonOptions);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<NativeTranslateResponse>(JsonOptions);
            elapsed.Stop();

            if (result is null)
            {
                throw new InvalidOperationException("Empty translate response.");
            }

            TranslateResultBox.Text = result.Result;
            TranslationStatusText.Text = result.ErrorCode == "0"
                ? $"Done in {elapsed.ElapsedMilliseconds} ms"
                : Pick(result.ErrorMessage, $"Translate failed ({result.ErrorCode})");
            SetTokenUsage(result.TokenUsage);
            await RefreshMemoryAsync(baseUrl);
        }
        catch (Exception ex)
        {
            elapsed.Stop();
            TranslationStatusText.Text = $"Translate failed after {elapsed.ElapsedMilliseconds} ms";
            LogText.Text = ex.Message;
        }
        finally
        {
            TranslateButton.IsEnabled = true;
            SetButtonLabel(TranslateButton, "Run");
            UpdateTranslateSummary();
            UpdateResponsiveState(Bounds.Width);
        }
    }

    private void UpdateTranslateSummary()
    {
        var source = SelectedId(SourceLanguageSelect, _defaultSource);
        var target = SelectedId(TargetLanguageSelect, _defaultTarget);
        var provider = SelectedId(ProviderSelect, _defaultProvider);
        var model = SelectedId(ModelSelect, _defaultModel);

        SourceLanguageSummaryText.Text = source.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "auto detect"
            : SelectedLabel(SourceLanguageSelect, source);
        ModelSummaryText.Text = string.IsNullOrWhiteSpace(model)
            ? provider
            : $"{provider} / {model}";
        TranslateRouteText.Text = $"{source} -> {target} / {provider}";
    }

    private void UpdateTextStats()
    {
        SourceStatsText.Text = FormatTextStats(TranslateSourceBox.Text);
        ResultStatsText.Text = FormatTextStats(TranslateResultBox.Text);
    }

    private void SetTokenUsage(NativeTokenUsage? usage)
    {
        if (usage is null)
        {
            TranslationTokenText.Text = "tokens --";
            PanelTokenText.Text = "--";
            _providerTokenTotal = 0;
            SetTokenRingState(0);
            ToolTip.SetTip(TranslationTokenText, "No token usage yet.");
            ToolTip.SetTip(PanelTokenText, "No token usage yet.");
            ToolTip.SetTip(ResultTokenPill, "No token usage yet.");
            UpdateProviderUsageGauges();
            return;
        }

        TranslationTokenText.Text = $"tokens {usage.TotalTokens}";
        PanelTokenText.Text = usage.TotalTokens > 0 ? CompactTokenCount(usage.TotalTokens) + (usage.IsEstimated ? "*" : string.Empty) : "--";
        _providerTokenTotal = usage.TotalTokens;
        SetTokenRingState(usage.TotalTokens);

        var provider = SelectedId(ProviderSelect, _defaultProvider);
        var model = SelectedId(ModelSelect, _defaultModel);
        var runtime = string.IsNullOrWhiteSpace(model) ? provider : $"{provider} / {model}";
        var estimated = usage.IsEstimated ? "estimated client-side" : "exact from provider";
        var tokenTip = $"Token usage\n"
            + $"Runtime: {runtime}\n"
            + $"Input: {usage.InputTokens:N0}\n"
            + $"Output: {usage.OutputTokens:N0}\n"
            + $"Total: {usage.TotalTokens:N0}\n"
            + $"Mode: {estimated}\n"
            + $"Source: {usage.Source}";
        ToolTip.SetTip(TranslationTokenText, tokenTip);
        ToolTip.SetTip(PanelTokenText, tokenTip);
        ToolTip.SetTip(ResultTokenPill, tokenTip);
        UpdateProviderUsageGauges();
    }

    private void UpdateProviderUsageGauges()
    {
        var modelValue = _providerGpuTotalMb > 0 ? _providerModelMb / _providerGpuTotalMb : 0;
        var vramValue = _providerGpuTotalMb > 0 ? _providerGpuUsedMb / _providerGpuTotalMb : 0;
        var tokenValue = _providerTokenTotal > 0 ? Math.Min(_providerTokenTotal / 4096d, 1d) : 0;
        var apiOk = (ApiStatusText.Text ?? string.Empty).Equals("ok", StringComparison.OrdinalIgnoreCase);

        SetUsageGauge(
            SettingsModelGauge,
            modelValue,
            _providerModelMb > 0 && _providerGpuTotalMb > 0 ? $"{modelValue * 100:0}%" : "--",
            GaugeBrush(modelValue, "#34D399"),
            $"Model memory\nUsed: {ModelRamText.Text}\nShare of VRAM: {(modelValue > 0 ? modelValue * 100 : 0):0.#}%");

        SetUsageGauge(
            SettingsVramGauge,
            vramValue,
            _providerGpuTotalMb > 0 ? $"{vramValue * 100:0}%" : "--",
            GaugeBrush(vramValue, "#F59E0B"),
            $"VRAM\nUsed: {GpuText.Text}\n{GpuDetailText.Text}");

        SetUsageGauge(
            SettingsTokenGauge,
            tokenValue,
            _providerTokenTotal > 0 ? CompactTokenCount(_providerTokenTotal) : "--",
            Brush.Parse(_providerTokenTotal > 0 ? "#22D3EE" : "#64748B"),
            _providerTokenTotal > 0
                ? $"Last request tokens\nTotal: {_providerTokenTotal:N0}\nRelative to 4k context preview: {tokenValue * 100:0.#}%"
                : "No token usage yet.");

        SetUsageGauge(
            SettingsApiGauge,
            apiOk ? 1d : 0.12d,
            apiOk ? "OK" : "--",
            Brush.Parse(apiOk ? "#3B82F6" : "#64748B"),
            $"API status\n{ApiStatusText.Text}");
    }

    private static void SetUsageGauge(UsageGauge gauge, double value, string centerText, IBrush accent, string tooltip)
    {
        gauge.Value = Math.Clamp(value, 0d, 1d);
        gauge.CenterText = centerText;
        gauge.Accent = accent;
        ToolTip.SetTip(gauge, tooltip);
    }

    private static IBrush GaugeBrush(double value, string fallback)
    {
        if (value >= 0.9)
        {
            return Brush.Parse("#EF4444");
        }
        if (value >= 0.72)
        {
            return Brush.Parse("#F59E0B");
        }
        return Brush.Parse(fallback);
    }

    private void UpdateSourceHeaderDensity(double width)
    {
        SourcePanelTitleText.IsVisible = width >= 240;
        SourceLanguagePill.IsVisible = width >= (_translateSplitStacked ? 340 : 300);
        SourceStatsPill.IsVisible = width >= (_translateSplitStacked ? 660 : 190);

        SourceLanguageSummaryText.MaxWidth = width < 360 ? 92 : 150;
        SourceStatsText.MaxWidth = width < 360 ? 92 : 120;
    }

    private void UpdateResultHeaderDensity(double width)
    {
        // Mirrors the WebView container-query behavior: keep the action button,
        // progressively collapse metadata while the splitter narrows the panel.
        ResultPanelTitleText.IsVisible = width >= 240;
        ResultModelPill.IsVisible = width >= (_translateSplitStacked ? 760 : 360);
        ResultStatsPill.IsVisible = width >= (_translateSplitStacked ? 680 : 240);
        ResultTokenPill.IsVisible = width >= (_translateSplitStacked ? 320 : 190);

        ModelSummaryText.MaxWidth = width < 520 ? 120 : 210;
        ResultStatsText.MaxWidth = width < 360 ? 92 : 120;

        ResultHeaderCopyButton.MinWidth = width < 240 ? 24 : 30;
        ResultHeaderCopyButton.Width = width < 240 ? 24 : 30;
        ResultHeaderCopyButton.MinHeight = width < 240 ? 24 : 30;
        ResultHeaderCopyButton.Height = width < 240 ? 24 : 30;
        ResultHeaderCopyButton.Padding = new global::Avalonia.Thickness(0);
    }

    private void UpdateCopyHoverState()
    {
        var text = TranslateResultBox.Text ?? string.Empty;
        var tip = string.IsNullOrWhiteSpace(text)
            ? "No result to copy."
            : $"Copy translated result\n{FormatTextStats(text)}";
        ToolTip.SetTip(ResultHeaderCopyButton, tip);
        ToolTip.SetTip(CopyButton, tip);
    }

    private async Task CopyBoxTextAsync(TextBox sourceBox, TextBlock statusText, string label)
    {
        var text = sourceBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            statusText.Text = $"No {label} to copy.";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            statusText.Text = "Clipboard is unavailable.";
            return;
        }

        await clipboard.SetTextAsync(text);
        statusText.Text = $"{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label)} copied.";
    }

    private static void NormalizeSplitGrid(Grid grid)
    {
        NormalizeColumnSplit(grid);
        NormalizeRowSplit(grid);
    }

    private static void NormalizeColumnSplit(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 3 || grid.Bounds.Width <= 0)
        {
            return;
        }

        var first = grid.ColumnDefinitions[0];
        var last = grid.ColumnDefinitions[^1];
        var available = grid.Bounds.Width;
        for (var i = 1; i < grid.ColumnDefinitions.Count - 1; i++)
        {
            available -= Math.Max(0, grid.ColumnDefinitions[i].ActualWidth);
        }

        if (available <= 1)
        {
            return;
        }

        var minimumWidth = GetSplitPaneMinimum(available, 180);
        first.MinWidth = minimumWidth;
        last.MinWidth = minimumWidth;

        var firstWidth = first.ActualWidth <= 0 ? available * 0.5 : first.ActualWidth;
        var minRatio = minimumWidth <= 0 ? 0.08 : Math.Min(0.49, minimumWidth / available);
        var ratio = Math.Clamp(firstWidth / available, minRatio, 1 - minRatio);
        first.Width = new GridLength(ratio, GridUnitType.Star);
        last.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private static void NormalizeRowSplit(Grid grid)
    {
        if (grid.RowDefinitions.Count < 3 || grid.Bounds.Height <= 0)
        {
            return;
        }

        var first = grid.RowDefinitions[0];
        var last = grid.RowDefinitions[^1];
        var available = grid.Bounds.Height;
        for (var i = 1; i < grid.RowDefinitions.Count - 1; i++)
        {
            available -= Math.Max(0, grid.RowDefinitions[i].ActualHeight);
        }

        if (available <= 1)
        {
            return;
        }

        var minimumHeight = GetSplitPaneMinimum(available, 140);
        first.MinHeight = minimumHeight;
        last.MinHeight = minimumHeight;

        var firstHeight = first.ActualHeight <= 0 ? available * 0.5 : first.ActualHeight;
        var minRatio = minimumHeight <= 0 ? 0.08 : Math.Min(0.49, minimumHeight / available);
        var ratio = Math.Clamp(firstHeight / available, minRatio, 1 - minRatio);
        first.Height = new GridLength(ratio, GridUnitType.Star);
        last.Height = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private static double GetSplitPaneMinimum(double available, double preferred)
    {
        var maxThatFitsBothSides = Math.Max(0, (available - 16) / 2);
        if (maxThatFitsBothSides <= 48)
        {
            return maxThatFitsBothSides;
        }

        return Math.Min(preferred, maxThatFitsBothSides);
    }

    private void SetTokenRingState(long totalTokens)
    {
        ResultTokenPill.Classes.Set("empty", totalTokens <= 0);
        ResultTokenPill.Classes.Set("warning", totalTokens >= 6_000 && totalTokens < 7_200);
        ResultTokenPill.Classes.Set("danger", totalTokens >= 7_200);
    }

    private static string CompactTokenCount(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 10_000)
        {
            return $"{value / 1_000d:0}K";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private string PickTargetFallback(string currentTarget)
    {
        var options = GetOptions(TargetLanguageSelect);
        var english = options.FirstOrDefault(option =>
            option.Id.Equals("en", StringComparison.OrdinalIgnoreCase) &&
            !option.Id.Equals(currentTarget, StringComparison.OrdinalIgnoreCase));
        if (english is not null)
        {
            return english.Id;
        }

        return options.FirstOrDefault(option =>
                !option.Id.Equals(currentTarget, StringComparison.OrdinalIgnoreCase))
            ?.Id ?? _defaultTarget;
    }

    private static void AddSettingsGroupLabel(StackPanel panel, string text)
    {
        var label = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Margin = new global::Avalonia.Thickness(0, panel.Children.Count == 0 ? 0 : 8, 0, 0)
        };
        label.Classes.Add("label");
        panel.Children.Add(label);
    }

    private static void AddEmptySettingsNote(StackPanel panel, string text)
    {
        var note = new Border();
        note.Classes.Add("settingsStatusCard");
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        body.Classes.Add("muted");
        note.Child = body;
        panel.Children.Add(note);
    }

    private static Border ProviderBadge(string text)
    {
        var border = new Border
        {
            Background = Brush.Parse("#0B0F14"),
            BorderBrush = Brush.Parse("#2B3544"),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new global::Avalonia.Thickness(7, 2),
            MinHeight = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontFamily = new FontFamily("JetBrains Mono, Cascadia Code, Consolas"),
            Foreground = Brush.Parse("#A8B7CB"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 88
        };
        border.Child = label;
        return border;
    }

    private Control CreateProviderAvatar(string key, string name, bool small)
    {
        var avatar = new Border();
        avatar.Classes.Add("providerAvatar");
        avatar.Classes.Set("small", small);
        var normalizedKey = NormalizeSupplierKey(key);
        avatar.Background = SupplierBrush(normalizedKey, name);

        var iconName = SupplierIconName(normalizedKey, name);
        if (!string.IsNullOrWhiteSpace(iconName))
        {
            var assetPath = $"avares://Verbeam.Desktop.Avalonia/Assets/ProviderIcons/{iconName}";
            try
            {
                avatar.Background = Brush.Parse("#F8FAFC");
                if (iconName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var displaySize = small ? 18 : 22;
                    var vectorIcon = TryCreateSvgVectorIcon(assetPath, displaySize);
                    if (vectorIcon is not null)
                    {
                        avatar.Child = vectorIcon;
                        return avatar;
                    }
                }

                using var stream = AssetLoader.Open(new Uri(assetPath));
                avatar.Child = new Image
                {
                    Source = new Bitmap(stream),
                    Stretch = Stretch.Uniform,
                    Width = small ? 18 : 22,
                    Height = small ? 18 : 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return avatar;
            }
            catch
            {
                avatar.Background = SupplierBrush(normalizedKey, name);
                // Fall through to initials when an optional provider asset is unavailable.
            }
        }

        var initials = new TextBlock
        {
            Text = SupplierAvatarLabel(normalizedKey, name),
            FontSize = small ? 10 : 12
        };
        initials.Classes.Add("providerAvatarText");
        avatar.Child = initials;
        return avatar;
    }

    private static Control? TryCreateSvgVectorIcon(string assetPath, int size)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(assetPath));
            var svg = new SKSvg();
            var picture = svg.Load(stream);
            if (picture is null)
            {
                return null;
            }

            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            var pixelSize = Math.Max(1, size * 3);
            using var bitmap = new SKBitmap(pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(pixelSize / bounds.Width, pixelSize / bounds.Height) * 0.88f;
            canvas.Translate(
                ((pixelSize - bounds.Width * scale) / 2f) - bounds.Left * scale,
                ((pixelSize - bounds.Height * scale) / 2f) - bounds.Top * scale);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
            canvas.Flush();

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var output = new MemoryStream(data.ToArray());
            return new Image
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Source = new Bitmap(output),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch
        {
            return null;
        }
    }

    private static (double Left, double Top, double Width, double Height)? TryParseSvgViewBoxValues(string? viewBox)
    {
        if (string.IsNullOrWhiteSpace(viewBox))
        {
            return null;
        }

        var parts = viewBox
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        return (x, y, width, height);
    }

    private static IBrush? ResolveSvgFill(XElement path)
    {
        var fill = path.Attribute("fill")?.Value;
        if (string.IsNullOrWhiteSpace(fill))
        {
            var style = path.Attribute("style")?.Value ?? string.Empty;
            var fillStyle = style
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(part => part.StartsWith("fill:", StringComparison.OrdinalIgnoreCase));
            fill = fillStyle?.Split(':', 2).LastOrDefault();
        }

        fill = (fill ?? "#111827").Trim();
        if (fill.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (fill.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            fill = "#111827";
        }

        try
        {
            return Brush.Parse(fill);
        }
        catch
        {
            return Brush.Parse("#111827");
        }
    }

    private List<ProviderCatalogCard> BuildProviderCatalog()
    {
        var cards = new List<ProviderCatalogCard>();
        foreach (var provider in _providerCatalog)
        {
            var category = provider.IsLocal
                || provider.Name.Contains("llama", StringComparison.OrdinalIgnoreCase)
                || provider.Name.Contains("ollama", StringComparison.OrdinalIgnoreCase)
                || provider.Name.Contains("hybrid", StringComparison.OrdinalIgnoreCase)
                    ? "local"
                    : provider.RequiresNetwork
                        ? "official"
                        : "third_party";
            cards.Add(new ProviderCatalogCard(
                provider.Name,
                Pick(provider.DisplayName, provider.Name),
                category,
                provider.Name,
                "",
                Pick(provider.DefaultModel, "default route"),
                ProviderDescription(provider),
                "runtime",
                NormalizeSupplierKey(provider.Name),
                "",
                0));
        }

        foreach (var preset in _apiSupplierPresets.Where(preset => !preset.Id.Equals("custom", StringComparison.OrdinalIgnoreCase)))
        {
            var supplier = _apiSuppliers.FirstOrDefault(item => item.PresetId.Equals(preset.Id, StringComparison.OrdinalIgnoreCase));
            if (supplier is null)
            {
                continue;
            }

            var category = "api_preset";
            cards.Add(new ProviderCatalogCard(
                $"preset:{preset.Id}",
                Pick(preset.DisplayName, preset.Id),
                category,
                "api-compatible",
                preset.BaseUrl,
                Pick(supplier.ActiveModel, preset.DefaultModel),
                $"{Pick(preset.DisplayName, preset.Id)} API supplier is configured.",
                "preset",
                preset.Id,
                preset.Id,
                supplier.ModelCatalog.Count));
        }

        return cards
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string ProviderDescription(ProviderOptionDto provider)
    {
        var location = provider.IsLocal ? "Local runtime" : provider.RequiresNetwork ? "Network provider" : "Runtime provider";
        var model = string.IsNullOrWhiteSpace(provider.DefaultModel) ? "default model" : provider.DefaultModel;
        return $"{location}. Uses {model}.";
    }

    private bool IsProviderCatalogCardActive(ProviderCatalogCard card, string selectedProvider)
    {
        if (card.Source.Equals("preset", StringComparison.OrdinalIgnoreCase))
        {
            var supplier = _apiSuppliers.FirstOrDefault(item => item.Id.Equals(_activeApiSupplierId, StringComparison.OrdinalIgnoreCase));
            return selectedProvider.Equals("api-compatible", StringComparison.OrdinalIgnoreCase)
                && supplier is not null
                && supplier.PresetId.Equals(card.PresetId, StringComparison.OrdinalIgnoreCase);
        }

        return selectedProvider.Equals(card.RuntimeProvider, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProviderCatalogCardMatchesFilter(ProviderCatalogCard card, string filter)
    {
        var normalized = NormalizeProviderCategory(filter);
        var cardCategory = NormalizeProviderCategory(card.Category);
        return normalized is "all" or ""
            || cardCategory.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("third_party", StringComparison.OrdinalIgnoreCase) && cardCategory.Equals("api_preset", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProviderCategory(string category)
    {
        var value = (category ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal);
        return value switch
        {
            "" => "third_party",
            "api" => "third_party",
            "cn" => "cn_official",
            "cnofficial" => "cn_official",
            "thirdparty" => "third_party",
            "codingplan" => "coding_plan",
            _ => value
        };
    }

    private static string ProviderCategoryLabel(string category)
        => NormalizeProviderCategory(category) switch
        {
            "all" => "All",
            "local" => "Local",
            "api_preset" => "API Suppliers",
            "official" => "Official",
            "cn_official" => "CN Official",
            "aggregator" => "Aggregator",
            "first_party" => "First Party",
            "third_party" => "Third Party",
            "coding_plan" => "Coding Plan",
            "custom" => "Custom",
            var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.Replace("_", " ", StringComparison.Ordinal))
        };

    private static int ProviderCategoryRank(string category)
        => NormalizeProviderCategory(category) switch
        {
            "local" => 0,
            "api_preset" => 1,
            "official" => 2,
            "cn_official" => 3,
            "aggregator" => 4,
            "first_party" => 5,
            "third_party" => 6,
            "coding_plan" => 7,
            "custom" => 8,
            _ => 20
        };

    private static string NormalizeSupplierKey(string value)
        => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string SupplierIconName(string key, string name)
    {
        var normalized = NormalizeSupplierKey(key);
        if (SupplierIconByKey.TryGetValue(normalized, out var icon))
        {
            return icon;
        }

        var nameKey = NormalizeSupplierKey(name);
        return SupplierIconByKey.TryGetValue(nameKey, out icon) ? icon : string.Empty;
    }

    private static string SupplierInitials(string name, string fallback)
    {
        var parts = (name ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
        }

        if (parts.Length == 1)
        {
            return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        }

        return fallback;
    }

    private static string SupplierAvatarLabel(string key, string name)
    {
        var normalizedName = NormalizeSupplierKey(name);
        var lookup = string.IsNullOrWhiteSpace(key) ? normalizedName : key;
        if (lookup.Contains("llama", StringComparison.OrdinalIgnoreCase))
        {
            return "LL";
        }
        if (lookup.Contains("hybrid", StringComparison.OrdinalIgnoreCase))
        {
            return "HC";
        }
        if (lookup.Contains("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return "OL";
        }
        if (lookup.Contains("mock", StringComparison.OrdinalIgnoreCase))
        {
            return "MP";
        }
        if (lookup.Contains("openai", StringComparison.OrdinalIgnoreCase)
            || lookup.Contains("api", StringComparison.OrdinalIgnoreCase))
        {
            return "AI";
        }
        if (lookup.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            return "DS";
        }
        if (lookup.Contains("deepl", StringComparison.OrdinalIgnoreCase))
        {
            return "DL";
        }
        if (lookup.Contains("gemini", StringComparison.OrdinalIgnoreCase)
            || lookup.Contains("google", StringComparison.OrdinalIgnoreCase)
            || lookup.Contains("gemma", StringComparison.OrdinalIgnoreCase))
        {
            return "G";
        }
        if (lookup.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return "Q";
        }
        if (lookup.Contains("verbeam", StringComparison.OrdinalIgnoreCase))
        {
            return "V";
        }
        if (lookup.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || lookup.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return "C";
        }
        if (lookup.Contains("kimi", StringComparison.OrdinalIgnoreCase))
        {
            return "K";
        }
        if (lookup.Contains("bailian", StringComparison.OrdinalIgnoreCase))
        {
            return "BL";
        }

        return SupplierInitials(name, "AI");
    }

    private static IBrush SupplierBrush(string key, string name)
    {
        var color = SupplierColorByKey.TryGetValue(key, out var known)
            ? known
            : SupplierFallbackColors[Math.Abs((name ?? key ?? "api").GetHashCode()) % SupplierFallbackColors.Length];
        return Brush.Parse(color);
    }

    private static string ProviderGroup(ProviderOptionDto provider)
    {
        if (provider.Name.Equals("api-compatible", StringComparison.OrdinalIgnoreCase)
            || provider.Kind.Contains("api", StringComparison.OrdinalIgnoreCase)
            || provider.RequiresNetwork)
        {
            return "API";
        }

        if (provider.IsLocal
            || provider.Name.Contains("llama", StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains("ollama", StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains("hybrid", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        if (provider.Kind.Contains("aggregator", StringComparison.OrdinalIgnoreCase))
        {
            return "Aggregator";
        }

        return "Official";
    }

    private static bool ProviderMatchesFilter(ProviderOptionDto provider, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var group = ProviderGroup(provider);
        return filter.Equals("Official", StringComparison.OrdinalIgnoreCase)
            ? group is "Official" or "Aggregator"
            : group.Equals(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static int ProviderGroupRank(string group)
        => group switch
        {
            "Local" => 0,
            "Official" => 1,
            "Aggregator" => 2,
            "API" => 3,
            _ => 9
        };

    private static bool IsRecommendedModel(ModelOptionDto model)
        => model.IsDefault || model.IsRecommended || IsRealtimeRecommendedModel(model);

    private static bool IsRealtimeRecommendedModel(ModelOptionDto model)
        => model.Name.Contains("hy-mt2-1.8b-q4", StringComparison.OrdinalIgnoreCase)
            || model.DisplayName.Contains("Hy-MT2.1.8B (Q4", StringComparison.OrdinalIgnoreCase)
            || model.DisplayName.Contains("Hy-MT2 1.8B (Q4", StringComparison.OrdinalIgnoreCase);

    private void EnsureCustomApiSupplierPreset()
    {
        if (_apiSupplierPresets.Any(preset => preset.Id.Equals("custom", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _apiSupplierPresets.Insert(0, new ApiSupplierPresetDto
        {
            Id = "custom",
            DisplayName = "Custom",
            Category = "custom",
            Protocol = "openai_chat"
        });
    }

    private static string LastHealthLabel(ApiSupplierProfileDto supplier)
    {
        var health = supplier.LastHealth;
        if (health is null || string.IsNullOrWhiteSpace(health.Status) || health.Status.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "not tested";
        }

        if (health.LatencyMs.HasValue && (health.Status.Equals("ready", StringComparison.OrdinalIgnoreCase)
            || health.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase)
            || health.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{health.LatencyMs.Value} ms";
        }

        return Pick(health.Message, health.Status);
    }

    private static string ShortenMiddle(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        var left = Math.Max(4, (maxLength - 3) / 2);
        var right = Math.Max(4, maxLength - 3 - left);
        return $"{value[..left]}...{value[^right..]}";
    }

    private static SelectOption ToLanguageOption(LanguageOptionDto language)
    {
        var label = string.IsNullOrWhiteSpace(language.DisplayName)
            ? language.Code
            : $"{language.DisplayName} ({language.Code})";
        var detail = string.IsNullOrWhiteSpace(language.NativeName)
            ? language.PromptName
            : language.NativeName;
        return new SelectOption(language.Code, label, detail);
    }

    private static void SetOptions(ComboBox combo, IReadOnlyList<SelectOption> options, string selectedId)
    {
        combo.ItemsSource = options;
        if (options.Count == 0)
        {
            combo.SelectedItem = null;
            return;
        }

        combo.SelectedItem = options.FirstOrDefault(option =>
                option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? options[0];
    }

    private static List<SelectOption> ReadOptionArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(item =>
            {
                var id = GetString(item, "id", string.Empty);
                var label = GetString(item, "label", id);
                var description = GetString(item, "description", string.Empty);
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = label;
                }

                return new SelectOption(id, label, description);
            })
            .Where(option => !string.IsNullOrWhiteSpace(option.Id))
            .ToList();
    }

    private void SyncComboSelection(ComboBox combo, string id)
    {
        _suppressSettingsSync = true;
        try
        {
            SelectById(combo, id);
        }
        finally
        {
            _suppressSettingsSync = false;
        }
    }

    private void UpdateProviderSettingsSummary()
    {
        var provider = SelectedId(SettingsProviderSelect, _defaultProvider);
        var model = SelectedId(SettingsModelSelect, _defaultModel);
        SettingsProviderSummaryText.Text = string.IsNullOrWhiteSpace(model)
            ? provider
            : $"{provider} / {model}";
        SettingsProviderDetailText.Text = $"Current default route: {SelectedId(SourceLanguageSelect, _defaultSource)} -> {SelectedId(TargetLanguageSelect, _defaultTarget)}";
        SettingsRecommendedText.Text = string.IsNullOrWhiteSpace(model)
            ? "Choose a model from the provider list."
            : $"Using {model}";
    }

    private static string ComboValue(ComboBox combo, string fallback)
    {
        return combo.SelectedItem switch
        {
            SelectOption option when !string.IsNullOrWhiteSpace(option.Id) => option.Id,
            ComboBoxItem item when item.Content is not null => item.Content.ToString() ?? fallback,
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => fallback
        };
    }

    private static void SelectById(ComboBox combo, string id)
    {
        combo.SelectedItem = GetOptions(combo).FirstOrDefault(option =>
                option.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? combo.SelectedItem;
    }

    private static void SelectComboContent(ComboBox combo, string content)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem comboItem
                && string.Equals(comboItem.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = comboItem;
                return;
            }
        }
    }

    private static string SelectedId(ComboBox combo, string fallback)
    {
        return combo.SelectedItem is SelectOption option && !string.IsNullOrWhiteSpace(option.Id)
            ? option.Id
            : fallback;
    }

    private static string SelectedLabel(ComboBox combo, string fallback)
    {
        return combo.SelectedItem is SelectOption option && !string.IsNullOrWhiteSpace(option.Label)
            ? option.Label
            : fallback;
    }

    private static IReadOnlyList<SelectOption> GetOptions(ComboBox combo)
    {
        return combo.ItemsSource is IEnumerable<SelectOption> options
            ? options.ToArray()
            : [];
    }

    private static string NormalizeBaseUrl(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:5768"
            : value.Trim();
        return normalized.TrimEnd('/');
    }

    private static string CleanOrDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string Pick(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ParseIntOrDefault(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;

    private static double ParseDoubleOrDefault(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;

    private static string OneLine(string? value, int maxLength)
    {
        var normalized = Pick(value, string.Empty)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static string ShortId(string? value)
    {
        var id = Pick(value, string.Empty);
        return id.Length <= 8 ? id : id[..8];
    }

    private static string DisplayMemoryKind(string? value)
        => Pick(value, "memory").Replace('_', ' ') switch
        {
            "term" => "Term",
            "ocr correction" => "OCR correction",
            "style" => "Style",
            "translation" => "Translation example",
            "scene summary" => "Scene summary",
            var text => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text)
        };

    private static string DisplayTrust(string? value)
        => Pick(value, "unknown").Replace('_', ' ') switch
        {
            "user verified" => "approved",
            "trusted import" => "trusted",
            "local generated" => "candidate",
            "untrusted import" => "imported",
            "quarantined" => "quarantined",
            var text => text
        };

    private static string FormatTextStats(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "0 chars / 0 lines";
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Count(ch => ch == '\n') + 1;
        return $"{text.Length} chars / {lines} lines";
    }

    private sealed record ProviderOptionDto(
        string Name,
        string DisplayName,
        string Kind,
        string DefaultModel,
        bool RequiresNetwork,
        bool IsLocal);

    private sealed record LanguageOptionDto(
        string Code,
        string DisplayName,
        string NativeName,
        string PromptName,
        bool IsDefaultSource,
        bool IsDefaultTarget,
        bool IsOcrSupported,
        bool IsSpeechSupported);

    private sealed record ModelOptionDto(
        string Provider,
        string Name,
        string DisplayName,
        bool IsDefault,
        bool IsInstalled,
        string Source,
        bool IsRecommended = false,
        string RecommendationReason = "",
        string RecommendedUse = "",
        string SupplierId = "",
        string SupplierName = "");

    private sealed record ProviderCatalogCard(
        string Id,
        string Name,
        string Category,
        string RuntimeProvider,
        string Endpoint,
        string Model,
        string Description,
        string Source,
        string IconKey,
        string PresetId,
        int ModelCount);

    private sealed record ApiSupplierPresetCatalogDto
    {
        public IReadOnlyList<ApiSupplierPresetDto> Presets { get; init; } = [];
    }

    private sealed record ApiSupplierPresetDto
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Category { get; init; } = "custom";
        public string Protocol { get; init; } = "openai_chat";
        public string BaseUrl { get; init; } = string.Empty;
        public string ModelsUrl { get; init; } = string.Empty;
        public bool RequiresApiKey { get; init; } = true;
        public bool SupportsBalance { get; init; }
        public string DefaultModel { get; init; } = string.Empty;
        public IReadOnlyList<ApiSupplierRecommendedModelDto> RecommendedModels { get; init; } = [];

        public int RecommendedModelsCount => RecommendedModels.Count;
    }

    private sealed record ApiSupplierRecommendedModelDto
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }

    private sealed record ApiSupplierProfileDto
    {
        public string Id { get; init; } = string.Empty;
        public string PresetId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Protocol { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
        public string ModelsUrl { get; init; } = string.Empty;
        public bool HasApiKey { get; init; }
        public string ActiveModel { get; init; } = string.Empty;
        public IReadOnlyList<ApiSupplierFetchedModelDto> ModelCatalog { get; init; } = [];
        public ApiSupplierHealthDto? LastHealth { get; init; }
        public bool SupportsBalance { get; init; }
        public string BalanceTemplate { get; init; } = string.Empty;
        public string BalanceUrl { get; init; } = string.Empty;
        public int BalanceAutoIntervalMinutes { get; init; }
        public ApiSupplierBalanceDto? LastBalance { get; init; }
    }

    private sealed record ApiSupplierFetchedModelDto
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string OwnedBy { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
    }

    private sealed record ApiSupplierHealthDto
    {
        public string Status { get; init; } = "unknown";
        public long? LatencyMs { get; init; }
        public DateTimeOffset? CheckedAt { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    private sealed record ApiSupplierBalanceDto
    {
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<ApiSupplierBalancePlanDto> Plans { get; init; } = [];
        public DateTimeOffset? CheckedAt { get; init; }
    }

    private sealed record ApiSupplierBalancePlanDto
    {
        public string Name { get; init; } = string.Empty;
        public double? Total { get; init; }
        public double? Used { get; init; }
        public double? Remaining { get; init; }
        public string Unit { get; init; } = string.Empty;
    }

    private sealed record ApiSupplierUpsertRequestDto
    {
        public string PresetId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
        public string ModelsUrl { get; init; } = string.Empty;
        public string ApiKey { get; init; } = string.Empty;
        public string ActiveModel { get; init; } = string.Empty;
        public string BalanceTemplate { get; init; } = string.Empty;
        public string BalanceUrl { get; init; } = string.Empty;
        public int BalanceAutoIntervalMinutes { get; init; }
    }

    private sealed record ApiSupplierTestResultDto
    {
        public string SupplierId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public long? LatencyMs { get; init; }
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<ApiSupplierFetchedModelDto> Models { get; init; } = [];
    }

    private sealed record ApiSupplierModelFetchResultDto
    {
        public string SupplierId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<ApiSupplierFetchedModelDto> Models { get; init; } = [];
    }

    private sealed record TranslationRouteDto
    {
        public string ProfileId { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public string SupplierId { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
    }

    private sealed record MemoryRuntimeScopeDto
    {
        public string Profile { get; init; } = "default";
        public string DatabaseScope { get; init; } = string.Empty;
        public int MemoryItemCount { get; init; }
        public int PendingReviewCount { get; init; }
        public int FailedJobsCount { get; init; }
    }

    private sealed record MemoryItemDto
    {
        public string Id { get; init; } = string.Empty;
        public string ProfileId { get; init; } = string.Empty;
        public string MemoryKind { get; init; } = string.Empty;
        public string SourceLanguage { get; init; } = string.Empty;
        public string TargetLanguage { get; init; } = string.Empty;
        public string SourceText { get; init; } = string.Empty;
        public string TargetText { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
        public int Priority { get; init; }
        public double Confidence { get; init; }
        public string TrustLevel { get; init; } = string.Empty;
        public string SecurityFlagsJson { get; init; } = string.Empty;
        public string Classification { get; init; } = string.Empty;
        public string Visibility { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public int UseCount { get; init; }
    }

    private sealed record MemoryUpsertRequestDto
    {
        public string Profile { get; init; } = string.Empty;
        public string MemoryKind { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Target { get; init; } = string.Empty;
        public string SourceText { get; init; } = string.Empty;
        public string TargetText { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
        public int Priority { get; init; }
        public double Confidence { get; init; }
        public string Origin { get; init; } = string.Empty;
        public string TrustLevel { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public string ApprovedBy { get; init; } = string.Empty;
        public string Visibility { get; init; } = string.Empty;
    }

    private sealed record MemoryUpdateRequestDto
    {
        public string? SourceText { get; init; }
        public string? TargetText { get; init; }
        public string? Note { get; init; }
        public int? Priority { get; init; }
        public double? Confidence { get; init; }
        public string? TrustLevel { get; init; }
        public string? ApprovedBy { get; init; }
        public string? Visibility { get; init; }
        public bool? IsActive { get; init; }
        public bool? AcknowledgeSecurityFlags { get; init; }
    }

    private sealed record MemoryReviewRequestDto
    {
        public string Action { get; init; } = string.Empty;
        public string ReviewedBy { get; init; } = string.Empty;
        public bool? AcknowledgeSecurityFlags { get; init; }
    }

    private sealed record EngineOptionDto(
        string Name,
        string DisplayName,
        string Kind,
        string DefaultLanguage,
        bool IsAvailable,
        bool IsDefault,
        bool RequiresExternalProcess,
        bool IsLocal,
        string Source,
        string Status = "",
        bool RequiresApiConfiguration = false,
        string Note = "");

    private sealed record NativeDocumentJobStatus
    {
        public string Id { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string ProfileId { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string SourceKind { get; init; } = string.Empty;
        public string InputFileName { get; init; } = string.Empty;
        public string InputMimeType { get; init; } = string.Empty;
        public string InputHash { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public int? TotalUnits { get; init; }
        public int CompletedUnits { get; init; }
        public double Progress { get; init; }
        public int ArtifactCount { get; init; }
        public int WarningCount { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public IReadOnlyList<NativeDocumentArtifact> Artifacts { get; init; } = [];

        public override string ToString()
        {
            var file = string.IsNullOrWhiteSpace(InputFileName) ? "document" : InputFileName;
            var percent = Math.Clamp(Progress * 100d, 0, 100);
            return $"{file}  ·  {Status}  ·  {percent:0}%";
        }
    }

    private sealed record NativeDocumentArtifact
    {
        public string Id { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public override string ToString()
        {
            var kind = string.IsNullOrWhiteSpace(Kind) ? "artifact" : Kind;
            var file = string.IsNullOrWhiteSpace(FileName) ? Id : FileName;
            return $"{kind} / {file} / {FormatBytes(SizeBytes)}";
        }
    }

    private sealed record SelectOption(string Id, string Label, string Detail = "")
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private sealed record NativeTranslateRequest
    {
        public string? Text { get; init; }
        public string? Source { get; init; }
        public string? Target { get; init; }
        public string? Mode { get; init; }
        public string? Surface { get; init; }
        public string? Provider { get; init; }
        public string? Model { get; init; }
        public long? ClientRequestStartedAtUnixMs { get; init; }
    }

    private sealed record NativeTranslateResponse(
        string Result,
        string ErrorCode,
        string ErrorMessage,
        NativeTokenUsage? TokenUsage = null);

    private sealed record NativeTokenUsage(
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        string Source,
        bool IsEstimated);

    private sealed record NativeOcrTranslateRequest
    {
        public string? ImageBase64 { get; init; }
        public string? ImageMimeType { get; init; }
        public string? OcrProvider { get; init; }
        public string? ContentType { get; init; }
        public string? Preference { get; init; }
        public string? Language { get; init; }
        public string? Profile { get; init; }
        public string? PreprocessingPreset { get; init; }
        public string? TranslationProvider { get; init; }
        public string? Model { get; init; }
        public string? Source { get; init; }
        public string? Target { get; init; }
        public string? Mode { get; init; }
        public string? Surface { get; init; }
    }

    private sealed record NativeOcrTranslateResponse
    {
        public NativeOcrResponse? Ocr { get; init; }
        public NativeTranslateResponse? Translation { get; init; }
        public NativeOcrStructuredTranslation? Structured { get; init; }
    }

    private sealed record NativeOcrResponse
    {
        public string Text { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public string Engine { get; init; } = string.Empty;
        public string Language { get; init; } = string.Empty;
        public long LatencyMs { get; init; }
    }

    private sealed record NativeOcrStructuredTranslation
    {
        public string Text { get; init; } = string.Empty;
        public string Engine { get; init; } = string.Empty;
        public long LatencyMs { get; init; }
        public bool CacheHit { get; init; }
        public NativeTokenUsage? TokenUsage { get; init; }
    }

    private sealed record NativeSpeechRequest
    {
        public string? AudioBase64 { get; init; }
        public string? AudioMimeType { get; init; }
        public string? SourceUrl { get; init; }
        public string? Provider { get; init; }
        public string? Language { get; init; }
        public string? Profile { get; init; }
        public string? SessionId { get; init; }
        public string? Glossary { get; init; }
        public bool PreferCaptions { get; init; } = true;
    }

    private sealed record NativeSpeechTranslateRequest
    {
        public string? AudioBase64 { get; init; }
        public string? AudioMimeType { get; init; }
        public string? SourceUrl { get; init; }
        public string? SpeechProvider { get; init; }
        public string? Language { get; init; }
        public string? Profile { get; init; }
        public string? Source { get; init; }
        public string? Target { get; init; }
        public string? Mode { get; init; }
        public string? TranslationProvider { get; init; }
        public string? Model { get; init; }
        public string? Glossary { get; init; }
    }

    private sealed record NativeSpeechResponse
    {
        public string EventId { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public IReadOnlyList<NativeSpeechSegment> Segments { get; init; } = [];
        public string Provider { get; init; } = string.Empty;
        public string Engine { get; init; } = string.Empty;
        public string Language { get; init; } = string.Empty;
        public string SourceKind { get; init; } = string.Empty;
        public string SourceUri { get; init; } = string.Empty;
        public string AudioMimeType { get; init; } = string.Empty;
        public bool CaptionsUsed { get; init; }
        public long LatencyMs { get; init; }
    }

    private sealed record NativeSpeechSegment
    {
        public int Index { get; init; }
        public double StartSeconds { get; init; }
        public double EndSeconds { get; init; }
        public string Text { get; init; } = string.Empty;
        public double Confidence { get; init; }
        public string? Speaker { get; init; }
        public string? Language { get; init; }
    }

    private sealed record NativeSpeechTranslateResponse
    {
        public NativeSpeechResponse? Speech { get; init; }
        public IReadOnlyList<NativeSpeechTranslatedSegment> Translations { get; init; } = [];
        public NativeTokenUsage? TokenUsage { get; init; }
    }

    private sealed record NativeSpeechTranslatedSegment
    {
        public int Index { get; init; }
        public double StartSeconds { get; init; }
        public double EndSeconds { get; init; }
        public string SourceText { get; init; } = string.Empty;
        public string TranslatedText { get; init; } = string.Empty;
        public string ErrorCode { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public string Engine { get; init; } = string.Empty;
        public long LatencyMs { get; init; }
        public bool CacheHit { get; init; }
        public NativeTokenUsage? TokenUsage { get; init; }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static (string Base64, string Mime) ParseDataOrBase64(string value, string fallbackMime)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma > 0)
            {
                var meta = value[5..comma];
                var semi = meta.IndexOf(';');
                var mime = semi >= 0 ? meta[..semi] : meta;
                return (value[(comma + 1)..], string.IsNullOrWhiteSpace(mime) ? fallbackMime : mime);
            }
        }

        return (value, fallbackMime);
    }

    private static string MimeFromPath(string path, string fallback)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".html" or ".htm" => "text/html",
            ".md" or ".markdown" => "text/markdown",
            ".txt" => "text/plain",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".ogg" or ".opus" => "audio/ogg",
            ".flac" => "audio/flac",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => fallback
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.#} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024d * 1024):0.#} MB";
        }

        return bytes >= 1024L
            ? $"{bytes / 1024d:0.#} KB"
            : $"{bytes} B";
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                var message = GetString(json.RootElement, "errorMessage", string.Empty);
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = GetString(json.RootElement, "error", string.Empty);
                }
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = GetString(json.RootElement, "detail", string.Empty);
                }
                if (!string.IsNullOrWhiteSpace(message))
                {
                    throw new InvalidOperationException(message);
                }
            }
            catch (JsonException)
            {
                // Fall through and surface the raw body below.
            }
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(text)
            ? response.ReasonPhrase ?? response.StatusCode.ToString()
            : text);
    }

    private static string FormatMemory(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return value == 0 ? "0 MB" : "-";
        }

        return value >= 1024
            ? $"{value / 1024d:0.#} GB"
            : $"{value:0} MB";
    }

    private static string GetString(JsonElement root, string property, string fallback)
    {
        return root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static string GetNestedString(JsonElement root, string[] path, string fallback)
    {
        return TryGetNested(root, path, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static double GetNestedDouble(JsonElement root, string[] path)
    {
        if (!TryGetNested(root, path, out var value))
        {
            return double.NaN;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => double.NaN
        };
    }

    private static bool GetNestedBool(JsonElement root, string[] path)
    {
        return TryGetNested(root, path, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static bool TryGetNested(JsonElement root, string[] path, out JsonElement value)
    {
        value = root;
        foreach (var property in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out value))
            {
                return false;
            }
        }

        return true;
    }
}
