using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SlangGraph.Core;

namespace LoLTranslator.App;

public partial class MainWindow : Window
{
    private static readonly Regex SystemActorPrefix = new(
        @"^\s*(?:[0-9OoIl]{1,2}\s*[:：.]\s*[0-9OoIl]{2}\s*)?(?<identity>[\p{L}\p{N}_.'’\- ]{1,72}\s*[\(（][^\)）\r\n]{1,32}[\)）])\s*(?=(?:has\s*targeted|signals?\b|is\s+(?:on\s+the\s+way|asking\s+for)|has\s+(?:disconnected|reconnected|slain|shut\s+down|drawn\s+first\s+blood)|purchased\b|used\b|destroyed\b|님이\b))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private bool _isRunning;
    private Rect? _selectedRegion;
    private string? _lastCapturePath;
    private OcrShadowSemanticService? _shadowSemanticService;
    private ShadowRuntimeFeedbackDraft? _lastShadowFeedbackDraft;
    private ChampionOfficialRc2RealScreenshotShadowResult? _lastChampionRc2ShadowResult;
    private IncrementalGeneralChatShadowService? _incrementalGeneralShadowService;
    private IncrementalGeneralChatShadowResult? _lastIncrementalGeneralShadowResult;
    private readonly DispatcherTimer _continuousTimer;
    private readonly ContinuousTranslationSessionCoordinator _continuousCoordinator = new();
    private readonly VisualChatChangeWatcher _visualChangeWatcher = new(
        TimeSpan.FromMilliseconds(200),
        differenceThreshold: 28,
        maximumWait: TimeSpan.FromMilliseconds(900));
    private readonly RuntimeChatEventLedger _runtimeEventLedger = new();
    private RuntimeTranslationDispatcher? _runtimeDispatcher;
    private readonly TranslationCoverageTracker _coverageTracker = new();
    private readonly ContinuousRuntimeTrace _runtimeTrace = new();
    private readonly OcrTextDiagnosticTrace _ocrTextDiagnosticTrace = new();
    private readonly RuntimeInputProvenanceTrace _inputProvenanceTrace = new();
    private readonly PlayerFeedbackService _playerFeedbackService =
        PlayerFeedbackService.LoadFromApplicationDirectory(AppContext.BaseDirectory);
    private Task? _continuousOcrTask;
    private Task? _continuousStartTask;
    private CancellationTokenSource _continuousSessionCancellation = new();
    private long _continuousSessionId;
    private long _continuousFrameId;
    private readonly Dictionary<int, (string Hash, OcrBoundingBox Bounds)> _coordinateVisualRows = new();
    private readonly Dictionary<int, RuntimeInputEvidenceRow> _coordinateInputEvidenceRows = new();
    private readonly Dictionary<string, RuntimeInputCorrelation> _inputCorrelations = new(StringComparer.Ordinal);
    private readonly SourceIdentityColorMemory _sourceIdentityColorMemory = new();
    private readonly Queue<CapturedOcrFrame> _pendingOcrFramePaths = new();
    // A viewport is an observation of the current chat history, not queued
    // translation work. OCR only the latest stable viewport; the event ledger
    // extracts append-only messages and prevents old screenshots from replaying.
    private const int MaximumBufferedOcrFrames = 1;
    // The optional Korean recognizer is currently excluded from the realtime
    // profile. Running a second GPU OCR model made one frame take up to ten
    // seconds and caused the visual-frame queue to grow while playing video.
    private const bool EnableKoreanOcrFallback = false;
    private readonly IHighAccuracyChatOcrProvider _highAccuracyOcrProvider = new NativePpOcrV5SidecarProvider();
    private readonly NativePpOcrV5SidecarProvider? _koreanOcrProvider =
        EnableKoreanOcrFallback ? CreateKoreanOcrProvider() : null;
    private Task<HighAccuracyOcrCapability>? _koreanOcrProbeTask;
    private TranslationSidecarWindow? _translationSidecar;
    private bool _sidecarAdjusting;
    private Task<LocalModelStatus>? _localModelTask;
    private LocalModelStatus _localModelStatus = new("checking", "正在检查本地 Qwen3……", false);
    private DateTimeOffset? _localModelEnsureStartedAt;
    private DateTimeOffset _nextLocalModelCheckAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextOcrAttemptAt = DateTimeOffset.MinValue;
    private bool _pendingOcrAfterBusy;
    private bool _startupBaselinePending;
    private DateTimeOffset? _startupEmptyObservedAt;
    private DateTimeOffset? _lastContinuousOcrAt;
    private string? _lastTracedVisualStatus;
    private string? _lastTracedModelState;
    private bool _savedRegionStartupHandled;

    public MainWindow()
    {
        InitializeComponent();
        Title = "LoL Translator · " + RuntimeBuildInfo.Label;
        SystemLanguageText.Text =
            $"{CultureInfo.CurrentUICulture.DisplayName} ({CultureInfo.CurrentUICulture.Name})";
        LoadAvailableOcrLanguages();
        LoadShadowContexts();
        LoadShadowSemanticService();
        _continuousTimer = new DispatcherTimer();
        _continuousTimer.Tick += ContinuousTimer_Tick;
        RefreshContinuousInterval();
        RestoreRegionButton.IsEnabled = ContinuousTranslationRegionStore.TryLoadCompatible(
            SystemParameters.WorkArea, 1.0, out _);
        TracePathText.Text = _runtimeTrace.FilePath;
        OcrDiagnosticPathText.Text = _ocrTextDiagnosticTrace.FilePath;
        OcrTextDiagnosticCheckBox.Checked += OcrTextDiagnosticCheckBox_Checked;
        OcrTextDiagnosticCheckBox.Unchecked += OcrTextDiagnosticCheckBox_Unchecked;
        Trace("application", "window_initialized", "ready");
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_savedRegionStartupHandled) return;
        _savedRegionStartupHandled = true;
        _ = FlushPendingPlayerFeedbackAsync();

        if (!ContinuousTranslationRegionStore.TryLoadCompatible(
                SystemParameters.WorkArea,
                1.0,
                out Rect region))
        {
            StatusText.Text = "请选择聊天区域";
            StatusText.Foreground = System.Windows.Media.Brushes.Gold;
            SelectedRegionText.Text = "首次使用时，只需选择一次聊天区域";
            return;
        }

        _selectedRegion = region;
        SelectedRegionText.Text = "已使用上次聊天区域";
        SelectRegionButton.Content = "更换聊天区域";
        RestoreRegionButton.IsEnabled = true;
        TestCaptureButton.IsEnabled = true;
        IncrementalGeneralShadowCheckBox.IsChecked = true;
        UpdateIncrementalGeneralSessionState();
        Trace("region", "saved_region_auto_restored", "accepted");
        StartContinuousTranslation();
    }

    private async Task FlushPendingPlayerFeedbackAsync()
    {
        try
        {
            await _playerFeedbackService.FlushPendingAsync();
        }
        catch
        {
            // Feedback transport must never interrupt OCR or translation.
        }
    }

    private void LoadShadowContexts()
    {
        SemanticLocaleComboBox.ItemsSource = new[]
        {
            new ShadowSemanticLocaleOption("自动（正式 Pack 全部语言）", OcrShadowSemanticService.AutoSemanticLocale),
            new ShadowSemanticLocaleOption("English", "en-US"),
            new ShadowSemanticLocaleOption("Français", "fr-FR"),
            new ShadowSemanticLocaleOption("简体中文", "zh-CN"),
            new ShadowSemanticLocaleOption("한국어", "ko-KR")
        };
        SemanticLocaleComboBox.SelectedIndex = 0;

        ShadowContextComboBox.ItemsSource = new[]
        {
            new ShadowMatchContextOption(
                "LoL 英文局内聊天（care + no F）",
                "lol.global",
                "in_game_chat",
                new[] { "lol.global", "lol.eu_or_global" }),
            new ShadowMatchContextOption("LoL 英语/EU 局内聊天（no F）", "lol.eu_or_global", "in_game_chat"),
            new ShadowMatchContextOption("LoL 法语公开社区（no F）", "lol.fr", "community_reference"),
            new ShadowMatchContextOption("非 LoL 边界测试", "non_lol", "other"),
            new ShadowMatchContextOption("无上下文边界测试", null, null)
        };
        ShadowContextComboBox.SelectedIndex = 0;

        FeedbackKindComboBox.ItemsSource = new[]
        {
            new ShadowRuntimeFeedbackKindOption("未识别文本", "unrecognized_text"),
            new ShadowRuntimeFeedbackKindOption("误匹配", "false_positive"),
            new ShadowRuntimeFeedbackKindOption("slot 缺失", "slot_missing"),
            new ShadowRuntimeFeedbackKindOption("上下文缺失", "context_missing"),
            new ShadowRuntimeFeedbackKindOption("OCR 表面变体", "ocr_surface_variant"),
            new ShadowRuntimeFeedbackKindOption("融合歧义", "fusion_ambiguous"),
            new ShadowRuntimeFeedbackKindOption("符合预期", "expected_behavior")
        };
        FeedbackKindComboBox.SelectedIndex = 0;

        FeedbackOriginComboBox.ItemsSource = new[]
        {
            new ShadowRuntimeFeedbackOriginOption("未知来源（不可保存）", "unknown"),
            new ShadowRuntimeFeedbackOriginOption("合成 fixture", "synthetic_fixture"),
            new ShadowRuntimeFeedbackOriginOption("受控测试", "controlled_test"),
            new ShadowRuntimeFeedbackOriginOption("真实来源（待脱敏门）", "real_source")
        };
        FeedbackOriginComboBox.SelectedIndex = 0;
    }

    private void LoadShadowSemanticService()
    {
        try
        {
            _shadowSemanticService =
                OcrShadowSemanticService.LoadFromApplicationDirectory(AppContext.BaseDirectory);
            PackStatusText.Text =
                $"已加载语义 {OcrShadowSemanticService.ExpectedPackVersion} + " +
                $"Rendering {OcrShadowSemanticService.ExpectedRenderingPackVersion}，SHA-256 已验证";
            PackStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception exception)
        {
            _shadowSemanticService = null;
            PackStatusText.Text = $"正式 Pack 加载失败：{exception.Message}";
            PackStatusText.Foreground = System.Windows.Media.Brushes.LightCoral;
        }
    }

    private void LoadAvailableOcrLanguages()
    {
        var languages =
            OcrService.GetAvailableLanguages();

        SourceLanguageComboBox.ItemsSource =
            languages;

        SourceLanguageComboBox.DisplayMemberPath =
            nameof(OcrLanguageOption.DisplayName);

        // 优先选择简体中文。
        OcrLanguageOption? preferredLanguage =
            languages.FirstOrDefault(language =>
                language.LanguageTag.Equals(OcrService.MultiLanguageTag, StringComparison.OrdinalIgnoreCase));

        preferredLanguage ??=
            languages.FirstOrDefault(
                language =>
                    language.LanguageTag.StartsWith(
                        "zh-Hans",
                        StringComparison.OrdinalIgnoreCase) ||
                    language.LanguageTag.Equals(
                        "zh-CN",
                        StringComparison.OrdinalIgnoreCase));

        // 没有中文时，优先选择英语。
        preferredLanguage ??=
            languages.FirstOrDefault(
                language =>
                    language.LanguageTag.Equals(
                        "en-US",
                        StringComparison.OrdinalIgnoreCase) ||
                    language.LanguageTag.StartsWith(
                        "en-",
                        StringComparison.OrdinalIgnoreCase));

        // 再没有时，选择第一个选项。
        SourceLanguageComboBox.SelectedItem =
            preferredLanguage ??
            languages.FirstOrDefault();
    }

    private void SelectRegionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        bool resumeContinuousAfterCancelledSelection = _continuousCoordinator.State == ContinuousTranslationState.Running;
        int cachedBodyCount = _incrementalGeneralShadowService?.CachedBodyCount ?? 0;
        if (IncrementalGeneralChatSessionResetPolicy.RequiresRegionReplacementConfirmation(cachedBodyCount))
        {
            MessageBoxResult confirmation = MessageBox.Show(
                $"更换聊天区域会清空本次已处理的 {cachedBodyCount} 条聊天去重记录。\n\n" +
                "连续测试同一聊天区域时，请使用“重新截取同一区域”，不要重新选择区域。\n\n" +
                "仍要更换区域吗？",
                "更换聊天区域",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (!IncrementalGeneralChatSessionResetPolicy.ShouldApplyRegionReplacement(
                    cachedBodyCount,
                    confirmation == MessageBoxResult.Yes))
            {
                IncrementalGeneralSessionStateText.Text =
                    $"当前区域已保持；本会话已处理 {cachedBodyCount} 条正文。连续测试请点击“重新截取同一区域”。";
                return;
            }
        }

        _continuousTimer.Stop();
        _translationSidecar?.Hide();
        Hide();

        var selectionOverlay =
            new SelectionOverlay();

        bool? result =
            selectionOverlay.ShowDialog();

        Show();
        Activate();

        if (result == true &&
            selectionOverlay.SelectedRegion
                is Rect region)
        {
            _selectedRegion = region;
            Trace("region", "region_selected", "accepted");
            _lastCapturePath = null;
            _continuousCoordinator.ResetForRegionChange();
            _continuousSessionCancellation.Cancel();
            _continuousSessionCancellation.Dispose();
            _continuousSessionCancellation = new CancellationTokenSource();
            _continuousSessionId++;
            _visualChangeWatcher.Reset();
            _runtimeEventLedger.Reset();
            ResetTranslationDispatcher();
            _coverageTracker.Reset();
            _sourceIdentityColorMemory.Reset();
            UpdateCoverageStatus();
            _pendingOcrAfterBusy = false;
            _startupBaselinePending = false;
            _incrementalGeneralShadowService?.ResetSession();
            _incrementalGeneralShadowService = null;
            ResetShadowFeedback();

            SelectedRegionText.Text = "聊天区域已保存（下次启动自动使用）";

            TestCaptureButton.IsEnabled = true;
            TestCaptureButton.Content = "截取当前区域";
            RecognizeButton.IsEnabled = false;

            CapturePreviewImage.Source = null;

            PreviewPlaceholder.Visibility =
                Visibility.Visible;

            LastCaptureText.Text =
                "尚未进行测试截图";

            OcrStatusText.Text =
                "请截取当前区域，然后识别截图文字。连续测试不要重新选择区域。";

            OcrResultTextBox.Text =
                "识别结果会显示在这里。";

            ShadowResultTextBox.Text =
                "识别截图后，这里会显示正式 Pack 的 shadow 匹配结果。";

            UpdateIncrementalGeneralSessionState();
            ContinuousTranslationRegionStore.Save(region, SystemParameters.WorkArea, 1.0);
            RestoreRegionButton.IsEnabled = true;
            SelectRegionButton.Content = "更换聊天区域";
            IncrementalGeneralShadowCheckBox.IsChecked = true;
            StartContinuousTranslation();
        }
        else if (resumeContinuousAfterCancelledSelection)
        {
            StartContinuousTranslation();
        }
    }

    private async void TestCaptureButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedRegion is not Rect region)
        {
            MessageBox.Show(
                "请先选择聊天框区域。",
                "尚未选择区域",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        string? capturePath = null;

        try
        {
            // 隐藏程序，避免主窗口出现在截图中。
            Hide();

            await Task.Delay(250);

            capturePath =
                ScreenCaptureService.Capture(region);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"截图失败：{exception.Message}",
                "截图错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Show();
            Activate();
        }

        if (capturePath is null)
        {
            return;
        }

        _lastCapturePath = capturePath;
        ResetShadowFeedback();
        TestCaptureButton.Content = "重新截取同一区域";

        CapturePreviewImage.Source =
            LoadBitmapImage(capturePath);

        PreviewPlaceholder.Visibility =
            Visibility.Collapsed;

        LastCaptureText.Text =
            $"已保存：{capturePath}";

        OcrStatusText.Text =
            "截图已准备，请点击“识别截图文字”。连续测试时继续使用“重新截取同一区域”。";

        RecognizeButton.IsEnabled = true;

        OcrResultTextBox.Text =
            "点击“识别截图文字”开始 OCR。";

        ShadowResultTextBox.Text =
            "等待 OCR 结果。";
    }

    private async void RecognizeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(
                _lastCapturePath))
        {
            MessageBox.Show(
                "请先进行一次测试截图。",
                "没有截图",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        try
        {
            RecognizeButton.IsEnabled = false;
            OcrStatusText.Text = "正在识别……";
            OcrResultTextBox.Text = string.Empty;

            if (SourceLanguageComboBox.SelectedItem
                    is not OcrLanguageOption selectedLanguage)
            {
                throw new InvalidOperationException(
                    "请选择 OCR 来源语言。");
            }

            IReadOnlyList<OcrRecognitionResult> recognitionCandidates =
                await OcrService.RecognizeCandidatesAsync(
                    _lastCapturePath,
                    selectedLanguage.LanguageTag);

            OcrRecognitionResult[] nonEmptyCandidates = recognitionCandidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text))
                .ToArray();

            if (nonEmptyCandidates.Length == 0)
            {
                OcrResultTextBox.Text =
                    "没有识别到文字。";

                OcrStatusText.Text =
                    "识别完成，但结果为空";
            }
            else
            {
                OcrResultTextBox.Text =
                    string.Join(
                        Environment.NewLine + Environment.NewLine,
                        nonEmptyCandidates.Select(candidate =>
                            $"[{candidate.RecognizerLanguageTag}]" + Environment.NewLine + candidate.Text));

                OcrStatusText.Text =
                    $"识别完成（{nonEmptyCandidates.Length} 个语言候选）";

                await RunShadowMatchAsync(nonEmptyCandidates);
            }
        }
        catch (Exception exception)
        {
            OcrStatusText.Text =
                "识别失败";

            MessageBox.Show(
                $"OCR 识别失败：{exception.Message}",
                "OCR 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RecognizeButton.IsEnabled =
                !_isRunning &&
                !string.IsNullOrWhiteSpace(
                    _lastCapturePath);
        }
    }

    private async Task RunShadowMatchAsync(IReadOnlyList<OcrRecognitionResult> recognitionCandidates, IReadOnlyList<OcrShadowLineResult>? fusedOverride = null, IReadOnlyDictionary<int, string>? occurrenceKeys = null)
    {
        if (_shadowSemanticService is null)
        {
            ShadowResultTextBox.Text =
                "正式 0.3.0 Pack 未成功加载，无法进行 shadow 匹配。";
            return;
        }

        if (ShadowContextComboBox.SelectedItem is not ShadowMatchContextOption context)
        {
            ShadowResultTextBox.Text = "请选择 Shadow 匹配上下文。";
            return;
        }

        if (SemanticLocaleComboBox.SelectedItem is not ShadowSemanticLocaleOption semanticLocale)
        {
            ShadowResultTextBox.Text = "请选择语义语言。";
            return;
        }

        IReadOnlyList<OcrShadowLineResult> fusedResults = fusedOverride ??
            _shadowSemanticService.MatchCandidates(recognitionCandidates, semanticLocale.LanguageTag, context);

        string formalDisplay = _shadowSemanticService.FormatDisplayText(
            fusedResults,
            "多 OCR 逐消息融合",
            semanticLocale.LanguageTag,
            context,
            CultureInfo.CurrentUICulture.Name);
        ShadowResultTextBox.Text = formalDisplay;

        _lastShadowFeedbackDraft = new ShadowRuntimeFeedbackDraft(
            Path.GetFileName(_lastCapturePath) ?? "capture-unknown",
            recognitionCandidates,
            semanticLocale.LanguageTag,
            CultureInfo.CurrentUICulture.Name,
            context,
            fusedResults);
        SaveFeedbackButton.IsEnabled = true;
        int ambiguityCount = fusedResults.Count(line => line.Fusion.IsAmbiguous);

        if (ambiguityCount > 0)
        {
            FeedbackKindComboBox.SelectedItem =
                ((IEnumerable<ShadowRuntimeFeedbackKindOption>)FeedbackKindComboBox.ItemsSource)
                .First(option => option.Value == "fusion_ambiguous");
            FeedbackStatusText.Text =
                $"检测到 {ambiguityCount} 条融合歧义；可保存为本地待脱敏反馈，不会修改正式 Pack。";
        }
        else
        {
            FeedbackStatusText.Text =
                "可按需保存本地待脱敏反馈；它不会修改正式 Pack。";
        }

        _lastChampionRc2ShadowResult = null;
        SaveChampionRc2ShadowLogButton.IsEnabled = false;
        _lastIncrementalGeneralShadowResult = null;
        SaveIncrementalGeneralShadowLogButton.IsEnabled = false;
        if (IncrementalGeneralShadowCheckBox.IsChecked == true)
        {
            IncrementalGeneralShadowStatusText.Text =
                "正在运行本机增量通用聊天 Qwen3 Shadow……";
            await Task.Yield();
            try
            {
                _incrementalGeneralShadowService ??=
                    IncrementalGeneralChatShadowService.CreateExplicit(_shadowSemanticService);
                _lastIncrementalGeneralShadowResult = _incrementalGeneralShadowService.Process(
                    fusedResults,
                    context,
                    CultureInfo.CurrentUICulture.Name,
                    occurrenceKeys);
                ShadowResultTextBox.Text = formalDisplay + Environment.NewLine + Environment.NewLine +
                    IncrementalGeneralChatShadowService.FormatDisplay(_lastIncrementalGeneralShadowResult);
                SaveIncrementalGeneralShadowLogButton.IsEnabled = true;
                bool providerUnavailable = _lastIncrementalGeneralShadowResult.Lines.Any(line =>
                    string.Equals(line.FailureReason, "local_provider_unavailable", StringComparison.Ordinal));
                IncrementalGeneralShadowStatusText.Text = providerUnavailable
                    ? "local_provider_unavailable：本地 Qwen3/Ollama 未就绪；本次增量 Shadow 已安全停止，不会回退到其他模型。"
                    : $"增量通用聊天 Shadow 已完成：新增模型调用 {_lastIncrementalGeneralShadowResult.ModelCallCount} 行；" +
                      $"已去重 {_lastIncrementalGeneralShadowResult.DeduplicatedCount} 行；" +
                      $"fail-closed {_lastIncrementalGeneralShadowResult.FailClosedCount} 行。";
                UpdateIncrementalGeneralSessionState();
                OcrStatusText.Text =
                    "聊天新增后，请点击“重新截取同一区域”→“识别截图文字”。不要重新选择聊天区域。";
                ChampionRc2ShadowStatusText.Text =
                    "RC.2 单行 Shadow 本次未运行：增量通用聊天测试模式优先。";
            }
            catch (Exception exception)
            {
                IncrementalGeneralShadowStatusText.Text =
                    $"增量通用聊天 Shadow 未输出：{exception.Message}。正式 0.3.0 Shadow 结果未受影响。";
            }

            return;
        }

        IncrementalGeneralShadowStatusText.Text =
            "增量通用聊天 Shadow 未启用；本次不会调用本地 Qwen3。";
        if (ChampionRc2ShadowCheckBox.IsChecked != true)
        {
            ChampionRc2ShadowStatusText.Text =
                "RC.2/Qwen3 测试模式未启用；本次只运行现有正式 0.3.0 Shadow。";
            return;
        }

        ChampionRc2ShadowStatusText.Text = "正在运行本机 RC.2 官方英雄名 + Qwen3 Shadow……";
        await Task.Yield();
        try
        {
            ChampionOfficialRc2RealScreenshotShadowService service =
                ChampionOfficialRc2RealScreenshotShadowService.CreateExplicit(_shadowSemanticService);
            _lastChampionRc2ShadowResult = service.Process(
                fusedResults,
                context,
                CultureInfo.CurrentUICulture.Name);
            ShadowResultTextBox.Text = formalDisplay + Environment.NewLine + Environment.NewLine +
                ChampionOfficialRc2RealScreenshotShadowService.FormatDisplay(_lastChampionRc2ShadowResult);
            SaveChampionRc2ShadowLogButton.IsEnabled = true;
            bool providerUnavailable = _lastChampionRc2ShadowResult.Lines.Any(line =>
                string.Equals(line.FailureReason, "local_provider_unavailable", StringComparison.Ordinal));
            ChampionRc2ShadowStatusText.Text = providerUnavailable
                ? "local_provider_unavailable：本地 Qwen3/Ollama 未就绪；本次 Shadow 已安全停止，不会回退到其他模型。"
                : $"RC.2 Shadow 已完成：本地模型调用 {_lastChampionRc2ShadowResult.ModelCallCount} 行；" +
                  $"fail-closed {_lastChampionRc2ShadowResult.FailClosedCount} 行。原图未写入数据集。";
        }
        catch (Exception exception)
        {
            ChampionRc2ShadowStatusText.Text =
                $"RC.2 Shadow 未输出：{exception.Message}。正式 0.3.0 Shadow 结果未受影响。";
        }
    }

    private void SaveChampionRc2ShadowLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastChampionRc2ShadowResult is null)
        {
            ChampionRc2ShadowStatusText.Text = "请先在勾选测试模式后完成一次截图识别。";
            return;
        }

        try
        {
            string path = ChampionOfficialRc2RealScreenshotShadowLogStore.Append(_lastChampionRc2ShadowResult);
            SaveChampionRc2ShadowLogButton.IsEnabled = false;
            ChampionRc2ShadowStatusText.Text =
                $"已保存本地脱敏 Shadow 日志：{path}。原图、Evidence 和 Pack 均未写入。";
        }
        catch (Exception exception)
        {
            ChampionRc2ShadowStatusText.Text = $"本地 Shadow 日志保存失败：{exception.Message}";
        }
    }

    private void SaveIncrementalGeneralShadowLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastIncrementalGeneralShadowResult is null)
        {
            IncrementalGeneralShadowStatusText.Text = "请先在勾选增量测试模式后完成一次截图识别。";
            return;
        }

        try
        {
            string path = IncrementalGeneralChatShadowLogStore.Append(_lastIncrementalGeneralShadowResult);
            SaveIncrementalGeneralShadowLogButton.IsEnabled = false;
            IncrementalGeneralShadowStatusText.Text =
                $"已保存本地脱敏增量 Shadow 日志：{path}。原图、路径、昵称、Evidence 和 Pack 均未写入。";
        }
        catch (Exception exception)
        {
            IncrementalGeneralShadowStatusText.Text = $"本地增量 Shadow 日志保存失败：{exception.Message}";
        }
    }

    private void ResetIncrementalGeneralSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_incrementalGeneralShadowService is null || !_incrementalGeneralShadowService.HasProcessedBodies)
        {
            UpdateIncrementalGeneralSessionState();
            return;
        }

        int cachedBodyCount = _incrementalGeneralShadowService.CachedBodyCount;
        MessageBoxResult confirmation = MessageBox.Show(
            $"这会清空本次已处理的 {cachedBodyCount} 条聊天去重记录。\n" +
            "当前区域会保留，之后重新截取会被视为新的测试起点。\n\n要清空吗？",
            "清空本次聊天去重记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _incrementalGeneralShadowService.ResetSession();
        _lastIncrementalGeneralShadowResult = null;
        SaveIncrementalGeneralShadowLogButton.IsEnabled = false;
        UpdateIncrementalGeneralSessionState();
        IncrementalGeneralShadowStatusText.Text =
            "已清空本次聊天去重记录；当前区域保持不变。请点击“重新截取同一区域”开始新的测试。";
    }

    private void SaveFeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastShadowFeedbackDraft is null)
        {
            FeedbackStatusText.Text = "请先完成一次 OCR 与 shadow 匹配。";
            return;
        }

        if (FeedbackKindComboBox.SelectedItem is not ShadowRuntimeFeedbackKindOption kind)
        {
            FeedbackStatusText.Text = "请选择反馈类别。";
            return;
        }

        if (FeedbackOriginComboBox.SelectedItem is not ShadowRuntimeFeedbackOriginOption origin ||
            origin.Value == "unknown")
        {
            FeedbackStatusText.Text = "请明确选择反馈来源；未知来源不能写入本地收件箱。";
            return;
        }

        try
        {
            string path = ShadowRuntimeFeedbackStore.Append(
                _lastShadowFeedbackDraft,
                kind.Value,
                origin.Value,
                FeedbackNotesTextBox.Text);
            SaveFeedbackButton.IsEnabled = false;
            FeedbackStatusText.Text =
                $"已保存本地待脱敏反馈：{path}。未进入 Pack。";
        }
        catch (Exception exception)
        {
            FeedbackStatusText.Text = $"保存运行反馈失败：{exception.Message}";
        }
    }

    private void PreflightFeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShadowRuntimeFeedbackExportManifest manifest =
                ShadowRuntimeFeedbackPreflight.InspectFile(ShadowRuntimeFeedbackStore.DefaultPath);
            string manifestPath = ShadowRuntimeFeedbackPreflight.WriteManifest(manifest);
            FeedbackStatusText.Text =
                $"本地预检完成：export_ready {manifest.ExportReadyCount}，blocked {manifest.BlockedCount}。" +
                $"仅写出清单：{manifestPath}；原始反馈未修改，未进入数据层。";
        }
        catch (FileNotFoundException)
        {
            FeedbackStatusText.Text = "本机待脱敏反馈收件箱尚不存在，未生成清单。";
        }
        catch (Exception exception)
        {
            FeedbackStatusText.Text = $"本地反馈预检失败：{exception.Message}";
        }
    }

    private void ResetShadowFeedback()
    {
        _lastShadowFeedbackDraft = null;
        _lastChampionRc2ShadowResult = null;
        _lastIncrementalGeneralShadowResult = null;
        SaveFeedbackButton.IsEnabled = false;
        SaveChampionRc2ShadowLogButton.IsEnabled = false;
        SaveIncrementalGeneralShadowLogButton.IsEnabled = false;
        FeedbackNotesTextBox.Text = string.Empty;
        FeedbackStatusText.Text = "运行反馈仅在你点击保存后写入本机待脱敏收件箱。";
        ChampionRc2ShadowStatusText.Text = "关闭时只运行现有正式 0.3.0 Shadow。启用后仅对可靠融合且命中官方英雄名的行调用本地 Qwen3。";
        IncrementalGeneralShadowStatusText.Text = "关闭时不运行增量通用聊天 Shadow。启用后仅对可靠的新正文调用本地 Qwen3，并对会话内旧行去重。";
        UpdateIncrementalGeneralSessionState();
    }

    private void UpdateIncrementalGeneralSessionState()
    {
        if (_selectedRegion is null)
        {
            IncrementalGeneralSessionStateText.Text = "尚未选择聊天区域。";
            ResetIncrementalGeneralSessionButton.IsEnabled = false;
            return;
        }

        int cachedBodyCount = _incrementalGeneralShadowService?.CachedBodyCount ?? 0;
        IncrementalGeneralSessionStateText.Text = cachedBodyCount > 0
            ? $"当前区域已保持；本会话已处理 {cachedBodyCount} 条正文。连续测试不要重新选区。"
            : "当前区域已保持；尚未建立聊天去重记录。连续测试不要重新选区。";
        ResetIncrementalGeneralSessionButton.IsEnabled = cachedBodyCount > 0;
    }

    private static BitmapImage LoadBitmapImage(
        string filePath)
    {
        var image =
            new BitmapImage();

        image.BeginInit();

        image.CacheOption =
            BitmapCacheOption.OnLoad;

        image.UriSource =
            new Uri(
                filePath,
                UriKind.Absolute);

        image.EndInit();
        image.Freeze();

        return image;
    }

    private void ContinuousIntervalComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshContinuousInterval();

    private void RefreshContinuousInterval()
    {
        // Visual watcher cadence is internal. OCR and Qwen run only for a settled
        // visual change, never simply because time elapsed.
        if (_continuousTimer is not null) _continuousTimer.Interval = TimeSpan.FromMilliseconds(250);
    }

    private void StartContinuousTranslation()
    {
        if (_isRunning) return;
        if (_continuousStartTask is { IsCompleted: false }) return;
        Trace("session", "start_requested", "starting");
        _continuousStartTask = StartContinuousTranslationAsync();
    }

    private void OcrTextDiagnosticCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        // The existing diagnostics expander is the explicit consent boundary.
        // Do not create an image-bearing trace until the user actually starts
        // or resumes a continuous session.
        OcrDiagnosticPathText.Text = "原始输入留痕将在下一次连续会话开始后保存到本机（含导出脱敏清单）。";
    }

    private void OcrTextDiagnosticCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _inputProvenanceTrace.Disable();
        _inputCorrelations.Clear();
        OcrDiagnosticPathText.Text = "原始输入留痕已关闭；不会保存新的截图或聊天文本。";
    }

    private async Task StartContinuousTranslationAsync()
    {
        if (_selectedRegion is null) return;
        if (_continuousSessionCancellation.IsCancellationRequested)
        {
            _continuousSessionCancellation.Dispose();
            _continuousSessionCancellation = new CancellationTokenSource();
        }
        CancellationToken startCancellation = _continuousSessionCancellation.Token;
        if (OcrTextDiagnosticCheckBox.IsChecked == true)
        {
            _inputProvenanceTrace.BeginSession(_continuousSessionId);
            OcrDiagnosticPathText.Text = _inputProvenanceTrace.SessionDirectory ?? _ocrTextDiagnosticTrace.FilePath;
        }
        // OCR model initialization and Ollama/Qwen warm-up are independent.
        // Start both immediately instead of paying their cold-start costs in
        // series before the first translation can be displayed.
        BeginLocalModelEnsure(force: true);
        StatusText.Text = "正在并行加载 OCR 与翻译模型……";
        StatusText.Foreground = System.Windows.Media.Brushes.Gold;
        ContinuousStatusText.Text = "正在并行准备 GPU OCR 与本地翻译模型，OCR 就绪后会立即开始监测。";
        Trace("startup", "parallel_preload_started", "ocr_and_translation_model");
        Trace("ocr_provider", "probe_started", "loading");
        if (_koreanOcrProvider is not null)
        {
            Trace("ocr_provider", "korean_probe_started", "loading");
            _koreanOcrProbeTask = _koreanOcrProvider.ProbeAsync(startCancellation);
        }
        else
        {
            Trace("ocr_provider", "korean_fallback_disabled", "realtime_low_latency_profile");
        }
        Stopwatch ocrProbe = Stopwatch.StartNew();
        HighAccuracyOcrCapability capability;
        try { capability = await _highAccuracyOcrProvider.ProbeAsync(startCancellation); }
        catch (OperationCanceledException) { Trace("ocr_provider", "probe_cancelled", "cancelled"); return; }
        if (startCancellation.IsCancellationRequested) return;
        if (!capability.Ready)
        {
            Trace("ocr_provider", "probe_completed", capability.State,
                elapsedMilliseconds: ocrProbe.ElapsedMilliseconds);
            StatusText.Text = capability.Message;
            StatusText.Foreground = System.Windows.Media.Brushes.Gold;
            ContinuousStatusText.Text = "高精度识别尚未就绪；为避免低精度误译，连续翻译未启动。";
            return;
        }
        Trace("ocr_provider", "probe_completed", "ready",
            elapsedMilliseconds: ocrProbe.ElapsedMilliseconds);
        RefreshContinuousInterval();
        _continuousCoordinator.Start();
        _sourceIdentityColorMemory.Reset();
        _isRunning = true;
        _startupBaselinePending = true;
        _startupEmptyObservedAt = null;
        // Force one initial observation. If it contains a populated historical
        // viewport, the differ records it as a baseline instead of translating
        // every old line.
        _pendingOcrAfterBusy = true;
        _continuousTimer.Start();
        if (_translationSidecar is null)
        {
            // Keep the overlay independent. An owned WPF window is hidden when
            // its owner is minimized, which made it appear only after Alt+Tab.
            _translationSidecar = new TranslationSidecarWindow();
            _translationSidecar.ViewReceipt += _runtimeTrace.AppendViewReceipt;
        }
        _translationSidecar.ResetSession();
        _translationSidecar.PositionNear(_selectedRegion.Value);
        // The game chat is normally hidden. Prepare the sidecar now but reveal
        // it only after a new translated chat message is available.
        _translationSidecar.Hide();
        Trace("overlay", "window_prepared", "hidden_until_translation");
        DockSidecarButton.IsEnabled = true;
        AdjustSidecarButton.IsEnabled = true;
        StatusText.Text = "连续翻译中";
        StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        ContinuousStatusText.Text = "当前区域正在连续翻译；识别到可靠新消息时会显示在聊天侧边。";
        StartButton.Content = "停止连续翻译";
        PauseButton.IsEnabled = true;
        PauseButton.Content = "暂停";
        Trace("session", "monitoring_started", "running");
        if (MinimizeAfterStartCheckBox.IsChecked == true && WindowState != WindowState.Minimized)
        {
            _ = Dispatcher.BeginInvoke(() => WindowState = WindowState.Minimized, DispatcherPriority.Background);
        }
    }

    private void StopContinuousTranslation()
    {
        Trace("session", "stop_requested", "stopping");
        _continuousCoordinator.Stop();
        _continuousSessionCancellation.Cancel();
        _continuousSessionId++;
        _inputProvenanceTrace.Disable();
        _inputCorrelations.Clear();
        _continuousTimer.Stop();
        _isRunning = false;
        _pendingOcrAfterBusy = false;
        _startupBaselinePending = false;
        _startupEmptyObservedAt = null;
        ClearBufferedOcrFrames();
        _runtimeEventLedger.Reset();
        ResetTranslationDispatcher();
        _translationSidecar?.ResetSession();
        _translationSidecar?.Hide();
        DockSidecarButton.IsEnabled = false;
        AdjustSidecarButton.IsEnabled = false;
        StatusText.Text = "已停止";
        StatusText.Foreground = System.Windows.Media.Brushes.LightCoral;
        ContinuousStatusText.Text = "连续翻译已停止；可开始、选择其他区域，或使用下方诊断工具。";
        StartButton.Content = "开始连续翻译";
        PauseButton.IsEnabled = false;
        TestCaptureButton.IsEnabled = _selectedRegion is not null;
        Trace("session", "stopped", "stopped");
    }

    private void BeginLocalModelEnsure(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow < _nextLocalModelCheckAt) return;
        if (_localModelTask is { IsCompleted: false }) return;
        _localModelStatus = new LocalModelStatus("checking", "正在检查/启动本地 Ollama……", false);
        _localModelEnsureStartedAt = DateTimeOffset.UtcNow;
        _localModelTask = LocalModelManager.EnsureQwen3Async(TimeSpan.FromSeconds(20));
    }

    private void PollLocalModelStatus()
    {
        if (_localModelTask is { IsCompletedSuccessfully: true })
        {
            _localModelStatus = _localModelTask.Result;
            _nextLocalModelCheckAt = DateTimeOffset.UtcNow.AddSeconds(_localModelStatus.Ready ? 20 : 6);
        }
        else if (_localModelTask is { IsFaulted: true })
        {
            _localModelStatus = new LocalModelStatus("start_failed", "本地 Ollama 状态检查失败；会自动重试。", false, _localModelTask.Exception?.GetBaseException().Message);
            _nextLocalModelCheckAt = DateTimeOffset.UtcNow.AddSeconds(6);
        }
        if (!_localModelStatus.Ready && (_localModelTask is null || _localModelTask.IsCompleted)) BeginLocalModelEnsure();
        if (!string.Equals(_lastTracedModelState, _localModelStatus.State, StringComparison.Ordinal))
        {
            _lastTracedModelState = _localModelStatus.State;
            Trace("translation_model", "readiness_changed", _localModelStatus.State,
                elapsedMilliseconds: _localModelEnsureStartedAt is { } startedAt
                    ? Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds)
                    : null,
                queueCount: TranslationQueueCount);
        }
    }

    private int TranslationQueueCount => _runtimeDispatcher?.Count ?? 0;

    private void EnsureRuntimeDispatcher()
    {
        if (_runtimeDispatcher is not null || _shadowSemanticService is null) return;
        var workers = new RuntimeTranslationWorkers(_shadowSemanticService);
        _runtimeDispatcher = new RuntimeTranslationDispatcher(workers.Deterministic, workers.Model);
    }

    private void RecordDispatchTransitions(RuntimeDispatchUpdate update)
    {
        foreach (RuntimeDispatchTransition transition in update.Transitions)
        {
            _runtimeTrace.AppendDispatch(transition, _runtimeDispatcher?.FastQueued ?? 0, _runtimeDispatcher?.ModelQueued ?? 0);
            if (!transition.IsFailure) continue;
            _coverageTracker.RecordSuperseded(new[] { transition.Message });
            _continuousCoordinator.RecordTerminalFailures(1);
        }
    }

    private void ResetTranslationDispatcher()
    {
        if (_runtimeDispatcher is not null) RecordDispatchTransitions(_runtimeDispatcher.Reset());
    }

    private void PumpTranslationDispatcher()
    {
        if (_runtimeDispatcher is null || _continuousCoordinator.State != ContinuousTranslationState.Running) return;
        RuntimeDispatchUpdate update = _runtimeDispatcher.Advance();
        RecordDispatchTransitions(update);
        foreach (RuntimeDispatchDelivery delivery in update.Deliveries)
        {
            IncrementalGeneralChatShadowResult result = delivery.Result;
            if (OcrTextDiagnosticCheckBox.IsChecked == true)
            {
                _ocrTextDiagnosticTrace.AppendTranslation(result);
                _inputProvenanceTrace.RecordTranslation(result, _inputCorrelations);
            }
            _lastIncrementalGeneralShadowResult = result;
            TranslationSidecarApplyResult presentation = _translationSidecar?.Present(result.Lines.Single().SafeDisplay!)
                ?? TranslationSidecarApplyResult.Empty;
            _runtimeTrace.AppendPresentation(delivery, presentation);
            if (OcrTextDiagnosticCheckBox.IsChecked == true)
                _inputProvenanceTrace.RecordOverlayPresentation(result, _inputCorrelations, presentation.Enqueued > 0);
            if (presentation.Enqueued > 0 && _translationSidecar is not null)
            {
                _translationSidecar.ShowWithoutActivation();
                _continuousCoordinator.RecordResult(result);
                _coverageTracker.RecordTranslation(result);
            }
            else
            {
                _coverageTracker.RecordSuperseded(new[] { delivery.Message });
                _continuousCoordinator.RecordTerminalFailures(1);
            }
            // The view no longer decides safety. Submission is not proof that
            // the compositor painted a row, especially in a burst exceeding 8 rows.
            Trace("overlay", "event_presented", presentation.Enqueued > 0 ? "submitted" : "view_unavailable",
                queueCount: TranslationQueueCount, displayedMessages: presentation.Enqueued,
                failedMessages: presentation.Enqueued > 0 ? 0 : 1,
                overlayMissingSafePrefixRows: presentation.MissingSafePrefix,
                overlayUntrustedPresentationRows: presentation.UntrustedPresentation);
        }
        if (update.Transitions.Count > 0 || update.Deliveries.Count > 0) RefreshContinuousStatus();
    }

    private void ContinuousTimer_Tick(object? sender, EventArgs e)
    {
        // Translation completions/deadlines are independent of visual changes
        // and OCR's busy flag. Poll before any capture-loop early return.
        if (_continuousCoordinator.State == ContinuousTranslationState.Running) PumpTranslationDispatcher();
        if (!_continuousCoordinator.TryBeginTick() || _selectedRegion is not Rect region) return;
        string? transientPath = null;
        string? scaledPath = null;
        try
        {
            // Windows exposes no safe "new in-game chat" event.  This is a
            // non-invasive visual watcher: unchanged pixels never invoke OCR.
            VisualChatWatchDecision visual = _visualChangeWatcher.Observe(
                ScreenCaptureService.CaptureVisualFingerprint(region), DateTimeOffset.UtcNow);
            if (!string.Equals(_lastTracedVisualStatus, visual.Status, StringComparison.Ordinal))
            {
                _lastTracedVisualStatus = visual.Status;
                Trace("visual", "frame_observed", visual.Status);
            }
            bool ocrBusy = _continuousOcrTask is { IsCompleted: false };
            if (visual.ShouldRunOcr && ocrBusy)
            {
                BufferOcrFrame(region, "recognition_in_progress");
                ContinuousStatusText.Text = $"检测到后续消息；已暂存 {_pendingOcrFramePaths.Count} 个聊天画面。";
                Trace("ocr", "frame_buffered", "waiting_for_active_recognition");
                _continuousCoordinator.CompleteTick(null, false);
                return;
            }
            bool shouldRunOcr = visual.ShouldRunOcr || _pendingOcrAfterBusy || _pendingOcrFramePaths.Count > 0;
            if (!shouldRunOcr)
            {
                PollLocalModelStatus();
                if (TranslationQueueCount > 0)
                {
                    ContinuousStatusText.Text = $"正在处理队列中的 {TranslationQueueCount} 条消息……";
                    _continuousCoordinator.CompleteTick(null, false);
                    return;
                }
                ContinuousStatusText.Text = visual.Status switch
                {
                    "change_detected" or "debouncing" => "检测到聊天区域变化，正在等待文字稳定……",
                    _ => "自动监测新消息中；画面无实质变化。"
                };
                _continuousCoordinator.CompleteTick(null, false);
                return;
            }

            if (ocrBusy)
            {
                ContinuousStatusText.Text = "OCR 队列忙碌；监测仍在继续，已合并为最新稳定画面。";
                _continuousCoordinator.CompleteTick(null, false);
                return;
            }
            // A real new stable visual change and an already-buffered newest
            // viewport take precedence over the idle OCR cooldown. Otherwise
            // a message can wait behind a 2.5 s no-change backoff repeatedly.
            bool urgentViewport = visual.ShouldRunOcr || _pendingOcrFramePaths.Count > 0;
            if (DateTimeOffset.UtcNow < _nextOcrAttemptAt && !urgentViewport)
            {
                if (visual.ShouldRunOcr) BufferOcrFrame(region, "ocr_recovery_wait");
                _pendingOcrAfterBusy = true;
                ContinuousStatusText.Text = "高精度 OCR 正在短暂恢复；画面监测仍在继续。";
                _continuousCoordinator.CompleteTick(null, false);
                return;
            }
            if (_shadowSemanticService is null || ShadowContextComboBox.SelectedItem is not ShadowMatchContextOption context ||
                SemanticLocaleComboBox.SelectedItem is not ShadowSemanticLocaleOption semanticLocale)
                throw new InvalidOperationException("连续翻译配置尚未就绪。");
            long session = _continuousSessionId;
            CancellationToken cancellationToken = _continuousSessionCancellation.Token;
            if (visual.ShouldRunOcr && _pendingOcrFramePaths.Count > 0)
                BufferOcrFrame(region, "preserved_behind_backlog");
            CapturedOcrFrame? bufferedPath = _pendingOcrFramePaths.Count > 0 ? _pendingOcrFramePaths.Dequeue() : null;
            string trigger = bufferedPath is not null ? "buffered_stable_frame" :
                _pendingOcrAfterBusy ? "latched_latest_frame" : visual.Status;
            _pendingOcrAfterBusy = false;
            ContinuousStatusText.Text = "OCR 队列：正在快速识别新增聊天……";
            _continuousOcrTask = ProcessRealtimeOcrFrameAsync(
                region, session, cancellationToken, context, semanticLocale, bufferedPath);
            Trace("ocr", "frame_queued", trigger);
            _continuousCoordinator.CompleteTick(null, false);
        }
        catch (Exception exception)
        {
            _continuousCoordinator.CompleteTick(null, true);
            ContinuousStatusText.Text = $"本轮未输出：{exception.GetType().Name}；下一轮会安全重试。";
            Trace("monitor", "tick_failed", "retry_next_frame", exception: exception);
        }
        finally { ScreenCaptureService.DeleteTransient(transientPath); ScreenCaptureService.DeleteTransient(scaledPath); }
    }

    private async Task ProcessRealtimeOcrFrameAsync(Rect region, long session, CancellationToken cancellationToken,
        ShadowMatchContextOption context, ShadowSemanticLocaleOption semanticLocale, CapturedOcrFrame? capturedFrame = null)
    {
        string? path = null;
        string? ocrCandidatePath = null;
        string? koreanCandidatePath = null;
        Stopwatch total = Stopwatch.StartNew();
        long frameId = capturedFrame?.Id ?? 0;
        try
        {
            CapturedOcrFrame frame = capturedFrame ??= CaptureRealtimeFrame(region);
            frameId = frame.Id;
            path = frame.Path;
            DateTimeOffset ocrStartedAt = DateTimeOffset.UtcNow;
            Stopwatch preprocessing = Stopwatch.StartNew();
            ocrCandidatePath = OcrChatPreprocessor.CreateLolChatHighContrastCandidate(path);
            Trace("ocr", "chat_preprocessing_completed", "lol_color_contrast_2x",
                elapsedMilliseconds: preprocessing.ElapsedMilliseconds);
            Stopwatch ocr = Stopwatch.StartNew();
            Trace("ocr", "recognition_started", "processing");
            IReadOnlyList<StructuredOcrLine> nativeLines = await _highAccuracyOcrProvider.RecognizeLinesAsync(
                ocrCandidatePath, "auto", frameId, cancellationToken);
            Trace("ocr", "recognizer_selected", "gpu_primary");
            nativeLines = ScaleStructuredLines(nativeLines, .5);
            if (ShouldTryKoreanFallback(nativeLines) && _koreanOcrProvider is not null)
            {
                try
                {
                    HighAccuracyOcrCapability koreanCapability = _koreanOcrProbeTask is null
                        ? await _koreanOcrProvider.ProbeAsync(cancellationToken)
                        : await _koreanOcrProbeTask;
                    Trace("ocr_provider", "korean_probe_completed", koreanCapability.State);
                    if (koreanCapability.Ready)
                    {
                        koreanCandidatePath = OcrChatPreprocessor.CreateScaledCandidate(path);
                        IReadOnlyList<StructuredOcrLine> koreanLines = await _koreanOcrProvider.RecognizeLinesAsync(
                            koreanCandidatePath, "ko-KR", frameId, cancellationToken);
                        koreanLines = ScaleStructuredLines(koreanLines, .5);
                        if (koreanLines.Sum(line => line.Text.Count(IsHangul)) >= 4)
                        {
                            nativeLines = koreanLines;
                            Trace("ocr", "recognizer_selected", "korean_fallback_gpu");
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Korean enhancement is optional for this frame. Keep the
                    // already completed primary OCR result instead of turning
                    // one fallback failure into a total recognition failure.
                    Trace("ocr", "korean_fallback_failed", "primary_result_kept", exception: exception);
                }
            }
            Trace("ocr", "recognition_completed", nativeLines.Count > 0 ? "lines_detected" : "zero_lines",
                elapsedMilliseconds: ocr.ElapsedMilliseconds, detectedLines: nativeLines.Count);
            var frameTiming = new RuntimeFrameTiming(frame.Id, frame.CapturedAtUtc, ocrStartedAt, DateTimeOffset.UtcNow);
            _runtimeTrace.AppendFrameTiming(frameTiming, nativeLines.Count);
            IReadOnlyList<OcrRecognitionResult> candidates = nativeLines.Count == 0
                ? Array.Empty<OcrRecognitionResult>()
                : new[] { new OcrRecognitionResult(
                    string.Join('\n', nativeLines.Select(line => line.Text)),
                    "ppocrv5-cpp-gpu",
                    nativeLines) };
            if (cancellationToken.IsCancellationRequested || session != _continuousSessionId) return;
            IReadOnlyList<OcrShadowLineResult> fused = BuildCoordinateLogicalRows(candidates, path, semanticLocale, context)
                .Select(line => line with { FrameTiming = frameTiming }).ToArray();
            // Keep startup baseline mode until the first confirmed populated
            // viewport or the first genuinely small new-message release. OCR
            // model loading can exceed a fixed deadline, so a wall-clock timeout
            // caused existing chat history to be translated as a fresh burst.
            bool mayEstablishStartupBaseline = _startupBaselinePending;
            bool allowImmediateTimestampedRelease = !mayEstablishStartupBaseline;
            VisibleMessageDiffResult diff = _runtimeEventLedger.Observe(
                fused,
                _coordinateVisualRows,
                mayEstablishStartupBaseline,
                allowImmediateTimestampedRelease);
            if (diff.InitialBaselineEstablished)
            {
                _startupBaselinePending = false;
                Trace("message_diff", "startup_baseline_established", "historical_messages_suppressed",
                    visibleMessages: diff.VisibleCount);
            }
            else if (_startupBaselinePending && diff.VisibleCount == 0 && diff.PendingConfirmationCount == 0)
            {
                // One completed OCR pass over an empty selected chat region is
                // a sufficient baseline. Waiting eight seconds swallowed a real
                // first message when the player typed soon after selection.
                _startupEmptyObservedAt ??= DateTimeOffset.UtcNow;
                _startupBaselinePending = false;
                Trace("message_diff", "startup_baseline_established", "empty_viewport_confirmed");
            }
            else if (_startupBaselinePending && diff.Inserted.Count > 0)
            {
                _startupBaselinePending = false;
                Trace("message_diff", "startup_baseline_completed", "first_live_messages_released");
            }
            _coverageTracker.ObserveFrame(diff);
            _runtimeTrace.AppendObservationDecisions(frameId, diff);
            UpdateCoverageStatus();
            if (OcrTextDiagnosticCheckBox.IsChecked == true)
            {
                _ocrTextDiagnosticTrace.Append(nativeLines, diff);
                IReadOnlyDictionary<string, RuntimeInputCorrelation> correlations = _inputProvenanceTrace.RecordOcrObservation(
                    session, frameId, region, path, _coordinateInputEvidenceRows.Values.OrderBy(row => row.LineNumber).ToArray(), diff);
                foreach ((string occurrenceKey, RuntimeInputCorrelation correlation) in correlations)
                    _inputCorrelations[occurrenceKey] = correlation;
                if (_inputProvenanceTrace.SessionDirectory is not null)
                    OcrDiagnosticPathText.Text = _inputProvenanceTrace.SessionDirectory;
            }
            if (diff.Inserted.Count > 0)
            {
                EnsureRuntimeDispatcher();
                _runtimeDispatcher!.Enqueue(diff.Inserted, context, CultureInfo.CurrentUICulture.Name);
                PumpTranslationDispatcher();
            }
            TranslationCoverageSnapshot frameCoverage = _coverageTracker.Snapshot;
            DateTimeOffset completedAt = DateTimeOffset.UtcNow;
            _nextOcrAttemptAt = diff.PendingConfirmationCount > 0
                ? completedAt.AddMilliseconds(300)
                : diff.Inserted.Count > 0
                    ? completedAt.AddMilliseconds(250)
                    : completedAt.AddSeconds(2.5);
            Trace("message_diff", "frame_compared",
                diff.Inserted.Count > 0 ? "new_messages" : diff.PendingConfirmationCount > 0 ? "waiting_second_ocr_frame" : "no_new_message",
                detectedLines: nativeLines.Count,
                visibleMessages: diff.VisibleCount,
                newMessages: diff.Inserted.Count,
                queueCount: TranslationQueueCount,
                estimatedMessageCandidates: frameCoverage.EstimatedMessageCandidates,
                queuedMessages: frameCoverage.QueuedMessages,
                measurableCoveragePercent: frameCoverage.MeasurableCoveragePercent,
                estimatedSystemCandidates: frameCoverage.EstimatedSystemCandidates,
                queuedSystemMessages: frameCoverage.QueuedSystemMessages,
                displayedSystemMessages: frameCoverage.DisplayedSystemMessages,
                rejectedUnreliableRows: CountDecision(diff, "rejected_unreliable_row"),
                rejectedNoChatPrefixRows: CountDecision(diff, "rejected_no_chat_prefix"),
                rejectedEmptyOrSystemRows: CountDecision(diff, "rejected_empty_or_system"),
                rejectedTooShortRows: CountDecision(diff, "rejected_too_short"),
                waitingConfirmationRows: CountDecision(diff, "waiting_second_ocr_frame"),
                suppressedBulkRealignmentRows: CountDecision(diff, "suppressed_bulk_ocr_realignment"),
                suppressedPreexistingViewportRows: CountDecision(diff, "suppressed_preexisting_viewport_variant"),
                suppressedRecentOcrVariantRows: CountDecision(diff, "suppressed_recent_ocr_variant"),
                suppressedSystemContinuationRows: CountDecision(diff, "suppressed_system_continuation"));
            _lastContinuousOcrAt = DateTimeOffset.Now;
            PollLocalModelStatus();
            _ = Dispatcher.BeginInvoke(PumpTranslationDispatcher, DispatcherPriority.Background);
            ContinuousStatusText.Text = $"快速 OCR {ocr.ElapsedMilliseconds}ms · 待确认 {diff.PendingConfirmationCount} · 逻辑新增 {diff.Inserted.Count} · 队列 {TranslationQueueCount}";
            if (diff.PendingConfirmationCount > 0)
            {
                // A new line must survive a second OCR observation. Schedule a
                // confirmation even when the pixels remain unchanged.
                _pendingOcrAfterBusy = true;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (capturedFrame is not null)
                _runtimeTrace.AppendFrameDisposition(frameId, capturedFrame.CapturedAtUtc, "ocr_failed");
            if (OcrTextDiagnosticCheckBox.IsChecked == true)
                _inputProvenanceTrace.RecordPipelineFailure(session, frameId, "ocr_or_fusion", exception.GetType().Name);
            _nextOcrAttemptAt = DateTimeOffset.UtcNow.AddSeconds(3);
            ContinuousStatusText.Text = $"识别较慢或失败（{exception.GetType().Name}）；监测仍在继续。";
            Trace("ocr", "recognition_failed", "retry_next_change", exception: exception);
        }
        finally
        {
            ScreenCaptureService.DeleteTransient(koreanCandidatePath);
            ScreenCaptureService.DeleteTransient(ocrCandidatePath);
            ScreenCaptureService.DeleteTransient(path);
        }
    }

    private void BufferOcrFrame(Rect region, string reason)
    {
        string? path = null;
        try
        {
            CapturedOcrFrame frame = CaptureRealtimeFrame(region);
            path = frame.Path;
            int replaced = 0;
            while (_pendingOcrFramePaths.Count >= MaximumBufferedOcrFrames)
            {
                CapturedOcrFrame replacedFrame = _pendingOcrFramePaths.Dequeue();
                ScreenCaptureService.DeleteTransient(replacedFrame.Path);
                _runtimeTrace.AppendFrameDisposition(replacedFrame.Id, replacedFrame.CapturedAtUtc, "replaced_before_ocr");
                replaced++;
            }
            _pendingOcrFramePaths.Enqueue(frame);
            path = null;
            Trace("ocr", "frame_buffered", reason);
            if (replaced > 0)
                Trace("ocr", "stale_viewport_replaced", "latest_viewport_kept",
                    queueCount: _pendingOcrFramePaths.Count);
        }
        finally
        {
            ScreenCaptureService.DeleteTransient(path);
        }
    }

    private static int CountDecision(VisibleMessageDiffResult diff, string decision) =>
        (diff.Decisions ?? Array.Empty<VisibleMessageDecision>()).Count(item =>
            string.Equals(item.Decision, decision, StringComparison.Ordinal));

    private void ClearBufferedOcrFrames()
    {
        while (_pendingOcrFramePaths.Count > 0)
        {
            CapturedOcrFrame frame = _pendingOcrFramePaths.Dequeue();
            ScreenCaptureService.DeleteTransient(frame.Path);
            _runtimeTrace.AppendFrameDisposition(frame.Id, frame.CapturedAtUtc, "cancelled_before_ocr");
        }
    }

    private CapturedOcrFrame CaptureRealtimeFrame(Rect region)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        long id = Interlocked.Increment(ref _continuousFrameId);
        string path = ScreenCaptureService.CaptureTransient(region);
        _runtimeTrace.AppendFrameDisposition(id, capturedAt, "captured");
        return new(path, id, capturedAt);
    }

    private static IReadOnlyList<StructuredOcrLine> ScaleStructuredLines(
        IReadOnlyList<StructuredOcrLine> lines,
        double scale) => lines.Select(line => line with
    {
        Bounds = new OcrBoundingBox(
            line.Bounds.X * scale,
            line.Bounds.Y * scale,
            line.Bounds.Width * scale,
            line.Bounds.Height * scale),
        Words = line.Words.Select(word => word with
        {
            Bounds = new OcrBoundingBox(
                word.Bounds.X * scale,
                word.Bounds.Y * scale,
                word.Bounds.Width * scale,
                word.Bounds.Height * scale)
        }).ToArray()
    }).ToArray();

    private static bool ShouldTryKoreanFallback(IReadOnlyList<StructuredOcrLine> lines)
    {
        if (lines.Count < 3) return false;
        int timestamps = lines.Count(line => OcrShadowLineFusion.ExtractTimestamp(line.Text) is not null);
        int substantial = lines.Count(line => line.Text.Count(char.IsLetterOrDigit) >= 8);
        int hanCharacters = lines.Sum(line => line.Text.Count(character => character is >= '\u3400' and <= '\u9fff'));
        return timestamps * 3 < lines.Count && (hanCharacters >= 2 || substantial * 2 < lines.Count);
    }

    private static bool IsHangul(char character) => character is >= '\u1100' and <= '\u11ff' or
        >= '\u3130' and <= '\u318f' or >= '\uac00' and <= '\ud7af';

    private static NativePpOcrV5SidecarProvider? CreateKoreanOcrProvider()
    {
        string? projectRoot = FindProjectRoot(AppContext.BaseDirectory);
        string packagedRoot = Path.Combine(AppContext.BaseDirectory, "models", "ppocrv5", "korean-mobile");
        string developmentRoot = projectRoot is null
            ? string.Empty
            : Path.Combine(projectRoot, "tools", "ppocr-windows-sidecar", ".third-party", "models", "korean_PP-OCRv3_mobile_rec_infer");
        string modelRoot = File.Exists(Path.Combine(packagedRoot, "inference.json")) ? packagedRoot : developmentRoot;
        string packagedDictionary = Path.Combine(AppContext.BaseDirectory, "models", "ppocrv5", "korean_dict.txt");
        string developmentDictionary = projectRoot is null
            ? string.Empty
            : Path.Combine(projectRoot, "tools", "ppocr-windows-sidecar", ".third-party", "models", "korean_dict.txt");
        string dictionary = File.Exists(packagedDictionary) ? packagedDictionary : developmentDictionary;
        return File.Exists(Path.Combine(modelRoot, "inference.json")) && File.Exists(dictionary)
            ? new NativePpOcrV5SidecarProvider(
                recognizerModelDirectory: modelRoot,
                dictionaryPath: dictionary,
                useGpu: true)
            : null;
    }

    private static string? FindProjectRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LoLTranslator.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private IReadOnlyList<OcrShadowLineResult> BuildCoordinateLogicalRows(IReadOnlyList<OcrRecognitionResult> candidates, string imagePath,
        ShadowSemanticLocaleOption semanticLocale, ShadowMatchContextOption context)
    {
        _coordinateVisualRows.Clear();
        _coordinateInputEvidenceRows.Clear();
        IReadOnlyList<CoordinateLogicalOcrLine> rows = CoordinateOcrLineFusion.Fuse(candidates
            .SelectMany(candidate => candidate.StructuredLines ?? Array.Empty<StructuredOcrLine>()));
        using var sourceBitmap = new System.Drawing.Bitmap(imagePath);
        int blueIdentityRows = 0, redIdentityRows = 0, goldIdentityRows = 0, whiteIdentityRows = 0, unknownIdentityRows = 0;
        int alliedRelationRows = 0, enemyRelationRows = 0;
        OcrShadowLineResult[] results = rows.Select((row, index) =>
        {
            ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(row.Preferred.Text);
            // The whole row can include a red enemy spell/target or a gold
            // system suffix. Determine sender colour from the sender portion
            // only; full-row analysis remains the stable visual hash used by
            // the sequence differ.
            ChatRowVisualAnalysis visual = ScreenCaptureService.ComputeRowVisualAnalysis(sourceBitmap, row.Preferred.Bounds);
            Match systemActor = SystemActorPrefix.Match(row.Preferred.Text);
            string? sourceIdentity = systemActor.Success
                ? systemActor.Groups["identity"].Value
                : envelope.PlayerOrRole;
            int sourceIdentityOffset = systemActor.Success
                ? systemActor.Groups["identity"].Index
                : envelope.Prefix?.IndexOf(envelope.PlayerOrRole ?? string.Empty, StringComparison.Ordinal) ?? -1;
            (double identityStart, double identityEnd) = systemActor.Success
                ? (Math.Clamp(systemActor.Groups["identity"].Index / (double)Math.Max(1, row.Preferred.Text.Length) - .02, .02, .82),
                   Math.Clamp((systemActor.Groups["identity"].Index + systemActor.Groups["identity"].Length) /
                       (double)Math.Max(1, row.Preferred.Text.Length) + .02, .18, .98))
                : (IdentityStartFraction(row.Preferred.Text, envelope), IdentityEndFraction(row.Preferred.Text, envelope));
            if (TryChampionIdentityFractions(row.Preferred.Text, sourceIdentity, sourceIdentityOffset,
                    out double championStart, out double championEnd))
            {
                // The player ID is OCR-unstable and is no longer rendered. Read
                // relation colour from the champion slot whenever possible; it
                // has the same source colour and a stable official vocabulary.
                identityStart = championStart;
                identityEnd = championEnd;
            }
            ChatRowVisualAnalysis identityVisual = ScreenCaptureService.ComputeRowVisualAnalysis(
                sourceBitmap,
                row.Preferred.Bounds,
                identityStart,
                identityEnd);
            string? sourceIdentityColor = identityVisual.DominantIdentityColor;
            string? identityRelation = _sourceIdentityColorMemory.Resolve(
                sourceIdentity,
                sourceIdentityColor,
                IsExplicitAlliedChannel(envelope.Channel));
            switch (sourceIdentityColor)
            {
                case "blue": blueIdentityRows++; break;
                case "red": redIdentityRows++; break;
                case "gold": goldIdentityRows++; break;
                case "white": whiteIdentityRows++; break;
                default: unknownIdentityRows++; break;
            }
            if (identityRelation == "ally") alliedRelationRows++;
            if (identityRelation == "enemy") enemyRelationRows++;
            _coordinateVisualRows[index + 1] = (visual.Hash, row.Preferred.Bounds);
            IReadOnlyList<SlangGraphSemanticMatch> matches = _shadowSemanticService!.Match(row.Preferred.Text, semanticLocale.LanguageTag, context)
                .SelectMany(line => line.Matches).ToArray();
            OcrShadowLineSource[] sources = row.Candidates.Select((candidate, sourceIndex) => new OcrShadowLineSource(
                sourceIndex, candidate.EngineLocale, index + 1, candidate.Text, OcrShadowLineFusion.ExtractTimestamp(candidate.Text))).ToArray();
            var reasons = new List<string> { "selected_chat_region_coordinate_row" };
            if (row.Candidates.Count > 1) reasons.Add("coordinate_row_aligned");
            if (visual.HasSystemGoldText) reasons.Add("lol_system_gold_row");
            if (sourceIdentityColor is not null) reasons.Add("lol_source_identity_" + sourceIdentityColor);
            if (identityRelation is "ally") reasons.Add("lol_allied_identity_row");
            if (identityRelation is "enemy") reasons.Add("lol_enemy_identity_row");
            if ((sourceIdentityColor is "white" or null) && (identityRelation is "ally" or "enemy"))
                reasons.Add("lol_source_identity_color_memory");
            if (envelope.HasHighConfidencePrefix)
                reasons.Add("structured_chat_envelope_anchor");
            if (envelope.RepairedMisplacedDelimiter)
                reasons.Add("ocr_misplaced_chat_delimiter_repaired");
            RuntimeTextColor actorColor = RuntimePresentationProjector.ParseColor(sourceIdentityColor);
            RuntimeColorEvidence actorEvidence = RuntimeColorEvidence.PixelRegion;
            if (actorColor is not (RuntimeTextColor.Blue or RuntimeTextColor.Red) && identityRelation is "ally" or "enemy")
            {
                actorColor = identityRelation == "ally" ? RuntimeTextColor.Blue : RuntimeTextColor.Red;
                actorEvidence = RuntimeColorEvidence.SessionMemory;
            }
            // Each target has its own sampled region. Never invert the sender
            // colour to guess a target relation. Character fractions are an
            // approximation until OCR provides trustworthy word geometry.
            var targetColors = RuntimePresentationProjector.TargetSlots(row.Preferred.Text, envelope).Select(slot =>
            {
                double length = Math.Max(1, row.Preferred.Text.Length);
                var targetVisual = ScreenCaptureService.ComputeRowVisualAnalysis(sourceBitmap, row.Preferred.Bounds,
                    slot.Start / length, (slot.Start + slot.Length) / length);
                return new RuntimeIdentityColorHint(slot.Champion,
                    RuntimePresentationProjector.ParseColor(targetVisual.DominantIdentityColor), RuntimeColorEvidence.PixelRegion);
            }).ToArray();
            var result = new OcrShadowLineResult(index + 1, row.Preferred.Text, matches, sources,
                new OcrFusionMetadata(row.Candidates.Count > 1 ? "fused" : "single_source", 0.95, reasons))
            { SourceStyle = new(actorColor, actorEvidence, visual.HasSystemGoldText, Array.AsReadOnly(targetColors)) };
            _coordinateInputEvidenceRows[index + 1] = new RuntimeInputEvidenceRow(
                index + 1, result, row.Preferred.Bounds, row.Candidates, envelope);
            return result;
        }).ToArray();
        Trace("color", "frame_identity_summary",
            $"blue={blueIdentityRows};red={redIdentityRows};gold={goldIdentityRows};white={whiteIdentityRows};unknown={unknownIdentityRows};ally={alliedRelationRows};enemy={enemyRelationRows}");
        return results;
    }

    private static bool IsExplicitAlliedChannel(string? channel) =>
        !string.IsNullOrWhiteSpace(channel) &&
        (channel.Contains("team", StringComparison.OrdinalIgnoreCase) ||
         channel.Contains("队伍", StringComparison.OrdinalIgnoreCase) ||
         channel.Contains("隊伍", StringComparison.OrdinalIgnoreCase) ||
         channel.Contains("[WE]", StringComparison.OrdinalIgnoreCase));

    private static double IdentityStartFraction(string text, ChatMessageEnvelope envelope)
    {
        if (!envelope.HasHighConfidencePrefix || string.IsNullOrWhiteSpace(envelope.PlayerOrRole) || text.Length == 0)
            return .02;
        int index = envelope.Prefix?.IndexOf(envelope.PlayerOrRole, StringComparison.Ordinal) ?? -1;
        return Math.Clamp((index < 0 ? 0 : index) / (double)text.Length - .02, .02, .82);
    }

    private static double IdentityEndFraction(string text, ChatMessageEnvelope envelope)
    {
        if (!envelope.HasHighConfidencePrefix || string.IsNullOrWhiteSpace(envelope.Prefix) || text.Length == 0)
            return .52;
        return Math.Clamp(envelope.Prefix.Length / (double)text.Length + .02, .18, .98);
    }

    private static bool TryChampionIdentityFractions(
        string text,
        string? identity,
        int identityOffset,
        out double start,
        out double end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(identity) || identityOffset < 0 || text.Length == 0) return false;
        int open = identity.LastIndexOfAny(['(', '（']);
        int close = identity.LastIndexOfAny([')', '）']);
        if (open < 0 || close <= open + 1) return false;
        int absoluteStart = identityOffset + open + 1;
        int absoluteEnd = identityOffset + close;
        start = Math.Clamp(absoluteStart / (double)text.Length - .04, .02, .90);
        end = Math.Clamp(absoluteEnd / (double)text.Length + .04, start + .04, .98);
        return true;
    }

    private void RefreshContinuousStatus()
    {
        string last = _lastContinuousOcrAt is null ? "尚未完成 OCR" : $"上次 OCR {_lastContinuousOcrAt.Value:HH:mm:ss}";
        ContinuousStatusText.Text = $"监测中 · {last} · 接受显示 {_continuousCoordinator.NewCount} · 信号队列 {_runtimeDispatcher?.FastQueued ?? 0} · 聊天队列 {_runtimeDispatcher?.ModelQueued ?? 0} · 容量拒绝 {_runtimeDispatcher?.OverflowRejectedCount ?? 0} · 未输出 {_continuousCoordinator.FailedCount}";
        UpdateCoverageStatus();
    }

    private void UpdateCoverageStatus()
    {
        TranslationCoverageSnapshot coverage = _coverageTracker.Snapshot;
        CoverageStatusText.Text = coverage.MeasurableDenominator == 0
            ? "画面覆盖率：等待消息样本"
            : $"已接纳事件提交显示 {coverage.DisplayedMessages}/{coverage.QueuedMessages}（{coverage.TranslationSuccessPercent:F0}%） · 未输出 {coverage.FailedMessages} · OCR 候选估计 {coverage.EstimatedMessageCandidates}（不是游戏总消息数）";
    }

    private void RestoreRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ContinuousTranslationRegionStore.TryLoadCompatible(SystemParameters.WorkArea, 1.0, out Rect region))
        {
            RestoreRegionButton.IsEnabled = false;
            ContinuousStatusText.Text = "上次区域与当前屏幕配置不相容，请重新选择区域。";
            return;
        }
        _selectedRegion = region;
        Trace("region", "region_restored", "accepted");
        SelectedRegionText.Text = "已恢复上次聊天区域（连续翻译会自动开始）";
        TestCaptureButton.IsEnabled = true;
        IncrementalGeneralShadowCheckBox.IsChecked = true;
        UpdateIncrementalGeneralSessionState();
        StartContinuousTranslation();
    }

    private void DockSidecarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_translationSidecar is null || _selectedRegion is null) return;
        _translationSidecar.DockNear(_selectedRegion.Value);
        ContinuousStatusText.Text = "译文窗已贴回聊天区域旁。";
    }

    private void AdjustSidecarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_translationSidecar is null) return;
        _sidecarAdjusting = !_sidecarAdjusting;
        _translationSidecar.SetAdjusting(_sidecarAdjusting);
        AdjustSidecarButton.Content = _sidecarAdjusting ? "锁定译文窗位置" : "调整译文窗位置";
        ContinuousStatusText.Text = _sidecarAdjusting ? "可拖动译文窗；完成后点击“锁定译文窗位置”。" : "译文窗位置已锁定。";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_continuousCoordinator.State == ContinuousTranslationState.Running) { StopContinuousTranslation(); return; }
        if (_selectedRegion is null)
        {
            MessageBox.Show("请先选择英雄联盟聊天框区域。", "尚未选择区域", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartContinuousTranslation();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_continuousCoordinator.State == ContinuousTranslationState.Running)
        {
            _continuousCoordinator.Pause(); _continuousTimer.Stop(); _isRunning = false;
            PauseButton.Content = "继续"; StatusText.Text = "已暂停"; ContinuousStatusText.Text = "已暂停；当前区域与去重记录会保留。";
        }
        else if (_continuousCoordinator.State == ContinuousTranslationState.Paused)
        {
            StartContinuousTranslation();
        }
    }

    private async void PlayerFeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PlayerFeedbackWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Submission is null) return;

        PlayerFeedbackButton.IsEnabled = false;
        try
        {
            PlayerFeedbackSubmitResult result = await _playerFeedbackService.SubmitOrQueueAsync(dialog.Submission);
            MessageBox.Show(
                result.SubmittedOnline
                    ? "反馈已发送，感谢你的帮助。"
                    : "反馈已保存在应用的待发送队列。线上服务开放或网络恢复后会自动发送，你不需要处理任何文件。",
                "玩家反馈",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"暂时无法保存反馈：{exception.Message}",
                "玩家反馈",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            PlayerFeedbackButton.IsEnabled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindVisualAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ExitButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        StopContinuousTranslation();
        Close();
    }

    private void OpenTraceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? directory = Path.GetDirectoryName(_runtimeTrace.FilePath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        string root = Directory.GetParent(directory)?.FullName ?? directory;
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{root}\"", UseShellExecute = true });
    }

    private void Trace(
        string stage,
        string eventName,
        string status,
        long? elapsedMilliseconds = null,
        int? detectedLines = null,
        int? visibleMessages = null,
        int? newMessages = null,
        int? queueCount = null,
        int? displayedMessages = null,
        int? failedMessages = null,
        int? estimatedMessageCandidates = null,
        int? queuedMessages = null,
        double? measurableCoveragePercent = null,
        int? estimatedSystemCandidates = null,
        int? queuedSystemMessages = null,
        int? displayedSystemMessages = null,
        int? rejectedUnreliableRows = null,
        int? rejectedNoChatPrefixRows = null,
        int? rejectedEmptyOrSystemRows = null,
        int? rejectedTooShortRows = null,
        int? waitingConfirmationRows = null,
        int? suppressedBulkRealignmentRows = null,
        int? suppressedPreexistingViewportRows = null,
        int? suppressedRecentOcrVariantRows = null,
        int? suppressedSystemContinuationRows = null,
        int? overlayMissingSafePrefixRows = null,
        int? overlayUntrustedPresentationRows = null,
        int? overlayDuplicateRows = null,
        long? oldestMessageAgeMilliseconds = null,
        Exception? exception = null)
    {
        _runtimeTrace.Append(stage, eventName, status, elapsedMilliseconds, detectedLines,
            visibleMessages, newMessages, queueCount, displayedMessages, failedMessages,
            estimatedMessageCandidates, queuedMessages, measurableCoveragePercent,
            estimatedSystemCandidates, queuedSystemMessages, displayedSystemMessages,
            rejectedUnreliableRows, rejectedNoChatPrefixRows, rejectedEmptyOrSystemRows,
            rejectedTooShortRows, waitingConfirmationRows, suppressedBulkRealignmentRows,
            suppressedPreexistingViewportRows, suppressedRecentOcrVariantRows,
            suppressedSystemContinuationRows, overlayMissingSafePrefixRows,
            overlayUntrustedPresentationRows, overlayDuplicateRows,
            oldestMessageAgeMilliseconds, exception);
        if (!IsLoaded) return;
        RuntimeTraceStatusText.Text = $"最近步骤：{stage} / {status}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The control window must never become OCR input when it overlaps a
        // user-selected chat rectangle. Windows then captures the game beneath
        // it instead of creating an application -> watcher feedback loop.
        SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WdaExcludeFromCapture);
    }

    private const uint WdaExcludeFromCapture = 0x00000011;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    protected override void OnClosed(EventArgs e)
    {
        _continuousTimer.Stop();
        ResetTranslationDispatcher();
        _runtimeDispatcher?.Dispose();
        Trace("application", "window_closed", "closed");
        _translationSidecar?.Close();
        ClearBufferedOcrFrames();
        _highAccuracyOcrProvider.Dispose();
        _koreanOcrProvider?.Dispose();
        _continuousSessionCancellation.Dispose();
        base.OnClosed(e);
    }
}
