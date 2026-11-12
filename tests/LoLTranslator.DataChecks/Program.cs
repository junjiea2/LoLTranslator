using LoLTranslator.App;
using SlangGraph.Core;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

string dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
string datasetPath = Path.Combine(dataDirectory, "lol-slang-v0.1.jsonl");
string annotationsPath = Path.Combine(dataDirectory, "annotations", "lol-chat-annotations.v0.1.jsonl");
string inboxPath = Path.Combine(dataDirectory, "inbox", "ocr-text-inbox.example.jsonl");
string graphPackPath = Path.Combine(dataDirectory, "packs", "lol", "slang-graph.v0.2.example.jsonl");
string candidateGraphPackPath = Path.Combine(dataDirectory, "packs", "lol", "slang-graph.v0.2.batch-001.candidate.jsonl");
string candidate2GraphPackPath = Path.Combine(dataDirectory, "packs", "lol", "slang-graph.v0.2.candidate.4.jsonl");
string releaseCandidatePackPath = Path.Combine(dataDirectory, "packs", "lol", "slang-graph.v0.3.0-rc.1.jsonl");
string releasePackPath = Path.Combine(dataDirectory, "packs", "lol", "slang-graph.v0.3.0.jsonl");
string renderingPackPath = Path.Combine(dataDirectory, "packs", "localization", "lol-renderings.v0.1.candidate.1.jsonl");
string renderingRcPackPath = Path.Combine(dataDirectory, "packs", "localization", "lol-renderings.v0.1.0-rc.1.jsonl");
string renderingReleasePackPath = Path.Combine(dataDirectory, "packs", "localization", "lol-renderings.v0.1.0.jsonl");
string releaseHoldoutPath = Path.Combine(dataDirectory, "collection", "lol", "holdout-0.3.0-rc.1.jsonl");
string corePackPath = Path.Combine(dataDirectory, "packs", "core", "communication.v0.2.jsonl");
string softwarePackPath = Path.Combine(dataDirectory, "packs", "software-collaboration", "slang-graph.v0.2.example.jsonl");
string wrongCorePath = Path.Combine(dataDirectory, "fixtures", "core-wrong-version.jsonl");
string cycleAPath = Path.Combine(dataDirectory, "fixtures", "dependency-cycle-a.jsonl");
string cycleBPath = Path.Combine(dataDirectory, "fixtures", "dependency-cycle-b.jsonl");
string genericSlotExtractorPath = Path.Combine(dataDirectory, "fixtures", "generic-slot-extractor.jsonl");
string reviewReleaseReadyPath = Path.Combine(dataDirectory, "fixtures", "review-release-ready.jsonl");
string domainLexiconFixturePath = Path.Combine(dataDirectory, "fixtures", "domain-lexicon.synthetic.v0.1.jsonl");
string candidateLexiconPath = Path.Combine(dataDirectory, "packs", "lol", "domain-lexicon.v0.1.candidate.1.jsonl");
string candidateLexiconMetadataPath = Path.Combine(dataDirectory, "packs", "lol", "domain-lexicon.v0.1.candidate.1.metadata.jsonl");
string candidate2LexiconPath = Path.Combine(dataDirectory, "packs", "lol", "domain-lexicon.v0.1.candidate.2.jsonl");
string championOfficialRc2Path = Path.Combine(dataDirectory, "packs", "lol", "domain-lexicon.v0.1.champion-official.rc.2.jsonl");
string runtimeItemAliasesPath = Path.Combine(dataDirectory, "runtime", "lol-client-item-display-aliases.v0.1.jsonl");

LolSlangCatalog catalog = LolSlangCatalog.LoadFromJsonl(datasetPath);
IReadOnlyList<ChatSampleAnnotation> annotations =
    ChatAnnotationLoader.LoadFromJsonl(annotationsPath, catalog.Dataset);
IReadOnlyList<OcrTextInboxRecord> inboxRecords =
    OcrTextInboxLoader.LoadFromJsonl(inboxPath, catalog.Dataset);
IReadOnlyList<ChatAnnotationCandidate> inboxCandidates =
    OcrTextInboxLoader.CreateCandidates(inboxRecords);
SlangGraphPackSet graphPackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, graphPackPath, softwarePackPath });
SlangGraphPack graphPack = graphPackSet.GetPack("pack.game.lol.chat");
SlangGraphPackSet candidatePackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, candidateGraphPackPath });
SlangGraphPack candidateGraphPack = candidatePackSet.GetPack("pack.game.lol.chat.batch-001");
SlangGraphPackSet candidate2PackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, candidate2GraphPackPath });
SlangGraphPack candidate2GraphPack = candidate2PackSet.GetPack("pack.game.lol.chat");
SlangGraphPackSet releaseCandidatePackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, releaseCandidatePackPath });
SlangGraphPack releaseCandidatePack = releaseCandidatePackSet.GetPack("pack.game.lol.chat");
SlangGraphPackSet releasePackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, releasePackPath });
SlangGraphPack releasePack = releasePackSet.GetPack("pack.game.lol.chat");
SlangGraphPackSet renderingPackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, releasePackPath, renderingPackPath });
SlangGraphPack renderingPack = renderingPackSet.GetPack("pack.game.lol.renderings");
SlangGraphPackSet renderingRcPackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, releasePackPath, renderingRcPackPath });
SlangGraphPack renderingRcPack = renderingRcPackSet.GetPack("pack.game.lol.renderings");
SlangGraphPackSet renderingReleasePackSet = SlangGraphPackSetLoader.Load(new[] { corePackPath, releasePackPath, renderingReleasePackPath });
SlangGraphPack renderingReleasePack = renderingReleasePackSet.GetPack("pack.game.lol.renderings");
IReadOnlyList<ReleaseHoldoutCase> releaseHoldout = LoadReleaseHoldout(releaseHoldoutPath);
var candidateMatcher = new SlangGraphSemanticMatcher(candidatePackSet.CombinedGraph);
var candidate2Matcher = new SlangGraphSemanticMatcher(candidate2PackSet.CombinedGraph);
var releaseCandidateMatcher = new SlangGraphSemanticMatcher(releaseCandidatePackSet.CombinedGraph);
var releaseMatcher = new SlangGraphSemanticMatcher(releasePackSet.CombinedGraph);
SlangGraphPack genericSlotExtractor = SlangGraphJsonl.Load(genericSlotExtractorPath);
SlangGraphPack reviewReleaseReady = SlangGraphJsonl.Load(reviewReleaseReadyPath);
int dependencyFailureChecks = 0;
int genericMatcherChecks = 0;
int crossDomainChecks = 0;
int releaseGateChecks = 0;
int releaseCandidateChecks = 0;
int releaseCandidateIntegrityChecks = 0;
int releaseCandidateHoldoutChecks = 0;
int releasePackChecks = 0;
int candidateRegressionChecks = 0;
int candidate2RegressionChecks = 0;
int supplementalRegressionChecks = 0;
int ocrShadowChecks = 0;
int continuousRuntimeChecks = 0;
int domainLexiconChecks = 0;
int candidateLexiconCompatibilityChecks = 0;
int localizedRenderingChecks = 0;
int localizedRenderingRcChecks = 0;
int localizedRenderingReleaseChecks = 0;
var candidateValidationFailures = new List<string>();
var candidate2ValidationFailures = new List<string>();
var candidateDeferredLabels = new List<string>();
var releaseCandidateIntegrityFailures = new List<string>();
var releaseCandidateHoldoutFailures = new List<string>();
var failedNamedChecks = new List<string>();

var ocrShadowService = OcrShadowSemanticService.LoadFromApplicationDirectory(AppContext.BaseDirectory);
ocrShadowChecks += Check(OcrService.NormalizeRecognizedText("중단 노플") == "중단 노플");
ocrShadowChecks += Check(OcrService.NormalizeRecognizedText("中 单 没 闪") == "中单没闪");
var highAccuracyUiProvider = new PackagedPpOcrV5Provider(
    Path.Combine(Path.GetTempPath(), "LoLTranslator-data-checks", Guid.NewGuid().ToString("N")));
HighAccuracyOcrCapability highAccuracyInitial = highAccuracyUiProvider.Capability;
HighAccuracyOcrCapability highAccuracyUnavailable = highAccuracyUiProvider
    .ProbeAsync(CancellationToken.None)
    .GetAwaiter()
    .GetResult();
ocrShadowChecks += Check(
    highAccuracyInitial.Message == PackagedPpOcrV5Provider.LoadingMessage &&
    highAccuracyUnavailable.State == "model_download_required" &&
    highAccuracyUnavailable.Message == PackagedPpOcrV5Provider.DownloadRequiredMessage &&
    PackagedPpOcrV5Provider.LoadingMessage == "高精度识别模型正在加载…" &&
    PackagedPpOcrV5Provider.ReadyMessage == "高精度识别模型已就绪。" &&
    PackagedPpOcrV5Provider.UnavailableMessage == "此设备暂无法使用高精度识别模型。" &&
    new[]
    {
        PackagedPpOcrV5Provider.LoadingMessage,
        PackagedPpOcrV5Provider.ReadyMessage,
        PackagedPpOcrV5Provider.UnavailableMessage,
        PackagedPpOcrV5Provider.DownloadRequiredMessage
    }.All(message => !message.Contains('\uFFFD')));

string provenanceRoot = Path.Combine(Path.GetTempPath(), "LoLTranslator-provenance-checks", Guid.NewGuid().ToString("N"));
var disabledProvenance = new RuntimeInputProvenanceTrace(provenanceRoot);
disabledProvenance.RecordOcrObservation(1, 1, new System.Windows.Rect(0, 0, 10, 10), null,
    Array.Empty<RuntimeInputEvidenceRow>(), new VisibleMessageDiffResult(Array.Empty<VisibleChatMessage>(), 0));
ocrShadowChecks += Check(!Directory.Exists(provenanceRoot));
var provenance = new RuntimeInputProvenanceTrace(provenanceRoot, TimeSpan.FromDays(1), 8L * 1024 * 1024);
provenance.BeginSession(7);
var provenanceNativeLine = new StructuredOcrLine(
    "00:01 [TEAM] player123 (Ahri): hello", new OcrBoundingBox(4, 5, 90, 12),
    new[] { new StructuredOcrWord("hello", new OcrBoundingBox(70, 5, 24, 12)) }, "en-US", 44);
var provenanceShadowLine = new OcrShadowLineResult(1, provenanceNativeLine.Text,
    Array.Empty<SlangGraphSemanticMatch>(),
    new[] { new OcrShadowLineSource(0, "en-US", 1, provenanceNativeLine.Text, "00:01") },
    new OcrFusionMetadata("fused", .95, new[] { "structured_chat_envelope_anchor" }));
ChatMessageEnvelope provenanceEnvelope = ChatMessageEnvelopeExtractor.Extract(provenanceNativeLine.Text);
var provenanceMessage = new VisibleChatMessage(provenanceShadowLine, "hello", "occ-provenance", "visual", provenanceNativeLine.Bounds, "00:01", DateTimeOffset.UtcNow);
var provenanceDiff = new VisibleMessageDiffResult(new[] { provenanceMessage }, 1, 0,
    new[] { new VisibleMessageDecision(1, provenanceNativeLine.Text, "hello", true, "inserted_new_message", "player") });
IReadOnlyDictionary<string, RuntimeInputCorrelation> provenanceCorrelations = provenance.RecordOcrObservation(
    7, 44, new System.Windows.Rect(1, 2, 100, 40), null,
    new[] { new RuntimeInputEvidenceRow(1, provenanceShadowLine, provenanceNativeLine.Bounds, new[] { provenanceNativeLine }, provenanceEnvelope) },
    provenanceDiff);
var provenanceFailureLine = new IncrementalGeneralChatShadowLine(1, provenanceEnvelope.Prefix, "hello", provenanceShadowLine.Fusion,
    new[] { "en-US" }, "occ-provenance", "general_translation", "pending", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
    Array.Empty<string>(), "hello", null, null, 4, "local_provider_unavailable");
provenance.RecordTranslation(new IncrementalGeneralChatShadowResult("diagnostic", "rc", "semantic", "rendering", "model", "zh-CN",
    new[] { provenanceFailureLine }, 0, 0, 1, 0), provenanceCorrelations);
string provenanceJson = File.ReadAllText(provenance.TraceFilePath!);
ocrShadowChecks += Check(provenanceCorrelations.TryGetValue("occ-provenance", out RuntimeInputCorrelation? provenanceCorrelation) &&
    provenanceJson.Contains(provenanceCorrelation.RowId, StringComparison.Ordinal) &&
    provenanceJson.Contains("raw_text", StringComparison.Ordinal) && provenanceJson.Contains("adopted_text", StringComparison.Ordinal) &&
    provenanceJson.Contains("local_provider_unavailable", StringComparison.Ordinal));
ocrShadowChecks += Check(!Directory.EnumerateFiles(provenance.SessionDirectory!, "*.png", SearchOption.AllDirectories).Any());
ocrShadowChecks += Check(File.Exists(Path.Combine(provenance.SessionDirectory!, "export-redaction-checklist.md")) &&
    provenanceJson.Contains("not_eligible", StringComparison.Ordinal) && provenanceJson.Contains("not_export_ready", StringComparison.Ordinal));

var nativeSidecarUiProvider = new NativePpOcrV5SidecarProvider(enabled: false);
HighAccuracyOcrCapability nativeSidecarDisabled = nativeSidecarUiProvider
    .ProbeAsync(CancellationToken.None)
    .GetAwaiter()
    .GetResult();
ocrShadowChecks += Check(
    nativeSidecarDisabled.State == "disabled" &&
    nativeSidecarDisabled.Message == NativePpOcrV5SidecarProvider.UnavailableMessage &&
    NativePpOcrV5SidecarProvider.LoadingMessage == "高精度识别模型正在加载…" &&
    NativePpOcrV5SidecarProvider.ReadyMessage == "高精度识别模型已就绪。" &&
    NativePpOcrV5SidecarProvider.UnavailableMessage == "此设备暂无法使用高精度识别模型。" &&
    new[]
    {
        NativePpOcrV5SidecarProvider.LoadingMessage,
        NativePpOcrV5SidecarProvider.ReadyMessage,
        NativePpOcrV5SidecarProvider.UnavailableMessage,
        NativePpOcrV5SidecarProvider.DownloadRequiredMessage
    }.All(message => !message.Contains('\uFFFD')));
nativeSidecarUiProvider.Dispose();

var animatedWatcher = new VisualChatChangeWatcher(
    TimeSpan.FromMilliseconds(350),
    differenceThreshold: 2,
    maximumWait: TimeSpan.FromSeconds(1));
DateTimeOffset animationStart = DateTimeOffset.UtcNow;
continuousRuntimeChecks += Check(!animatedWatcher.Observe(new byte[] { 0, 0 }, animationStart).ShouldRunOcr);
continuousRuntimeChecks += Check(!animatedWatcher.Observe(new byte[] { 10, 0 }, animationStart.AddMilliseconds(10)).ShouldRunOcr);
continuousRuntimeChecks += Check(!animatedWatcher.Observe(new byte[] { 20, 0 }, animationStart.AddMilliseconds(400)).ShouldRunOcr);
continuousRuntimeChecks += Check(animatedWatcher.Observe(new byte[] { 30, 0 }, animationStart.AddMilliseconds(1100)) is
    { ShouldRunOcr: true, Status: "forced_capture_after_animation" });

string traceTestPath = Path.Combine(Path.GetTempPath(), "LoLTranslator-data-checks", Guid.NewGuid().ToString("N"), "runtime.jsonl");
var runtimeTrace = new ContinuousRuntimeTrace(traceTestPath, "privacy-test-session");
runtimeTrace.Append("ocr", "recognition_completed", "lines_detected", elapsedMilliseconds: 125, detectedLines: 6,
    estimatedMessageCandidates: 5, queuedMessages: 4, measurableCoveragePercent: 75,
    estimatedSystemCandidates: 3, queuedSystemMessages: 2, displayedSystemMessages: 1,
    rejectedUnreliableRows: 2, rejectedNoChatPrefixRows: 1, waitingConfirmationRows: 3,
    suppressedBulkRealignmentRows: 4, oldestMessageAgeMilliseconds: 987);
runtimeTrace.Append("ocr", "recognition_failed", "retry_waiting",
    exception: new NativePpOcrSidecarException("native_inference_exception"));
string traceJson = File.ReadAllText(traceTestPath);
continuousRuntimeChecks += Check(
    traceJson.Contains("\"stage\":\"ocr\"", StringComparison.Ordinal) &&
    traceJson.Contains("\"detected_lines\":6", StringComparison.Ordinal) &&
    traceJson.Contains("\"estimated_message_candidates\":5", StringComparison.Ordinal) &&
    traceJson.Contains("\"measurable_coverage_percent\":75", StringComparison.Ordinal) &&
    traceJson.Contains("\"estimated_system_candidates\":3", StringComparison.Ordinal) &&
    traceJson.Contains("\"rejected_unreliable_rows\":2", StringComparison.Ordinal) &&
    traceJson.Contains("\"rejected_no_chat_prefix_rows\":1", StringComparison.Ordinal) &&
    traceJson.Contains("\"waiting_confirmation_rows\":3", StringComparison.Ordinal) &&
    traceJson.Contains("\"suppressed_bulk_realignment_rows\":4", StringComparison.Ordinal) &&
    traceJson.Contains("\"oldest_message_age_ms\":987", StringComparison.Ordinal) &&
    traceJson.Contains("\"error_code\":\"native_inference_exception\"", StringComparison.Ordinal) &&
    !traceJson.Contains("raw_text", StringComparison.OrdinalIgnoreCase) &&
    !traceJson.Contains("image_path", StringComparison.OrdinalIgnoreCase) &&
    !traceJson.Contains("player", StringComparison.OrdinalIgnoreCase));

var selectedRegionDiffer = new VisibleMessageSequenceDiffer();
var selectedRegionBody = new OcrShadowLineResult(
    1, "00:10 [TEAM] (Braum): hello from bot lane", Array.Empty<SlangGraphSemanticMatch>(),
    new[] { new OcrShadowLineSource(0, "ppocrv5-cpp", 1, "00:10 [TEAM] (Braum): hello from bot lane", null) },
    new OcrFusionMetadata("single_source", .95, new[] { "selected_chat_region_coordinate_row" }));
RuntimeEventContractChecks.Result runtimeContracts = RuntimeEventContractChecks.Run(selectedRegionBody, ocrShadowService);
foreach (string failure in runtimeContracts.Failures) Console.WriteLine($"RUNTIME CONTRACT FAIL: {failure}");
Console.WriteLine($"Runtime event contract checks: {runtimeContracts.Total - runtimeContracts.Failures.Count}/{runtimeContracts.Total}");
if (runtimeContracts.Failures.Count > 0) return 1;
if (!await RuntimeDispatchChecks.Run(selectedRegionBody, ocrShadowService)) return 1;
if (!RuntimePresentationChecks.Run(selectedRegionBody, ocrShadowService)) return 1;
if (!RuntimeTimingChecks.Run(selectedRegionBody, ocrShadowService)) return 1;
VisibleMessageDiffResult selectedRegionFirst = selectedRegionDiffer.Observe(new[] { selectedRegionBody });
VisibleMessageDiffResult selectedRegionConfirmed = selectedRegionDiffer.Observe(new[] { selectedRegionBody });
VisibleMessageDiffResult selectedRegionRepeat = selectedRegionDiffer.Observe(new[] { selectedRegionBody with { Text = "00:10 [TEAM] (Braum): hello frorn bot lane" } });
continuousRuntimeChecks += Check(selectedRegionFirst is { VisibleCount: 0, PendingConfirmationCount: 1 } && selectedRegionFirst.Inserted.Count == 0);
continuousRuntimeChecks += Check(selectedRegionConfirmed is { VisibleCount: 1 } && selectedRegionConfirmed.Inserted.Count == 1);
continuousRuntimeChecks += Check(selectedRegionRepeat is { VisibleCount: 1 } && selectedRegionRepeat.Inserted.Count == 0);
var immediateTimestampedDiffer = new VisibleMessageSequenceDiffer();
VisibleMessageDiffResult immediateTimestampedRelease = immediateTimestampedDiffer.Observe(
    new[] { selectedRegionBody }, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(immediateTimestampedRelease is
    { VisibleCount: 1, PendingConfirmationCount: 0 } && immediateTimestampedRelease.Inserted.Count == 1);
var immediateKoreanDiffer = new VisibleMessageSequenceDiffer();
OcrShadowLineResult immediateKoreanLine = selectedRegionBody with
{
    Text = "[팀] 플레이어 (아리): 미드로 가고 있어요",
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_source_identity_red", "lol_enemy_identity_row"
        }).ToArray()
    }
};
VisibleMessageDiffResult immediateKoreanRelease = immediateKoreanDiffer.Observe(
    new[] { immediateKoreanLine }, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(immediateKoreanRelease is
    { VisibleCount: 1, PendingConfirmationCount: 0 } &&
    immediateKoreanRelease.Inserted.Single().Line.Text.Contains("미드로", StringComparison.Ordinal));
// A populated viewport present at startup becomes history after the usual
// second-frame confirmation. It must not block the first genuinely new line.
var startupBaselineDiffer = new VisibleMessageSequenceDiffer();
OcrShadowLineResult[] startupHistory = Enumerable.Range(1, 5)
    .Select(index => selectedRegionBody with
    {
        LineNumber = index,
        Text = $"00:0{index} [TEAM] (Braum): historical message {index}"
    })
    .ToArray();
_ = startupBaselineDiffer.Observe(startupHistory, null, suppressInitialBacklog: true);
VisibleMessageDiffResult startupBaseline = startupBaselineDiffer.Observe(
    startupHistory,
    null,
    suppressInitialBacklog: true);
var startupWithNewMessage = startupHistory.Append(selectedRegionBody with
{
    LineNumber = 6,
    Text = "00:06 [TEAM] (Braum): genuinely new message"
}).ToArray();
_ = startupBaselineDiffer.Observe(startupWithNewMessage);
VisibleMessageDiffResult startupNewMessage = startupBaselineDiffer.Observe(startupWithNewMessage);
continuousRuntimeChecks += Check(
    startupBaseline.InitialBaselineEstablished &&
    startupBaseline.Inserted.Count == 0 &&
    startupBaseline.VisibleCount == 5 &&
    startupNewMessage.Inserted.Count == 1 &&
    startupNewMessage.Inserted.Single().Line.Text.Contains("genuinely new message", StringComparison.Ordinal));
var smallStartupDiffer = new VisibleMessageSequenceDiffer();
OcrShadowLineResult[] smallStartup = startupHistory.Take(2).ToArray();
_ = smallStartupDiffer.Observe(smallStartup, null, suppressInitialBacklog: true);
VisibleMessageDiffResult smallStartupConfirmed = smallStartupDiffer.Observe(
    smallStartup,
    null,
    suppressInitialBacklog: true);
continuousRuntimeChecks += Check(
    smallStartupConfirmed.InitialBaselineEstablished && smallStartupConfirmed.Inserted.Count == 0 &&
    smallStartupConfirmed.VisibleCount == 2);
var bulkRealignmentDiffer = new VisibleMessageSequenceDiffer();
_ = bulkRealignmentDiffer.Observe(startupHistory, null, suppressInitialBacklog: true);
_ = bulkRealignmentDiffer.Observe(startupHistory, null, suppressInitialBacklog: true);
OcrShadowLineResult[] bulkRealignmentRows = startupHistory.Select((line, index) => line with
{
    Text = $"00:0{(index == 4 ? 6 : index + 1)} [TEAM] (Ashe): completely changed OCR row {index + 1}"
}).ToArray();
VisibleMessageDiffResult bulkRealignment = bulkRealignmentDiffer.Observe(
    bulkRealignmentRows, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    bulkRealignment.Inserted.Count == 1 && bulkRealignment.Inserted.Single().TimestampKey == "00:06" &&
    bulkRealignment.Decisions!.Count(decision => decision.Decision == "suppressed_bulk_ocr_realignment") == 4);
// The same timestamped message can move vertically when the in-game chat
// scrolls. It must not be emitted again merely because its screen row changed.
var scrolledDiffer = new VisibleMessageSequenceDiffer();
var originalBounds = new Dictionary<int, (string Hash, OcrBoundingBox Bounds)>
{
    [1] = ("row-a", new OcrBoundingBox(10, 200, 260, 20))
};
var movedBounds = new Dictionary<int, (string Hash, OcrBoundingBox Bounds)>
{
    [1] = ("row-b", new OcrBoundingBox(10, 120, 260, 20))
};
_ = scrolledDiffer.Observe(new[] { selectedRegionBody }, originalBounds);
VisibleMessageDiffResult scrolledFirst = scrolledDiffer.Observe(new[] { selectedRegionBody }, originalBounds);
_ = scrolledDiffer.Observe(Array.Empty<OcrShadowLineResult>());
_ = scrolledDiffer.Observe(new[] { selectedRegionBody with { Text = "00:10 [TEAM] (Braum): hello frorn bot lane" } }, movedBounds);
VisibleMessageDiffResult scrolledRepeat = scrolledDiffer.Observe(
    new[] { selectedRegionBody with { Text = "00:10 [TEAM] (Braum): hello from bot lane" } }, movedBounds);
continuousRuntimeChecks += Check(scrolledFirst.Inserted.Count == 1 && scrolledRepeat.Inserted.Count == 0);
var systemLineDiffer = new VisibleMessageSequenceDiffer();
var routeSelectionLine = selectedRegionBody with { Text = "00:20 布郎姆已選擇路線：上路" };
_ = systemLineDiffer.Observe(new[] { routeSelectionLine });
VisibleMessageDiffResult routeSelectionRepeat = systemLineDiffer.Observe(new[] { routeSelectionLine });
continuousRuntimeChecks += Check(
    routeSelectionRepeat.Inserted.Count == 1 &&
    routeSelectionRepeat.VisibleCount == 1 &&
    routeSelectionRepeat.Inserted.Single().Line.Text.Contains("[系统] (游戏)：", StringComparison.Ordinal) &&
    routeSelectionRepeat.Inserted.Single().Line.Text.Contains("已選擇路線：上路", StringComparison.Ordinal));
var partialPrefixDiffer = new VisibleMessageSequenceDiffer();
var partialPrefixLine = selectedRegionBody with { Text = "01:42 [隊伍]" };
_ = partialPrefixDiffer.Observe(new[] { partialPrefixLine });
VisibleMessageDiffResult partialPrefixRepeat = partialPrefixDiffer.Observe(new[] { partialPrefixLine });
continuousRuntimeChecks += Check(partialPrefixRepeat.Inserted.Count == 0 && partialPrefixRepeat.VisibleCount == 0);
var timestampLossDiffer = new VisibleMessageSequenceDiffer();
_ = timestampLossDiffer.Observe(new[] { selectedRegionBody });
VisibleMessageDiffResult timestampedRelease = timestampLossDiffer.Observe(new[] { selectedRegionBody });
var timestampLost = selectedRegionBody with { Text = "[TEAM] (Braum): hello from bot lane" };
_ = timestampLossDiffer.Observe(Array.Empty<OcrShadowLineResult>());
_ = timestampLossDiffer.Observe(new[] { timestampLost });
VisibleMessageDiffResult timestampLossRepeat = timestampLossDiffer.Observe(new[] { timestampLost });
continuousRuntimeChecks += Check(timestampedRelease.Inserted.Count == 1 && timestampLossRepeat.Inserted.Count == 0);
var queueIdentity = new ContinuousTranslationMessageQueue();
VisibleChatMessage queueMessage = timestampedRelease.Inserted.Single();
queueIdentity.Enqueue(new[] { queueMessage, queueMessage with { BodyKey = "changed-metadata-same-event-id" } });
continuousRuntimeChecks += Check(queueIdentity.Count == 1);
var burstQueue = new ContinuousTranslationMessageQueue();
VisibleChatMessage[] burstMessages = Enumerable.Range(1, 4)
    .Select(index => queueMessage with
    {
        OccurrenceKey = $"burst-{index}",
        TimestampKey = $"00:{40 + index:D2}",
        BodyKey = $"burstbody{index}"
    }).ToArray();
burstQueue.Enqueue(burstMessages);
IReadOnlyList<VisibleChatMessage> firstBurstBatch = burstQueue.Take(3, DateTimeOffset.UtcNow);
continuousRuntimeChecks += Check(
    firstBurstBatch.Select(message => message.OccurrenceKey).SequenceEqual(new[] { "burst-1", "burst-2", "burst-3" }) &&
    burstQueue.Count == 1);
continuousRuntimeChecks += Check(
    ContinuousOverlayPresentationPolicy.IsStale(queueMessage with { DetectedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-13) }, DateTimeOffset.UtcNow) &&
    ContinuousOverlayPresentationPolicy.IsStale(queueMessage with { DetectedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5) }, DateTimeOffset.UtcNow) &&
    !ContinuousOverlayPresentationPolicy.IsStale(queueMessage with { DetectedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2) }, DateTimeOffset.UtcNow));
var appendTailDiffer = new VisibleMessageSequenceDiffer();
OcrShadowLineResult tailOld = selectedRegionBody with { LineNumber = 1, Text = "00:40 [TEAM] (Braum): old row" };
OcrShadowLineResult tailAnchor = selectedRegionBody with { LineNumber = 2, Text = "00:41 [TEAM] (Braum): retained row" };
_ = appendTailDiffer.Observe(new[] { tailOld, tailAnchor }, null, allowImmediateTimestampedRelease: true);
VisibleMessageDiffResult appendTailResult = appendTailDiffer.Observe(new[]
{
    tailOld with { Text = "00:40 [TEAM] (Braum): unrelated OCR corruption" },
    tailAnchor,
    selectedRegionBody with { LineNumber = 3, Text = "00:42 [TEAM] (Braum): true bottom append" }
}, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    appendTailResult.Inserted.Count == 1 && appendTailResult.Inserted.Single().Line.Text.Contains("true bottom append", StringComparison.Ordinal) &&
    appendTailResult.Decisions!.Any(decision => decision.Decision == "suppressed_preexisting_viewport_variant"));
var reorderedNewTimestampDiffer = new VisibleMessageSequenceDiffer();
_ = reorderedNewTimestampDiffer.Observe(new[]
{
    tailOld,
    tailAnchor
}, null, allowImmediateTimestampedRelease: true);
VisibleMessageDiffResult reorderedNewTimestampResult = reorderedNewTimestampDiffer.Observe(new[]
{
    selectedRegionBody with { LineNumber = 1, Text = "00:42 [TEAM] (Braum): new timestamp before OCR anchor" },
    tailAnchor
}, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    reorderedNewTimestampResult.Inserted.Count == 1 &&
    reorderedNewTimestampResult.Inserted.Single().Line.Text.Contains("new timestamp before OCR anchor", StringComparison.Ordinal) &&
    reorderedNewTimestampResult.Decisions!.Any(decision => decision.Decision == "inserted_new_message"));
var sameSecondDistinctDiffer = new VisibleMessageSequenceDiffer();
_ = sameSecondDistinctDiffer.Observe(new[]
{
    tailOld,
    tailAnchor
}, null, allowImmediateTimestampedRelease: true);
VisibleMessageDiffResult sameSecondDistinctResult = sameSecondDistinctDiffer.Observe(new[]
{
    selectedRegionBody with { LineNumber = 0, Text = "00:41 [TEAM] (Braum): distinct message in same second" },
    tailAnchor
}, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    sameSecondDistinctResult.Inserted.Count == 1 &&
    sameSecondDistinctResult.Inserted.Single().Line.Text.Contains("distinct message in same second", StringComparison.Ordinal));
continuousRuntimeChecks += Check(ChatMessageEnvelopeExtractor.Extract("OO:34 [TEAM] (Braum): OCR test") is
    { HasHighConfidencePrefix: true, Body: "OCR test" });
ChatMessageEnvelope numericPlayerEnvelope = ChatMessageEnvelopeExtractor.Extract(
    "08:18 [Team] 123456789012345678901234567890 (Sylas): rotate after wave");
ChatMessageEnvelope mixedPlayerEnvelope = ChatMessageEnvelopeExtractor.Extract(
    "08:42 [Team] xX_7G7G7G_|_ABC-99 (Kai'Sa): can we baron");
ChatMessageEnvelope misplacedDelimiterEnvelope = ChatMessageEnvelopeExtractor.Extract(
    "08:43 [Team]：123ABC_xyz (Sylas) where is my translation");
continuousRuntimeChecks += Check(
    numericPlayerEnvelope is
    {
        HasHighConfidencePrefix: true,
        PlayerOrRole: "123456789012345678901234567890 (Sylas)",
        Body: "rotate after wave"
    } &&
    mixedPlayerEnvelope is
    {
        HasHighConfidencePrefix: true,
        PlayerOrRole: "xX_7G7G7G_|_ABC-99 (Kai'Sa)",
        Body: "can we baron"
    } &&
    misplacedDelimiterEnvelope is
    {
        HasHighConfidencePrefix: true,
        RepairedMisplacedDelimiter: true,
        PlayerOrRole: "123ABC_xyz (Sylas)",
        Body: "where is my translation"
    } &&
    misplacedDelimiterEnvelope.Prefix == "08:43 [Team] 123ABC_xyz (Sylas)：" &&
    !ChatMessageEnvelopeExtractor.Extract("release note (draft): do not translate this as chat").HasHighConfidencePrefix);
var privacyPresentationLine = new IncrementalGeneralChatShadowLine(
    1, "08:42 [Team] xX_7G7G7G_|_ABC-99 (Kai'Sa):", "can we baron",
    selectedRegionBody.Fusion, new[] { "en-US" }, "privacy", "general_translation", "pending",
    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "can we baron", null,
    "可以打大龙吗", 3, null, "ally");
var leakedPrivacyLine = privacyPresentationLine with { FinalOutput = "xX_7G7G7G_|_ABC-99 说可以打大龙吗" };
var noChannelSystemPresentationLine = privacyPresentationLine with
{
    ChatPrefix = "08:43 EnemyPlayer (Irelia)：",
    MessageBody = "is on the way",
    FinalOutput = "正在赶来",
    SenderRelation = "enemy"
};
continuousRuntimeChecks += Check(
    LegacySidecarPresentationPolicy.TryCreatePrivacySafePrefix(privacyPresentationLine, out string? safePresentationPrefix) &&
    safePresentationPrefix == "08:42 [Team] Kai'Sa：" &&
    LegacySidecarPresentationPolicy.TryCreatePrivacySafePrefix(noChannelSystemPresentationLine, out string? noChannelSafePrefix) &&
    noChannelSafePrefix == "08:43 Irelia：" &&
    !LegacySidecarPresentationPolicy.TryCreatePrivacySafePrefix(leakedPrivacyLine, out _));
TranslationSidecarDisplayLine privacyPresentation = TranslationSidecarFormatter.Empty.Format(
    safePresentationPrefix, privacyPresentationLine.FinalOutput!, "zh-CN", "ally");
TranslationSidecarDisplayLine enemyPrivacyPresentation = TranslationSidecarFormatter.Empty.Format(
    "08:43 [All] Irelia：", "正在赶来", "zh-CN", "enemy");
continuousRuntimeChecks += Check(
    privacyPresentation.Segments.Any(segment =>
        segment.Role == TranslationSidecarTextRole.Muted && segment.Text.Contains("08:42", StringComparison.Ordinal)) &&
    privacyPresentation.Segments.Any(segment =>
        segment.Role == TranslationSidecarTextRole.AlliedIdentity && segment.Text.Contains("Kai'Sa", StringComparison.Ordinal)) &&
    privacyPresentation.Segments.All(segment => !segment.Text.Contains("xX_7G7G7G_|_ABC-99", StringComparison.Ordinal)) &&
    enemyPrivacyPresentation.Segments.Any(segment =>
        segment.Role == TranslationSidecarTextRole.EnemyIdentity && segment.Text.Contains("Irelia", StringComparison.Ordinal)));
var irregularPlayerDiffer = new VisibleMessageSequenceDiffer();
VisibleMessageDiffResult irregularPlayerRelease = irregularPlayerDiffer.Observe(
    new[]
    {
        selectedRegionBody with { LineNumber = 1, Text = "08:18 [Team] 123456789012345678901234567890 (Sylas): rotate after wave" },
        selectedRegionBody with { LineNumber = 2, Text = "08:42 [Team] xX_7G7G7G_|_ABC-99 (Kai'Sa): can we baron" }
    }, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    irregularPlayerRelease.Inserted.Count == 2 &&
    irregularPlayerRelease.Decisions!.All(decision => decision.Decision == "inserted_new_message"));
continuousRuntimeChecks += Check(
    OcrShadowLineFusion.ExtractTimestamp("I7:30 Objective Bounties are falling off soon") == "17:30" &&
    OcrShadowLineFusion.ExtractTimestamp("(7:52 grookey has shut down Zed") == "07:52");
var noiseDiffer = new VisibleMessageSequenceDiffer();
VisibleMessageDiffResult noiseFirst = noiseDiffer.Observe(new[] { selectedRegionBody with { Text = "random terrain OCR noise" } });
VisibleMessageDiffResult noiseSecond = noiseDiffer.Observe(new[] { selectedRegionBody with { Text = "random terrain OCR noise" } });
continuousRuntimeChecks += Check(noiseFirst is { VisibleCount: 0 } && noiseSecond is { VisibleCount: 0 });

var screenshotSystemDiffer = new VisibleMessageSequenceDiffer();
var assistancePing = selectedRegionBody with
{
    LineNumber = 1,
    Text = "17:22 Exodia (Zeri) is asking for assistance"
};
var objectiveAnnouncement = selectedRegionBody with
{
    LineNumber = 2,
    Text = "17:30 Objective Bounties are falling off soon. Claiming an Objective"
};
var bountyContinuation = selectedRegionBody with
{
    LineNumber = 3,
    Text = "(Bonus Bounty: 501G)",
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[] { "lol_system_gold_row" }).ToArray()
    }
};
_ = screenshotSystemDiffer.Observe(new[] { assistancePing, objectiveAnnouncement, bountyContinuation });
VisibleMessageDiffResult screenshotSystems = screenshotSystemDiffer.Observe(
    new[] { assistancePing, objectiveAnnouncement, bountyContinuation });
continuousRuntimeChecks += Check(
    screenshotSystems.Inserted.Count == 2 &&
    screenshotSystems.Inserted.All(message => message.Line.Fusion.Reasons.Contains("lol_system_notification", StringComparer.Ordinal)) &&
    screenshotSystems.Inserted.Single(message => message.Line.LineNumber == 1).Line.Fusion.Reasons.Contains(
        "lol_system_action_without_delimiter", StringComparer.Ordinal) &&
    ChatMessageEnvelopeExtractor.Extract(screenshotSystems.Inserted.Single(message => message.Line.LineNumber == 1).Line.Text) is
        { Body: "is asking for assistance", PlayerOrRole: not null } &&
    screenshotSystems.Inserted.Single(message => message.Line.LineNumber == 2).Line.Text.Contains("[系统] (游戏)：", StringComparison.Ordinal));
continuousRuntimeChecks += Check(
    screenshotSystems.Decisions?.Single(decision => decision.LineNumber == 3).Decision ==
        "suppressed_system_continuation" &&
    screenshotSystems.Inserted.All(message => message.Line.LineNumber != 3));
var systemEnvelopeDiffer = new VisibleMessageSequenceDiffer();
var spellPing = selectedRegionBody with { Text = "17:45 MUDAI (Kai'Sa): Jayce used Ignite" };
_ = systemEnvelopeDiffer.Observe(new[] { spellPing });
VisibleMessageDiffResult spellPingConfirmed = systemEnvelopeDiffer.Observe(new[] { spellPing });
continuousRuntimeChecks += Check(
    spellPingConfirmed.Inserted.Single().Line.Text == spellPing.Text &&
    spellPingConfirmed.Inserted.Single().Line.Fusion.Reasons.Contains("lol_system_notification", StringComparer.Ordinal) &&
    !spellPingConfirmed.Inserted.Single().Line.Fusion.Reasons.Contains(
        "lol_system_action_without_delimiter", StringComparer.Ordinal) &&
    spellPingConfirmed.Inserted.Single().Line.Text.Contains("Jayce used Ignite", StringComparison.Ordinal));
var targetedPingDiffer = new VisibleMessageSequenceDiffer();
var targetedPingLine = selectedRegionBody with { Text = "03:09 Bolemtin (Singed) has targeted Thebausffs - (Irelia)" };
_ = targetedPingDiffer.Observe(new[] { targetedPingLine });
VisibleMessageDiffResult targetedPingConfirmed = targetedPingDiffer.Observe(new[] { targetedPingLine });
continuousRuntimeChecks += Check(
    targetedPingConfirmed.Inserted.Single() is
    {
        Line.Text: "03:09 [系统] Bolemtin (Singed)：has targeted Thebausffs - (Irelia)",
        Line.Fusion.Reasons: var reasons
    } && reasons.Contains("lol_system_notification", StringComparer.Ordinal));
// One visual Ping may first be read as a complete structured row and later as
// a damaged OCR variant. The second observation must not enter translation.
var pingVariantDiffer = new VisibleMessageSequenceDiffer();
var completeMissingPing = selectedRegionBody with
{
    Text = "04:55 Sylas (Sylas) signals that enemies are missing"
};
VisibleMessageDiffResult completeMissingRelease = pingVariantDiffer.Observe(
    new[] { completeMissingPing }, null, allowImmediateTimestampedRelease: true);
_ = pingVariantDiffer.Observe(Array.Empty<OcrShadowLineResult>());
var damagedMissingVariant = completeMissingPing with
{
    Text = "04:55 Sylas signals that enernies are missing"
};
VisibleMessageDiffResult damagedMissingRelease = pingVariantDiffer.Observe(
    new[] { damagedMissingVariant }, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    completeMissingRelease.Inserted.Count == 1 &&
    damagedMissingRelease.Inserted.Count == 0 &&
    damagedMissingRelease.Decisions!.Any(decision => decision.Decision == "suppressed_recent_ocr_variant"));
var ordinaryPlayerDiffer = new VisibleMessageSequenceDiffer();
var ordinaryPlayerLine = selectedRegionBody with { Text = "00:28 [All] (Ambessa): what did you have for dinner today" };
_ = ordinaryPlayerDiffer.Observe(new[] { ordinaryPlayerLine });
VisibleMessageDiffResult ordinaryPlayerConfirmed = ordinaryPlayerDiffer.Observe(new[] { ordinaryPlayerLine });
continuousRuntimeChecks += Check(
    ordinaryPlayerConfirmed.Inserted.Single().Line.Text == ordinaryPlayerLine.Text &&
    ordinaryPlayerConfirmed.Decisions!.Any(decision => decision.MessageCategory == "player"));
IReadOnlyList<CoordinateLogicalOcrLine> mixedColorPhysicalRows = CoordinateOcrLineFusion.Fuse(new[]
{
    new StructuredOcrLine("17:22", new OcrBoundingBox(10, 20, 45, 18), Array.Empty<StructuredOcrWord>(), "ppocrv5-cpp", 7),
    new StructuredOcrLine("Exodia (Zeri)", new OcrBoundingBox(58, 19, 110, 20), Array.Empty<StructuredOcrWord>(), "ppocrv5-cpp", 7),
    new StructuredOcrLine("is asking for assistance", new OcrBoundingBox(172, 21, 180, 18), Array.Empty<StructuredOcrWord>(), "ppocrv5-cpp", 7),
    new StructuredOcrLine("17:30 Objective Bounties", new OcrBoundingBox(10, 50, 220, 18), Array.Empty<StructuredOcrWord>(), "ppocrv5-cpp", 7)
});
continuousRuntimeChecks += Check(
    mixedColorPhysicalRows.Count == 2 &&
    mixedColorPhysicalRows[0].Preferred.Text == "17:22 Exodia (Zeri) is asking for assistance" &&
    mixedColorPhysicalRows[1].Preferred.Text == "17:30 Objective Bounties");

// The active runtime ledger observes only the latest viewport. It must retain
// startup history as identities, emit each later typed event once, and never
// replay an older viewport after a newer one has already been observed.
var eventLedger = new RuntimeChatEventLedger();
OcrShadowLineResult ledgerHistory = selectedRegionBody with
{
    LineNumber = 1,
    Text = "04:40 Exodia (Zeri) signals to be cautious"
};
VisibleMessageDiffResult ledgerBaseline = eventLedger.Observe(
    new[] { ledgerHistory }, null, suppressInitialBacklog: true, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    ledgerBaseline.InitialBaselineEstablished && ledgerBaseline.Inserted.Count == 0);
OcrShadowLineResult ledgerPlayer = selectedRegionBody with
{
    LineNumber = 2,
    Text = "04:41 [Team] Player 77 (Zeri): rotate after wave"
};
OcrShadowLineResult ledgerPing = selectedRegionBody with
{
    LineNumber = 3,
    Text = "04:42 Exodia (Zeri) signals that enemies are missing"
};
OcrShadowLineResult ledgerSystem = selectedRegionBody with
{
    LineNumber = 4,
    Text = "04:43 Jinx has disconnected",
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[] { "lol_system_gold_row" }).ToArray()
    }
};
VisibleMessageDiffResult ledgerLive = eventLedger.Observe(
    new[] { ledgerHistory, ledgerPlayer, ledgerPing, ledgerSystem }, null,
    allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    ledgerLive.Inserted.Count == 3 &&
    ledgerLive.Decisions!.Any(item => item.MessageCategory == "player_text" && item.Decision == "inserted_typed_event") &&
    ledgerLive.Inserted.Single(item => item.TimestampKey == "04:41").EventKind == RuntimeChatEventKind.PlayerText &&
    ledgerLive.Inserted.Single(item => item.TimestampKey == "04:42") is { EventKind: RuntimeChatEventKind.Ping } pingEvent &&
    pingEvent.Line.RuntimeEvent?.Kind == RuntimeChatEventKind.Ping &&
    ledgerLive.Inserted.Single(item => item.TimestampKey == "04:43") is { EventKind: RuntimeChatEventKind.System } systemEvent &&
    systemEvent.Line.RuntimeEvent?.Kind == RuntimeChatEventKind.System);
VisibleMessageDiffResult ledgerSameViewport = eventLedger.Observe(
    new[] { ledgerHistory, ledgerPlayer, ledgerPing, ledgerSystem }, null,
    allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(ledgerSameViewport.Inserted.Count == 0);
VisibleMessageDiffResult ledgerOlderViewport = eventLedger.Observe(
    new[] { ledgerHistory, ledgerPlayer }, null, allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(ledgerOlderViewport.Inserted.Count == 0);
VisibleMessageDiffResult ledgerPingOcrVariant = eventLedger.Observe(
    new[] { ledgerPing with { Text = "04:42 Exodia (Zeri) signals that enernies are rnissing" } }, null,
    allowImmediateTimestampedRelease: true);
continuousRuntimeChecks += Check(
    ledgerPingOcrVariant.Inserted.Count == 0 &&
    ledgerPingOcrVariant.Decisions!.Any(item => item.Decision == "suppressed_event_ledger_duplicate"));
continuousRuntimeChecks += Check(
    RuntimeStructuredEventRenderer.TryRenderPing("signals that enemies are missing", "zh-CN", out string missingRendering) &&
    missingRendering == "敌人消失" &&
    RuntimeStructuredEventRenderer.TryRenderPing("is on the way", "ko-KR", out string onWayRendering) &&
    onWayRendering == "가는 중" &&
    !RuntimeStructuredEventRenderer.TryRenderPing("find an AI model to evaluate the role", "zh-CN", out _));
var noEvictionQueue = new ContinuousTranslationMessageQueue();
noEvictionQueue.Enqueue(Enumerable.Range(0, 100).Select(index => queueMessage with
{
    OccurrenceKey = $"no-eviction-{index}",
    TimestampKey = $"{index / 60:D2}:{index % 60:D2}",
    BodyKey = $"unique{index}"
}));
continuousRuntimeChecks += Check(noEvictionQueue.Count == 100);

var coverageTracker = new TranslationCoverageTracker();
coverageTracker.ObserveFrame(selectedRegionFirst);
coverageTracker.ObserveFrame(selectedRegionConfirmed);
coverageTracker.ObserveFrame(selectedRegionRepeat);
coverageTracker.ObserveFrame(routeSelectionRepeat);
coverageTracker.ObserveFrame(new VisibleMessageDiffResult(
    Array.Empty<VisibleChatMessage>(),
    0,
    Decisions: new[] { new VisibleMessageDecision(1, "random terrain OCR noise", string.Empty, false, "rejected_no_chat_prefix") }));
TranslationCoverageSnapshot observedCoverage = coverageTracker.Snapshot;
continuousRuntimeChecks += Check(observedCoverage is
    { EstimatedMessageCandidates: 2, QueuedMessages: 2, DisplayedMessages: 0, FailedMessages: 0,
      EstimatedSystemCandidates: 1, QueuedSystemMessages: 1 });
VisibleChatMessage selectedCoverageMessage = selectedRegionConfirmed.Inserted.Single();
VisibleChatMessage systemCoverageMessage = routeSelectionRepeat.Inserted.Single();
var coverageResult = new IncrementalGeneralChatShadowResult(
    "coverage-test", "rc", "semantic", "rendering", "model", "zh-CN",
    new[]
    {
        new IncrementalGeneralChatShadowLine(
            1, "00:10 [TEAM] (Braum):", "hello from bot lane", selectedRegionBody.Fusion,
            new[] { "en-US" }, selectedCoverageMessage.OccurrenceKey, "general_translation", "displayed_new",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            null, null, "你好，下路", 0, null),
        new IncrementalGeneralChatShadowLine(
            2, "00:20 [系统] (游戏)：", "布郎姆已選擇路線：上路", routeSelectionLine.Fusion,
            new[] { "zh-Hans-CN" }, systemCoverageMessage.OccurrenceKey, "general_translation", "fail_closed",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            null, null, null, 0, "controlled_failure")
    },
    2, 0, 1, 1);
coverageTracker.RecordTranslation(coverageResult);
TranslationCoverageSnapshot completedCoverage = coverageTracker.Snapshot;
continuousRuntimeChecks += Check(
    completedCoverage is { EstimatedMessageCandidates: 2, QueuedMessages: 2, DisplayedMessages: 1, FailedMessages: 1 } &&
    completedCoverage.DisplayedSystemMessages == 0 && completedCoverage.FailedSystemMessages == 1 &&
    Math.Abs(completedCoverage.MeasurableCoveragePercent - 50d) < .01 &&
    Math.Abs(completedCoverage.TranslationSuccessPercent - 50d) < .01);

IReadOnlyList<OcrShadowLineResult> careShadow = ocrShadowService.Match(
    "top care",
    "en-US",
    new ShadowMatchContextOption("test", "lol.global", "in_game_chat"));
ocrShadowChecks += Check(careShadow.SelectMany(line => line.Matches).Any(match =>
    match.ConceptId == "game.lol:lane.caution"));
ocrShadowChecks += Check(careShadow.SelectMany(line => line.Matches).Any(match =>
    SlotValueIs(match, "subject", "game.lol:lane.top")));

IReadOnlyList<OcrShadowLineResult> noFShadow = ocrShadowService.Match(
    "mid no F",
    "en-US",
    new ShadowMatchContextOption("test", "lol.eu_or_global", "in_game_chat"));
ocrShadowChecks += Check(noFShadow.SelectMany(line => line.Matches).Any(match =>
    match.ConceptId == "game.lol:summoner.flash.unavailable"));
ocrShadowChecks += Check(!ocrShadowService.Match(
        "Press F",
        "en-US",
        new ShadowMatchContextOption("test", "non_lol", "other"))
    .SelectMany(line => line.Matches)
    .Any());
ocrShadowChecks += Check(!ocrShadowService.Match(
        "gank",
        "en-US",
        new ShadowMatchContextOption("test", "lol.global", "in_game_chat"))
    .SelectMany(line => line.Matches)
    .Any());
ocrShadowChecks += Check(!ocrShadowService.Match(
        "no F",
        "en-US",
        new ShadowMatchContextOption("test", null, null))
    .SelectMany(line => line.Matches)
    .Any());
IReadOnlyList<OcrShadowLineResult> mixedPublishedContext = ocrShadowService.Match(
    "top care\nmid no F",
    OcrShadowSemanticService.AutoSemanticLocale,
    new ShadowMatchContextOption(
        "test",
        "lol.global",
        "in_game_chat",
        new[] { "lol.global", "lol.eu_or_global" }));
ocrShadowChecks += Check(mixedPublishedContext
    .SelectMany(line => line.Matches)
    .Select(match => match.ConceptId)
    .ToHashSet(StringComparer.OrdinalIgnoreCase)
    .IsSupersetOf(new[]
    {
        "game.lol:lane.caution",
        "game.lol:summoner.flash.unavailable"
    }));
string localizedShadowDisplay = ocrShadowService.FormatDisplayText(
    careShadow,
    "en-US",
    "en-US",
    new ShadowMatchContextOption("test", "lol.global", "in_game_chat"),
    "zh-CN");
ocrShadowChecks += Check(
    localizedShadowDisplay.Contains("输出: 上路小心", StringComparison.Ordinal) &&
    localizedShadowDisplay.Contains("输出 locale: zh-CN", StringComparison.Ordinal));
ocrShadowChecks += Check(
    localizedShadowDisplay.Contains("证据链:", StringComparison.Ordinal) &&
    localizedShadowDisplay.Contains("evidence_record=", StringComparison.Ordinal));
var shadowFeedback = ShadowRuntimeFeedbackStore.CreateRecord(
    new ShadowRuntimeFeedbackDraft(
        "capture-test-001",
        new[] { new OcrRecognitionResult("top care", "en-US") },
        "en-US",
        "zh-CN",
        new ShadowMatchContextOption("test", "lol.global", "in_game_chat"),
        careShadow),
    "expected_behavior",
    "controlled_test",
    "test-only");
ocrShadowChecks += Check(
    shadowFeedback.PackSha256 == OcrShadowSemanticService.ExpectedPackSha256 &&
    shadowFeedback.RedactionStatus == "unknown" &&
    shadowFeedback.IntakeStatus == "new" &&
    shadowFeedback.Matches.Any(match =>
        match.ConceptId == "game.lol:lane.caution" &&
        match.EvidenceIds.Count > 0 &&
        match.OcrSources.Single().RecognizerLanguageTag == "direct-input"));

IReadOnlyList<OcrShadowLineResult> fusedShadow = ocrShadowService.MatchCandidates(
    new[]
    {
        new OcrRecognitionResult(
            "00:22\ntop care\n00:27 top no f\n00:45 need mid gank",
            "en-US"),
        new OcrRecognitionResult(
            "00:22 [WE] player: t0P care\n00:27 [WE] player: top nof\n00:45 [WE] player: need mid gank\n01:01 我没有看到交闪",
            "zh-Hans-CN"),
        new OcrRecognitionResult("top care", "ja-JP")
    },
    OcrShadowSemanticService.AutoSemanticLocale,
    new ShadowMatchContextOption(
        "test",
        "lol.global",
        "in_game_chat",
        new[] { "lol.global", "lol.eu_or_global" }));
OcrShadowLineResult? fusedCare = fusedShadow.FirstOrDefault(line =>
    line.Matches.Any(match => match.ConceptId == "game.lol:lane.caution"));
OcrShadowLineResult? fusedNoF = fusedShadow.FirstOrDefault(line =>
    line.Matches.Any(match => match.ConceptId == "game.lol:summoner.flash.unavailable"));
ocrShadowChecks += Check(
    fusedShadow.Count == 4 &&
    fusedCare?.Sources.Count == 3 &&
    fusedNoF?.Sources.Count == 2 &&
    fusedShadow.Count(line => line.Text.Contains("gank", StringComparison.OrdinalIgnoreCase)) == 1 &&
    fusedShadow.Any(line => line.Text.Contains("我没有看到交闪", StringComparison.Ordinal)) &&
    fusedShadow.First(line => line.Text.Contains("gank", StringComparison.OrdinalIgnoreCase)).Matches.Count == 0);

ShadowMatchContextOption fusionContext = new(
    "test",
    "lol.global",
    "in_game_chat",
    new[] { "lol.global", "lol.eu_or_global" });
IReadOnlyList<OcrShadowLineResult> normalFusion = ocrShadowService.MatchCandidates(
    new[]
    {
        new OcrRecognitionResult("00:22 top care", "en-US"),
        new OcrRecognitionResult("00:22 top care", "fr-FR")
    },
    OcrShadowSemanticService.AutoSemanticLocale,
    fusionContext);
ocrShadowChecks += Check(
    normalFusion.Count == 1 &&
    normalFusion[0].Fusion.Status == "fused" &&
    normalFusion[0].Fusion.Confidence >= 0.95 &&
    normalFusion[0].Sources.Count == 2 &&
    normalFusion[0].Matches.Any(match => match.ConceptId == "game.lol:lane.caution"));

IReadOnlyList<OcrShadowLineResult> sameTimestampConflict = ocrShadowService.MatchCandidates(
    new[]
    {
        new OcrRecognitionResult("00:55 top care", "en-US"),
        new OcrRecognitionResult("00:55 rotate dragon", "fr-FR")
    },
    OcrShadowSemanticService.AutoSemanticLocale,
    fusionContext);
ocrShadowChecks += Check(
    sameTimestampConflict.Count == 2 &&
    sameTimestampConflict.All(line => !line.Fusion.IsAmbiguous) &&
    sameTimestampConflict.Any(line => line.Text.Contains("top care", StringComparison.OrdinalIgnoreCase) &&
        line.Matches.Any(match => match.ConceptId == "game.lol:lane.caution")) &&
    sameTimestampConflict.Any(line => line.Text.Contains("rotate dragon", StringComparison.OrdinalIgnoreCase)));

IReadOnlyList<OcrShadowLineResult> unanchoredBoundary = ocrShadowService.MatchCandidates(
    new[]
    {
        new OcrRecognitionResult("top care", "en-US"),
        new OcrRecognitionResult("need mid gank", "fr-FR")
    },
    OcrShadowSemanticService.AutoSemanticLocale,
    fusionContext);
ocrShadowChecks += Check(
    unanchoredBoundary.Count == 2 &&
    unanchoredBoundary.All(line => line.Fusion.IsAmbiguous &&
        line.Fusion.Reasons.Contains("unanchored_no_timestamp")) &&
    unanchoredBoundary.Single(line => line.Text.Contains("gank", StringComparison.OrdinalIgnoreCase)).Matches.Count == 0);

IReadOnlyList<OcrShadowLineResult> repeatedTimestampMessages = ocrShadowService.MatchCandidates(
    new[]
    {
        new OcrRecognitionResult("00:10 no f\n00:20 no f", "en-US"),
        new OcrRecognitionResult("00:10 no f\n00:20 no f", "fr-FR")
    },
    OcrShadowSemanticService.AutoSemanticLocale,
    fusionContext);
ocrShadowChecks += Check(
    repeatedTimestampMessages.Count == 2 &&
    repeatedTimestampMessages.All(line =>
        line.Fusion.Status == "fused" &&
        line.Sources.Count == 2 &&
        line.Matches.Any(match => match.ConceptId == "game.lol:summoner.flash.unavailable")));

var ambiguousFeedback = ShadowRuntimeFeedbackStore.CreateRecord(
    new ShadowRuntimeFeedbackDraft(
        "capture-conflict-001",
        new[]
        {
            new OcrRecognitionResult("top care", "en-US"),
            new OcrRecognitionResult("rotate dragon", "fr-FR")
        },
        OcrShadowSemanticService.AutoSemanticLocale,
        "zh-CN",
        fusionContext,
        unanchoredBoundary),
    "fusion_ambiguous",
    "controlled_test",
    "test-only");
ocrShadowChecks += Check(
    ambiguousFeedback.FusionAmbiguityCount == 2 &&
    ambiguousFeedback.FusedLines.All(line => line.FusionReasons.Contains("unanchored_no_timestamp")) &&
    ambiguousFeedback.Matches.All(match => match.FusionStatus == "ambiguous"));

ShadowRuntimeFeedbackRecord preflightComplete = shadowFeedback with
{
    RedactionStatus = "redacted"
};
ShadowRuntimeFeedbackRecord preflightMissingHash = preflightComplete with
{
    PackSha256 = string.Empty
};
ShadowRuntimeFeedbackRecord preflightAbsolutePath = preflightComplete with
{
    CaptureReference = @"C:\\Users\\local-test\\capture.png"
};
ShadowRuntimeFeedbackRecord preflightUnknownOrigin = preflightComplete with
{
    RecordOrigin = "unknown",
    FixtureStatus = "unknown",
    FormalEligibility = "unknown"
};
ShadowRuntimeFeedbackExportManifest feedbackPreflight = ShadowRuntimeFeedbackPreflight.InspectRecords(
    "shadow-feedback-regression.jsonl",
    new[]
    {
        preflightComplete,
        preflightMissingHash,
        preflightAbsolutePath,
        preflightUnknownOrigin,
        ambiguousFeedback
    });
ocrShadowChecks += Check(
    feedbackPreflight.ExportReadyCount == 2 &&
    feedbackPreflight.BlockedCount == 3 &&
    feedbackPreflight.Entries[0].Disposition == "export_ready" &&
    feedbackPreflight.Entries[0].Checklist.Contains("published_semantic_and_rendering_pack_identity_verified") &&
    feedbackPreflight.Entries[0].Checklist.Contains("manifest_only_not_observation_or_evidence"));
ocrShadowChecks += Check(
    feedbackPreflight.Entries[1].Disposition == "blocked" &&
    feedbackPreflight.Entries[1].BlockingReasons.Contains("published_pack_identity_mismatch_or_missing_hash"));
ocrShadowChecks += Check(
    feedbackPreflight.Entries[2].Disposition == "blocked" &&
    feedbackPreflight.Entries[2].BlockingReasons.Contains("absolute_path_or_local_location_detected"));
ocrShadowChecks += Check(
    feedbackPreflight.Entries[3].Disposition == "blocked" &&
    feedbackPreflight.Entries[3].BlockingReasons.Contains("unknown_record_origin"));
ocrShadowChecks += Check(
    feedbackPreflight.Entries[4].Disposition == "export_ready" &&
    feedbackPreflight.Entries[4].FormalEligibility == "not_eligible" &&
    feedbackPreflight.Entries[4].Checklist.Contains("fusion_ambiguous_retained"));
string feedbackPreflightTestDirectory = Path.Combine(
    Path.GetTempPath(),
    $"loltranslator-feedback-preflight-{Guid.NewGuid():N}");
Directory.CreateDirectory(feedbackPreflightTestDirectory);
string feedbackJsonlPath = Path.Combine(feedbackPreflightTestDirectory, "feedback.jsonl");
string manifestPath = Path.Combine(feedbackPreflightTestDirectory, "manifest.json");
try
{
    File.WriteAllText(
        feedbackJsonlPath,
        JsonSerializer.Serialize(preflightComplete) + Environment.NewLine +
        JsonSerializer.Serialize(preflightAbsolutePath) + Environment.NewLine);
    ShadowRuntimeFeedbackExportManifest filePreflight =
        ShadowRuntimeFeedbackPreflight.InspectFile(feedbackJsonlPath);
    ShadowRuntimeFeedbackPreflight.WriteManifest(filePreflight, manifestPath);
    string manifestJson = File.ReadAllText(manifestPath);
    ocrShadowChecks += Check(
        filePreflight.ExportReadyCount == 1 &&
        filePreflight.BlockedCount == 1 &&
        !manifestJson.Contains("top care", StringComparison.OrdinalIgnoreCase) &&
        !manifestJson.Contains("capture-test-001", StringComparison.OrdinalIgnoreCase) &&
        manifestJson.Contains("absolute_path_or_local_location_detected", StringComparison.Ordinal));
}
finally
{
    if (Directory.Exists(feedbackPreflightTestDirectory))
    {
        Directory.Delete(feedbackPreflightTestDirectory, recursive: true);
    }
}

ShadowMatchContextOption runtimeRouteContext = new(
    "runtime-route-test",
    "lol.global",
    "in_game_chat",
    new[] { "lol.global", "lol.eu_or_global" });
ocrShadowChecks += Check(!ocrShadowService.Match(
        "I care about the timing",
        "en-US",
        runtimeRouteContext)
    .SelectMany(line => line.Matches)
    .Any(match => match.ConceptId == "game.lol:lane.caution"));
ocrShadowChecks += Check(!ocrShadowService.Match(
        "I care about winning",
        "en-US",
        runtimeRouteContext)
    .SelectMany(line => line.Matches)
    .Any(match => match.ConceptId == "game.lol:lane.caution"));
ocrShadowChecks += Check(!ocrShadowService.Match(
        "care about bot lane",
        "en-US",
        runtimeRouteContext)
    .SelectMany(line => line.Matches)
    .Any(match => match.ConceptId == "game.lol:lane.caution"));
ocrShadowChecks += Check(ocrShadowService.Match(
        "top care",
        "en-US",
        runtimeRouteContext)
    .SelectMany(line => line.Matches)
    .Any(match => match.ConceptId == "game.lol:lane.caution"));
ocrShadowChecks += Check(ocrShadowService.Match(
        "bot care",
        "en-US",
        runtimeRouteContext)
    .SelectMany(line => line.Matches)
    .Any(match => match.ConceptId == "game.lol:lane.caution"));
ocrShadowChecks += Check(ocrShadowService.Match(
        "care",
        "en-US",
        runtimeRouteContext)
    .SelectMany(line => line.Matches)
    .Any(match => match.ConceptId == "game.lol:lane.caution"));

var routeOrchestrator = new ChatProcessingRouteOrchestrator(ocrShadowService);
ChatProcessingResult generalUnavailable = routeOrchestrator.Process(
    new ChatProcessingRequest("need mid gank", "en-US", "zh-CN", runtimeRouteContext));
ocrShadowChecks += Check(
    generalUnavailable.ProcessingRoute == ChatProcessingRoutes.GeneralTranslation &&
    generalUnavailable.SemanticState == ChatProcessingRoutes.SemanticNoMatch &&
    generalUnavailable.Status == "general_translation_unavailable" &&
    generalUnavailable.GeneralTranslation?.FailureReason == "general_translation_provider_unavailable" &&
    generalUnavailable.OutputText is null);
ChatProcessingResult directSemantic = routeOrchestrator.Process(
    new ChatProcessingRequest("top care", "en-US", "zh-CN", runtimeRouteContext));
ocrShadowChecks += Check(
    directSemantic.ProcessingRoute == ChatProcessingRoutes.SemanticRendering &&
    directSemantic.SemanticState == ChatProcessingRoutes.SemanticMatchFound &&
    directSemantic.OutputText == "上路小心" &&
    directSemantic.GeneralTranslation is null &&
    directSemantic.HumanReview.RequiresReview == false);
ChatProcessingResult hybridUnavailable = routeOrchestrator.Process(
    new ChatProcessingRequest("top no f, I can repeat gank", "en-US", "zh-CN", runtimeRouteContext));
ocrShadowChecks += Check(
    hybridUnavailable.ProcessingRoute == ChatProcessingRoutes.Hybrid &&
    hybridUnavailable.SemanticState == ChatProcessingRoutes.SemanticMatchFound &&
    hybridUnavailable.Status == "hybrid_unavailable" &&
    hybridUnavailable.GeneralTranslation?.Status == "unavailable" &&
    hybridUnavailable.HybridAggregation?.FailureReason == "hybrid_requires_general_translation_provider" &&
    hybridUnavailable.OutputText is null);
ChatProcessingResult guardedHumanReview = routeOrchestrator.Process(
    new ChatProcessingRequest("adc no f, but support may have heal", "en-US", "zh-CN", runtimeRouteContext));
ocrShadowChecks += Check(
    guardedHumanReview.ProcessingRoute == ChatProcessingRoutes.HumanReview &&
    guardedHumanReview.SemanticState == ChatProcessingRoutes.SemanticMatchFound &&
    guardedHumanReview.HumanReview.Reason == "semantic_fragment_with_contrast_and_uncertainty" &&
    guardedHumanReview.OutputText is null &&
    guardedHumanReview.GeneralTranslation is null &&
    guardedHumanReview.SemanticMatches.Any(match => match.ConceptId == "game.lol:summoner.flash.unavailable"));
ChatProcessingResult policyHybrid = routeOrchestrator.Process(
    new ChatProcessingRequest(
        "bot care, jungle may path down",
        "en-US",
        "zh-CN",
        runtimeRouteContext,
        new ChatProcessingPolicy(
            ChatProcessingRoutes.Hybrid,
            new ChatSemanticPolicy("required", new[] { "game.lol:lane.caution" }, Array.Empty<string>()))));
ocrShadowChecks += Check(
    policyHybrid.ProcessingRoute == ChatProcessingRoutes.Hybrid &&
    policyHybrid.HumanReview.RequiresReview == false &&
    policyHybrid.SemanticPolicy?.IsSatisfied == true &&
    policyHybrid.HybridAggregation?.Status == "unavailable");
ChatProcessingResult policyForbidden = routeOrchestrator.Process(
    new ChatProcessingRequest(
        "I care about winning",
        "en-US",
        "zh-CN",
        runtimeRouteContext,
        new ChatProcessingPolicy(
            ChatProcessingRoutes.GeneralTranslation,
            new ChatSemanticPolicy("forbidden", Array.Empty<string>(), Array.Empty<string>()))));
ocrShadowChecks += Check(
    policyForbidden.ProcessingRoute == ChatProcessingRoutes.GeneralTranslation &&
    policyForbidden.SemanticPolicy?.IsSatisfied == true &&
    policyForbidden.SemanticPolicy?.SuppressedFormalMatches == true &&
    policyForbidden.ObservedSemanticMatches.Count == 0 &&
    policyForbidden.SemanticMatches.Count == 0);
ChatProcessingResult policyHumanReview = routeOrchestrator.Process(
    new ChatProcessingRequest(
        "need mid gank",
        "en-US",
        "zh-CN",
        runtimeRouteContext,
        new ChatProcessingPolicy(
            ChatProcessingRoutes.HumanReview,
            new ChatSemanticPolicy("candidate_only", Array.Empty<string>(), Array.Empty<string>()))));
ocrShadowChecks += Check(
    policyHumanReview.ProcessingRoute == ChatProcessingRoutes.HumanReview &&
    policyHumanReview.HumanReview.Reason == "policy_required_human_review" &&
    policyHumanReview.OutputText is null &&
    policyHumanReview.SemanticPolicy?.IsSatisfied == true);

var translateGemmaProfile = new OllamaTranslationProfile(
    "test.translategemma",
    "translategemma:4b",
    "translategemma-official-direct-v1",
    "test-digest-a",
    DisableThinking: false,
    TemplateKind: "translategemma_direct");
var qwenProfile = new OllamaTranslationProfile(
    "test.qwen3",
    "qwen3:8b",
    "qwen3-non-thinking-strict-zh-hans-v1",
    "test-digest-b",
    DisableThinking: true,
    TemplateKind: "qwen3_non_thinking");
ControlledShadowRuntimeDependencies disabledControlledShadow = ControlledShadowQwen3RuntimeFactory.Create();
ocrShadowChecks += Check(
    !disabledControlledShadow.IsEnabled &&
    disabledControlledShadow.Activation == "disabled_by_default" &&
    disabledControlledShadow.GeneralTranslationProvider is UnavailableGeneralTranslationProvider &&
    disabledControlledShadow.HybridTranslationAggregator is UnavailableHybridTranslationAggregator);
ControlledShadowRuntimeDependencies enabledControlledShadow = ControlledShadowQwen3RuntimeFactory.Create(
    ControlledShadowQwen3Profile.ExplicitTestOnly());
ocrShadowChecks += Check(
    enabledControlledShadow.IsEnabled &&
    enabledControlledShadow.Activation == "explicit_controlled_shadow_test_only" &&
    enabledControlledShadow.GeneralTranslationProvider is OllamaLocalTranslationProvider &&
    enabledControlledShadow.HybridTranslationAggregator is ProtectedHybridTranslationAggregator &&
    ((OllamaLocalTranslationProvider)enabledControlledShadow.GeneralTranslationProvider).Profile.ModelTag == "qwen3:8b" &&
    ((OllamaLocalTranslationProvider)enabledControlledShadow.GeneralTranslationProvider).Profile.DisableThinking);
var templateProvider = new OllamaLocalTranslationProvider(translateGemmaProfile);
var qwenTemplateProvider = new OllamaLocalTranslationProvider(qwenProfile);
ocrShadowChecks += Check(templateProvider.BuildPrompt(new GeneralTranslationRequest("wait for me", "en-US", "zh-CN")) ==
    "Please translate the following English text into Simplified Chinese:\nwait for me");
ocrShadowChecks += Check(qwenProfile.DisableThinking &&
    qwenTemplateProvider.BuildPrompt(new GeneralTranslationRequest("don't fight", "en-US", "zh-CN")).Contains(
        "Output only the translation. Do not explain or add information.",
        StringComparison.Ordinal) &&
    qwenTemplateProvider.BuildPrompt(new GeneralTranslationRequest("don't fight", "en-US", "zh-CN")).Contains(
        "Preserve player and user names verbatim",
        StringComparison.Ordinal));
var qwenRequestHandler = new StaticHttpMessageHandler(HttpStatusCode.OK, "{\"response\":\"正常译文\"}");
var sharedPromptHandler = new StaticHttpMessageHandler(HttpStatusCode.OK,
    "{\"response\":\"{\\\"items\\\":[{\\\"id\\\":\\\"one\\\",\\\"translation\\\":\\\"之前\\\"}]}\"}");
var sharedPromptProvider = new OllamaLocalTranslationProvider(qwenProfile,
    new HttpClient(sharedPromptHandler) { BaseAddress = new Uri("http://127.0.0.1:11434/") });
_ = sharedPromptProvider.TranslateBatchWithDiagnostics(new[] { new BatchTranslationItem("one", "before u have", "en-US", "zh-CN") });
using (var sentPrompt = JsonDocument.Parse(sharedPromptHandler.LastRequestBody!))
    ocrShadowChecks += Check(sentPrompt.RootElement.GetProperty("prompt").GetString()!.Contains(TranslationOutputQuality.GameInstruction) &&
        qwenTemplateProvider.BuildPrompt(new GeneralTranslationRequest("before u have", "en-US", "zh-CN")).Contains(TranslationOutputQuality.GameInstruction));
var qwenSafetyProvider = new OllamaLocalTranslationProvider(
    qwenProfile,
    new HttpClient(qwenRequestHandler) { BaseAddress = new Uri("http://127.0.0.1:11434/") });
GeneralTranslationResult qwenSafetyResult = qwenSafetyProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    qwenSafetyResult.IsAvailable &&
    qwenRequestHandler.LastRequestBody?.Contains("\"think\":false", StringComparison.Ordinal) == true);
string reviewedTermsPath = Path.Combine(dataDirectory, "runtime", "lol-reviewed-runtime-terms.v0.1.jsonl");
ReviewedRuntimeTermResolver reviewedTerms = ReviewedRuntimeTermResolver.Load(reviewedTermsPath);
string officialTrilingualTermsPath = Path.Combine(dataDirectory, "runtime",
    "official-trilingual-runtime-terms.v0.1.jsonl");
string officialTrilingualAmbiguityPath = Path.Combine(dataDirectory, "runtime",
    "official-trilingual-runtime-term-ambiguities.v0.1.jsonl");
OfficialTrilingualRuntimeTerms officialTrilingualTerms =
    OfficialTrilingualRuntimeTerms.LoadOfficialSourceApproved(officialTrilingualTermsPath, officialTrilingualAmbiguityPath);
IReadOnlyList<OfficialTrilingualRuntimeTermMatch> controlWardTerms =
    officialTrilingualTerms.MatchExact("buy Control Ward", new[] { "en-US" }, "zh-CN");
IReadOnlyList<OfficialTrilingualRuntimeTermMatch> koreanAhriTerms =
    officialTrilingualTerms.MatchExact("아리 미드", new[] { "ko-KR" }, "zh-CN");
IReadOnlyList<OfficialTrilingualRuntimeTermMatch> protectedPlayerTerms =
    officialTrilingualTerms.MatchExact("Ahri (Singed) has targeted Irelia", new[] { "en-US" }, "zh-CN");
string? renderedCassiopeiaPrefix = officialTrilingualTerms.RenderChampionSlotsExact(
    "15:51 [Team] Player Name (Cassiopeia):", new[] { "en-US" }, "zh-CN");
ocrShadowChecks += Check(controlWardTerms.Single() is
    { CanonicalId: "game.lol.official:item.2055", TargetText: "控制守卫" });
ocrShadowChecks += Check(koreanAhriTerms.Single() is
    { CanonicalId: "game.lol.official:champion.ahri", TargetText: "九尾妖狐" });
ocrShadowChecks += Check(
    protectedPlayerTerms.All(term => !string.Equals(term.SourceText, "Ahri", StringComparison.OrdinalIgnoreCase)) &&
    protectedPlayerTerms.Any(term => term is { SourceText: "Singed", TargetText: "炼金术士" }) &&
    protectedPlayerTerms.Any(term => term is { SourceText: "Irelia", TargetText: "刀锋舞者" }) &&
    renderedCassiopeiaPrefix == "15:51 [Team] Player Name (魔蛇之拥):");
ocrShadowChecks += Check(
    officialTrilingualTerms.MatchExact("Hex Dragon", new[] { "en-US" }, "zh-CN").Count == 0 &&
    officialTrilingualTerms.ContainsBlockedAmbiguousSurface("we should take Hex Dragon"));
ocrShadowChecks += Check(officialTrilingualTerms.MatchExact("Ahri123", new[] { "en-US" }, "zh-CN").Count == 0);
ocrShadowChecks += Check(reviewedTerms.TryResolveExact("freeze this wave", "en-US", "zh-CN", out ReviewedRuntimeTermResolution? freezeTerm) &&
    freezeTerm is { Concept: "wave.freeze", Rendering: "控住这波兵线" });
ocrShadowChecks += Check(reviewedTerms.TryResolveExact("is on the way", "en-US", "zh-CN", out ReviewedRuntimeTermResolution? onWayTerm) &&
    onWayTerm is { Concept: "ping.on_the_way", Rendering: "正在路上" });
ocrShadowChecks += Check(reviewedTerms.TryResolveExact("님이 적이 사라졌다고 알림", "ko-KR", "zh-CN", out ReviewedRuntimeTermResolution? koreanMissingTerm) &&
    koreanMissingTerm is { Concept: "ping.enemy_missing", Rendering: "发出敌人消失信号" });
ocrShadowChecks += Check(!reviewedTerms.TryResolveExact("I'm freezing in my room", "en-US", "zh-CN", out _));
ocrShadowChecks += Check(
    reviewedTerms.TryResolveExact("base", "en-US", "zh-CN", out ReviewedRuntimeTermResolution? baseTerm) &&
    baseTerm is { Concept: "recall", Rendering: "回城" } &&
    reviewedTerms.TryResolveExact("(cautious)", "en-US", "zh-CN", out ReviewedRuntimeTermResolution? shortCautionTerm) &&
    shortCautionTerm is { Concept: "ping.caution", Rendering: "小心！" } &&
    reviewedTerms.TryResolveExact("void grubs", "en-US", "zh-CN", out ReviewedRuntimeTermResolution? voidGrubsTerm) &&
    voidGrubsTerm is { Concept: "objective.void_grubs", Rendering: "虚空巢虫" });
TranslationSidecarFormatter sidecarFormatter = TranslationSidecarFormatter.TryLoadOfficialItemDisplayCandidate(candidate2LexiconPath, runtimeItemAliasesPath);
TranslationSidecarDisplayLine alliedItemDisplay = sidecarFormatter.Format(
    "00:12 [TEAM] (Braum):", "我买了无尽之刃", "zh-CN");
ocrShadowChecks += Check(
    string.Concat(alliedItemDisplay.Segments.Select(segment => segment.Text)) == "00:12 [队伍] Braum：我买了无尽之刃" &&
    alliedItemDisplay.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.Muted && segment.Text.Contains("00:12", StringComparison.Ordinal)) &&
    alliedItemDisplay.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.AlliedIdentity && segment.Text.Contains("Braum", StringComparison.Ordinal)) &&
    alliedItemDisplay.Segments.Any(segment => segment is { Role: TranslationSidecarTextRole.Item, Text: "无尽之刃" }));
TranslationSidecarDisplayLine unknownAllChatDisplay = sidecarFormatter.Format(
    "00:13 [All] (Player):", "你好", "zh-CN");
TranslationSidecarDisplayLine verifiedEnemyDisplay = sidecarFormatter.Format(
    "00:13 [All] (Player):", "你好", "zh-CN", "enemy");
TranslationSidecarDisplayLine sourceGoldDisplay = sidecarFormatter.Format(
    "00:14 [System] Objective:", "赏金即将结束", "zh-CN", "gold");
ocrShadowChecks += Check(
    unknownAllChatDisplay.Segments.All(segment => segment.Role != TranslationSidecarTextRole.EnemyIdentity) &&
    verifiedEnemyDisplay.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.EnemyIdentity) &&
    sourceGoldDisplay.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.System) &&
    sourceGoldDisplay.Segments.Where(segment => !segment.Text.Contains("00:14", StringComparison.Ordinal))
        .All(segment => segment.Role == TranslationSidecarTextRole.System));
TranslationSidecarDisplayLine teamPresentationGuard = sidecarFormatter.Format(
    "15:51 [Team] Bolemtin (Singed):", "那太傻了", "zh-CN", "enemy");
OfficialGameEntityOcrRepair ocrRepair = OfficialGameEntityOcrRepair.Load(championOfficialRc2Path, candidate2LexiconPath);
ChatMessageEnvelope repairedPrefix = ocrRepair.Repair(
    ChatMessageEnvelopeExtractor.Extract("15:51 [Team] Bolemtin (Sinqed): that was goofy"));
ChatMessageEnvelope repairedTarget = ocrRepair.Repair(
    ChatMessageEnvelopeExtractor.Extract("03:09 [系统] Bolemtin (Singed)：has targeted Thebausffs - (Ireha)"));
ChatMessageEnvelope repairedItem = ocrRepair.Repair(
    ChatMessageEnvelopeExtractor.Extract("00:12 [Team] Bolemtin (Singed): Contorl Ward - 75 gold needed"));
ChatMessageEnvelope preservedUserName = ocrRepair.Repair(
    ChatMessageEnvelopeExtractor.Extract("15:51 [Team] Sinqed (Jax): that was goofy"));
ChatMessageEnvelope repairedRiven = ocrRepair.Repair(
    ChatMessageEnvelopeExtractor.Extract("15:52 [All] Opponent (Reven): nm"));
ocrShadowChecks += Check(teamPresentationGuard.Segments.Any(segment => segment is
    { Role: TranslationSidecarTextRole.AlliedIdentity, Text: var text } &&
    text.Contains("Singed", StringComparison.Ordinal) && !text.Contains("Bolemtin", StringComparison.Ordinal)));
ocrShadowChecks += Check(repairedPrefix.Prefix?.Contains("Bolemtin (Singed)", StringComparison.Ordinal) == true);
ocrShadowChecks += Check(repairedTarget.Body.Contains("Thebausffs - (Irelia)", StringComparison.Ordinal));
ocrShadowChecks += Check(repairedItem.Body.StartsWith("Control Ward - 75 gold", StringComparison.Ordinal));
ocrShadowChecks += Check(preservedUserName.Prefix?.Contains("Sinqed (Jax)", StringComparison.Ordinal) == true);
ocrShadowChecks += Check(
    repairedRiven.Prefix?.Contains("Opponent (Riven)", StringComparison.Ordinal) == true &&
    repairedRiven.PlayerOrRole?.Contains("Opponent (Riven)", StringComparison.Ordinal) == true &&
    RuntimeChatOcrTextRepair.Repair(repairedRiven).Body == "nvm" &&
    RuntimeChatOcrTextRepair.RepairBody("go mid") == "go mid");
TranslationSidecarDisplayLine alliedSystemDisplay = sidecarFormatter.Format(
    "20:08 [系统] Petricite (Locke)：", "发出敌人消失信号", "zh-CN", "ally");
ocrShadowChecks += Check(
    !string.Concat(alliedSystemDisplay.Segments.Select(segment => segment.Text)).Contains("[系统]", StringComparison.Ordinal) &&
    alliedSystemDisplay.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.AlliedIdentity &&
        segment.Text.Contains("Locke", StringComparison.Ordinal) && !segment.Text.Contains("Petricite", StringComparison.Ordinal)));
TranslationSidecarDisplayLine actionPingWithoutDelimiter = sidecarFormatter.Format(
    "09:05 [系统] little buFFoon (Azir)：", "正在路上", "zh-CN", "ally", suppressDelimiter: true);
TranslationSidecarDisplayLine objectPingWithDelimiter = sidecarFormatter.Format(
    "09:29 [系统] blaberfish2 (Lee Sin)：", "辛德拉使用了 R - 能量倾泻", "zh-CN", "ally");
ocrShadowChecks += Check(
    !string.Concat(actionPingWithoutDelimiter.Segments.Select(segment => segment.Text)).Contains(
        "(Azir)：", StringComparison.Ordinal) &&
    !string.Concat(actionPingWithoutDelimiter.Segments.Select(segment => segment.Text)).Contains(
        "(Azir):", StringComparison.Ordinal) &&
    string.Concat(actionPingWithoutDelimiter.Segments.Select(segment => segment.Text)).Contains(
        "Azir 正在路上", StringComparison.Ordinal) &&
    string.Concat(objectPingWithDelimiter.Segments.Select(segment => segment.Text)).Contains(
        "Lee Sin：辛德拉使用了 R", StringComparison.Ordinal));
TranslationSidecarDisplayLine opposingIdentityDisplay = sidecarFormatter.Format(
    "17:52 [系统] grookey (Lee Sin)：", "终结了 Minh Trinh Korea (Zed)", "zh-CN", "ally",
    new[] { "Minh Trinh Korea (Zed)" });
ocrShadowChecks += Check(
    opposingIdentityDisplay.Segments.Any(segment => segment is
        { Role: TranslationSidecarTextRole.EnemyIdentity, Text: "Zed" }) &&
    !string.Concat(opposingIdentityDisplay.Segments.Select(segment => segment.Text))
        .Contains("Minh Trinh Korea", StringComparison.Ordinal));
ocrShadowChecks += Check(
    new ChatRowVisualAnalysis("a", 100, 60, 2, 1, 0) is { DominantTextColor: "blue", IdentityRelation: "ally" } &&
    new ChatRowVisualAnalysis("b", 100, 60, 1, 2, 0) is { DominantTextColor: "red", IdentityRelation: "enemy" } &&
    new ChatRowVisualAnalysis("c", 100, 60, 0, 0, 3) is { DominantTextColor: "gold", IdentityRelation: null } &&
    new ChatRowVisualAnalysis("warm-enemy", 100, 20, 0, 1, 4).DominantIdentityColor == "red" &&
    new ChatRowVisualAnalysis("d", 100, 4, 0, 0, 0) is { DominantTextColor: "white", IdentityRelation: null } &&
    new ChatRowVisualAnalysis("e", 100, 60, 3, 3, 0) is { DominantTextColor: null });
using var compressedRedBitmap = new System.Drawing.Bitmap(8, 8);
using var compressedGoldBitmap = new System.Drawing.Bitmap(8, 8);
using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(compressedRedBitmap))
    graphics.Clear(System.Drawing.Color.FromArgb(120, 95, 80));
using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(compressedGoldBitmap))
    graphics.Clear(System.Drawing.Color.FromArgb(130, 95, 70));
ChatRowVisualAnalysis compressedRedVisual = ScreenCaptureService.ComputeRowVisualAnalysis(
    compressedRedBitmap, new OcrBoundingBox(0, 0, 8, 8));
ChatRowVisualAnalysis compressedGoldVisual = ScreenCaptureService.ComputeRowVisualAnalysis(
    compressedGoldBitmap, new OcrBoundingBox(0, 0, 8, 8));
ocrShadowChecks += Check(
    compressedRedVisual.DominantIdentityColor == "red" &&
    compressedGoldVisual.DominantTextColor == "gold");
var identityColorMemory = new SourceIdentityColorMemory();
ocrShadowChecks += Check(
    identityColorMemory.Resolve("Bolemtin (Singed)", "blue", false) == "ally" &&
    identityColorMemory.Resolve("B0lemtin (Singed)", "white", false) == "ally" &&
    identityColorMemory.Resolve("Any OCR Name (Singed)", null, false) == "ally" &&
    identityColorMemory.Resolve("Thebausffs (Reven)", "red", false) == "enemy" &&
    identityColorMemory.Resolve("Damaged Name (Riven)", "white", false) == "enemy" &&
    identityColorMemory.Resolve("Unknown (Jax)", "white", true) == "ally");
var teamChannelWithFalseRedVisual = selectedRegionBody with
{
    Text = "00:14 [TEAM] (Braum): Zed used Ignite",
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_source_identity_red", "lol_enemy_identity_row"
        }).ToArray()
    }
};
var teamChannelPriorityProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService teamChannelPriorityService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: teamChannelPriorityProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
IncrementalGeneralChatShadowResult teamChannelPriorityResult = teamChannelPriorityService.Process(
    new[] { teamChannelWithFalseRedVisual }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [teamChannelWithFalseRedVisual.LineNumber] = "team-channel-priority" });
ocrShadowChecks += Check(teamChannelPriorityResult.Lines.Single().SenderRelation == "ally");
var rememberedAllyWithNeutralFrame = selectedRegionBody with
{
    Text = "00:15 Bolemtin (Singed): 你好",
    Sources = new[] { new OcrShadowLineSource(0, "zh-CN", 1, "00:15 Bolemtin (Singed): 你好", "00:15") },
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_source_identity_white", "lol_allied_identity_row", "lol_source_identity_color_memory"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowService rememberedAllyService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: new RecordingLocalTranslationProvider(), reviewedRuntimeTermsPath: reviewedTermsPath);
IncrementalGeneralChatShadowLine rememberedAllyOutput = rememberedAllyService.Process(
    new[] { rememberedAllyWithNeutralFrame }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [rememberedAllyWithNeutralFrame.LineNumber] = "remembered-ally-neutral-frame" }).Lines.Single();
ocrShadowChecks += Check(rememberedAllyOutput.SenderRelation == "ally");
OfficialItemPingResolver itemPingResolver = OfficialItemPingResolver.Load(candidate2LexiconPath, runtimeItemAliasesPath);
ocrShadowChecks += Check(
    itemPingResolver.TryResolve("飞翼月甲 - 需要37金币", new[] { "zh-Hans-CN" }, "zh-CN", out OfficialItemPingResolution? missingGoldPing) &&
    missingGoldPing is { State: "missing_gold", MissingGold: 37, Rendering: "飞翼月甲 — 还需要 37 金币" });
ocrShadowChecks += Check(
    itemPingResolver.TryResolve("红水晶 - 可以购买", new[] { "zh-Hans-CN" }, "zh-CN", out OfficialItemPingResolution? readyPing) &&
    readyPing is { State: "purchasable_now", Rendering: "红水晶 — 可以购买" });
ocrShadowChecks += Check(!itemPingResolver.TryResolve(
    "I need 37 gold", new[] { "en-US" }, "zh-CN", out _));
TranslationSidecarDisplayLine itemPingDisplay = sidecarFormatter.Format(
    "03:13 神龙大侠（布朗姆）：", "飞翼月甲 — 还需要 37 金币", "zh-CN", "ally");
ocrShadowChecks += Check(
    itemPingDisplay.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.AlliedIdentity) &&
    itemPingDisplay.Segments.Any(segment => segment is { Role: TranslationSidecarTextRole.Item, Text: "飞翼月甲" }) &&
    itemPingDisplay.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.Normal && segment.Text.Contains("37 金币", StringComparison.Ordinal)));
TranslationSidecarDisplayLine untrustedWhiteDisplay = sidecarFormatter.Format(
    "01:01 Unknown Player (Sylas):", "错误地翻译了玩家名称", "zh-CN", "white");
var duplicateGuard = new TranslationSidecarDuplicateGuard(TimeSpan.FromMinutes(1));
DateTimeOffset duplicateNow = DateTimeOffset.UtcNow;
IncrementalGeneralChatShadowLine trustedDisplayedLine = new(
    1, "00:12 [TEAM] (Braum):", "where is my translation", selectedRegionBody.Fusion,
    new[] { "en-US" }, "trusted-1", "general_translation", "displayed_new", Array.Empty<string>(),
    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, null, "我的翻译在哪里", 0, null);
TranslationSidecarDisplayLine trustedPlayerDisplay = sidecarFormatter.Format(
    trustedDisplayedLine.ChatPrefix, trustedDisplayedLine.FinalOutput!, "zh-CN", "ally");
ocrShadowChecks += Check(
    !LegacySidecarPresentationPolicy.HasTrustedPresentation(
        trustedDisplayedLine with { ChatPrefix = "01:01 Unknown Player (Sylas):", SenderRelation = "white" },
        untrustedWhiteDisplay) &&
    LegacySidecarPresentationPolicy.HasTrustedPresentation(
        trustedDisplayedLine with { SenderRelation = "ally" }, trustedPlayerDisplay) &&
    LegacySidecarPresentationPolicy.HasTrustedPresentation(
        trustedDisplayedLine with { Route = "official_item_ping_runtime" }, itemPingDisplay));
var irregularPlayerOutput = trustedDisplayedLine with
{
    ChatPrefix = "08:42 [Team] xX_7G7G7G_|_ABC-99 (Kai'Sa):",
    MessageBody = "can we baron",
    FinalOutput = "可以打纳什男爵吗？",
    SenderRelation = "ally"
};
TranslationSidecarDisplayLine irregularPlayerDisplay = sidecarFormatter.Format(
    irregularPlayerOutput.ChatPrefix,
    irregularPlayerOutput.FinalOutput!,
    "zh-CN",
    irregularPlayerOutput.SenderRelation);
ocrShadowChecks += Check(
    LegacySidecarPresentationPolicy.HasTrustedPresentation(irregularPlayerOutput, irregularPlayerDisplay) &&
    !string.Concat(irregularPlayerDisplay.Segments.Select(segment => segment.Text))
        .Contains("xX_7G7G7G_|_ABC-99", StringComparison.Ordinal) &&
    string.Concat(irregularPlayerDisplay.Segments.Select(segment => segment.Text))
        .Contains("Kai'Sa", StringComparison.Ordinal));
var misplacedDelimiterProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService misplacedDelimiterService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: misplacedDelimiterProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult misplacedDelimiterLine = selectedRegionBody with
{
    Text = "08:43 [Team]：123ABC_xyz (Sylas) where is my translation",
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "ocr_misplaced_chat_delimiter_repaired", "lol_source_identity_blue"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowLine misplacedDelimiterOutput = misplacedDelimiterService.Process(
    new[] { misplacedDelimiterLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [misplacedDelimiterLine.LineNumber] = "misplaced-delimiter-occurrence" }).Lines.Single();
TranslationSidecarDisplayLine misplacedDelimiterDisplay = sidecarFormatter.Format(
    misplacedDelimiterOutput.ChatPrefix,
    misplacedDelimiterOutput.FinalOutput!,
    "zh-CN",
    misplacedDelimiterOutput.SenderRelation);
ocrShadowChecks += Check(
    misplacedDelimiterProvider.LastRequest?.Text == "where is my translation" &&
    misplacedDelimiterOutput.ChatPrefix?.Contains("123ABC_xyz (解脱者)", StringComparison.Ordinal) == true &&
    misplacedDelimiterOutput.MessageBody == "where is my translation" &&
    misplacedDelimiterOutput.FinalOutput == "where is my translation，继续。" &&
    misplacedDelimiterOutput.SenderRelation == "ally" &&
    misplacedDelimiterDisplay.Segments.Any(segment =>
        segment.Role == TranslationSidecarTextRole.AlliedIdentity &&
        segment.Text.Contains("解脱者", StringComparison.Ordinal) &&
        !segment.Text.Contains("123ABC_xyz", StringComparison.Ordinal)));
IncrementalGeneralChatShadowLine damagedRepeatedLine = trustedDisplayedLine with
{
    LineNumber = 2,
    ChatPrefix = "00:12 Braum:",
    MessageBody = "where is my transiation",
    SessionFingerprint = "damaged-repeat"
};
IncrementalGeneralChatShadowLine trustedPlayerRepeat = trustedDisplayedLine with
{
    LineNumber = 3,
    ChatPrefix = "00:13 [TEAM] (Braum):",
    SessionFingerprint = "trusted-2"
};
ocrShadowChecks += Check(
    duplicateGuard.ShouldDisplay(trustedDisplayedLine, duplicateNow) &&
    !duplicateGuard.ShouldDisplay(damagedRepeatedLine, duplicateNow.AddSeconds(3)) &&
    duplicateGuard.ShouldDisplay(trustedPlayerRepeat, duplicateNow.AddSeconds(5)));
var structuredMissingPing = trustedDisplayedLine with
{
    ChatPrefix = "04:55 [系统] Sylas (Sylas)：",
    MessageBody = "signals that enemies are missing",
    FinalOutput = "敌人消失！",
    Route = "reviewed_runtime_term",
    SessionFingerprint = "missing-structured",
    SenderRelation = "ally"
};
var damagedMissingPing = structuredMissingPing with
{
    ChatPrefix = null,
    MessageBody = "04:55 Sylas (Sylas) signals that enernies are missing",
    FinalOutput = "塞拉斯表示能量不见了",
    Route = "general_translation",
    SessionFingerprint = "missing-damaged",
    SenderRelation = null
};
var newerStructuredMessage = trustedDisplayedLine with
{
    ChatPrefix = "08:45 [系统] Sylas (Sylas)：",
    MessageBody = "has slain an enemy",
    FinalOutput = "击杀了一名敌人",
    SessionFingerprint = "newer-structured",
    SenderRelation = "ally"
};
var staleWhiteMessage = trustedDisplayedLine with
{
    ChatPrefix = "08:30 Sylas:",
    MessageBody = "old damaged message",
    FinalOutput = "迟到的旧消息",
    SessionFingerprint = "stale-white",
    SenderRelation = null
};
var qualityGuard = new TranslationSidecarDuplicateGuard(TimeSpan.FromMinutes(1));
ocrShadowChecks += Check(
    qualityGuard.ShouldDisplay(structuredMissingPing, duplicateNow) &&
    !qualityGuard.ShouldDisplay(damagedMissingPing, duplicateNow.AddSeconds(2)) &&
    qualityGuard.ShouldDisplay(newerStructuredMessage, duplicateNow.AddSeconds(4)) &&
    !qualityGuard.ShouldDisplay(staleWhiteMessage, duplicateNow.AddSeconds(6)));
VisibleChatMessage chronologicalOlder = queueMessage with { TimestampKey = "08:30", OccurrenceKey = "chrono-old" };
VisibleChatMessage chronologicalNewer = queueMessage with { TimestampKey = "08:45", OccurrenceKey = "chrono-new" };
var chronologicalQueue = new ContinuousTranslationMessageQueue();
chronologicalQueue.Enqueue(new[] { chronologicalNewer, chronologicalOlder });
IReadOnlyList<VisibleChatMessage> chronologicalFirst = chronologicalQueue.Take(1, duplicateNow);
var inFlightSupersedeQueue = new ContinuousTranslationMessageQueue();
inFlightSupersedeQueue.Enqueue(new[] { chronologicalOlder });
IReadOnlyList<VisibleChatMessage> olderInFlight = inFlightSupersedeQueue.Take(1, duplicateNow);
inFlightSupersedeQueue.Enqueue(new[] { chronologicalNewer });
var timestampRegressionQueue = new ContinuousTranslationMessageQueue();
timestampRegressionQueue.Enqueue(new[] { chronologicalNewer });
IReadOnlyList<VisibleChatMessage> completedNewer = timestampRegressionQueue.Take(1, duplicateNow);
var completedNewerResult = new IncrementalGeneralChatShadowResult(
    "queue-timestamp-regression", "rc", "semantic", "rendering", "model", "zh-CN",
    new[] { trustedDisplayedLine with { SessionFingerprint = "chrono-new" } },
    1, 0, 0, 1);
_ = timestampRegressionQueue.Complete(completedNewer, completedNewerResult, duplicateNow);
timestampRegressionQueue.Enqueue(new[] { chronologicalOlder });
continuousRuntimeChecks += Check(
    chronologicalFirst.Count == 1 && chronologicalFirst.Single().TimestampKey == "08:45" &&
    timestampRegressionQueue.Count == 1 &&
    timestampRegressionQueue.Take(1, duplicateNow).Single().TimestampKey == "08:30");
continuousRuntimeChecks += Check(inFlightSupersedeQueue.HasNewerPendingThan(olderInFlight));
var pingDuplicateGuard = new TranslationSidecarDuplicateGuard(TimeSpan.FromMinutes(1));
IncrementalGeneralChatShadowLine firstOnWayPing = trustedDisplayedLine with
{
    ChatPrefix = "21:02 [系统] Sheiden (Jax)：",
    MessageBody = "is on the way",
    FinalOutput = "正在路上",
    SessionFingerprint = "ping-1",
    SenderRelation = "ally"
};
IncrementalGeneralChatShadowLine repeatedOnWayPing = firstOnWayPing with
{
    ChatPrefix = "21:06 [系统] Sheiden (Jax)：",
    SessionFingerprint = "ping-2"
};
IncrementalGeneralChatShadowLine otherPlayerOnWayPing = firstOnWayPing with
{
    ChatPrefix = "21:07 [系统] Jayce (Jayce)：",
    SessionFingerprint = "ping-3"
};
ocrShadowChecks += Check(
    pingDuplicateGuard.ShouldDisplay(firstOnWayPing, duplicateNow) &&
    !pingDuplicateGuard.ShouldDisplay(repeatedOnWayPing, duplicateNow.AddSeconds(4)) &&
    pingDuplicateGuard.ShouldDisplay(otherPlayerOnWayPing, duplicateNow.AddSeconds(5)) &&
    pingDuplicateGuard.ShouldDisplay(repeatedOnWayPing with { SessionFingerprint = "ping-4" }, duplicateNow.AddSeconds(17)));
string feedbackQueueTestDirectory = Path.Combine(
    Path.GetTempPath(),
    "lol-translator-feedback-test-" + Guid.NewGuid().ToString("N"));
var feedbackQueueService = new PlayerFeedbackService(null, pendingDirectory: feedbackQueueTestDirectory);
PlayerFeedbackSubmission feedbackQueueSubmission = PlayerFeedbackService.CreateSubmission(
    "ocr_error",
    "The translated line did not match what was visible.",
    "Open chat and send one short line.");
PlayerFeedbackSubmitResult feedbackQueueResult = await feedbackQueueService.SubmitOrQueueAsync(feedbackQueueSubmission);
string[] queuedFeedbackFiles = Directory.GetFiles(feedbackQueueTestDirectory, "*.json");
string queuedFeedbackJson = File.ReadAllText(queuedFeedbackFiles.Single());
ocrShadowChecks += Check(
    feedbackQueueResult.Status == "queued_for_retry" &&
    queuedFeedbackFiles.Length == 1 &&
    !queuedFeedbackJson.Contains("screenshot", StringComparison.OrdinalIgnoreCase) &&
    !queuedFeedbackJson.Contains("ocr_text", StringComparison.OrdinalIgnoreCase) &&
    !queuedFeedbackJson.Contains("chat_text", StringComparison.OrdinalIgnoreCase) &&
    !queuedFeedbackJson.Contains("runtime_log", StringComparison.OrdinalIgnoreCase));
Directory.Delete(feedbackQueueTestDirectory, recursive: true);
ocrShadowChecks += Check(
    OnlineDistributionLinks.LatestDownloadUrl ==
    "https://github.com/junjiea2/LoLTranslator-Preview/releases/latest");
var reviewedRuntimeProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService reviewedRuntimeService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: reviewedRuntimeProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult freezeWaveLine = selectedRegionBody with { Text = "00:12 [TEAM] (Braum): freeze this wave" };
IncrementalGeneralChatShadowResult freezeWaveResult = reviewedRuntimeService.Process(
    new[] { freezeWaveLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [freezeWaveLine.LineNumber] = "freeze-wave-occurrence" });
ocrShadowChecks += Check(freezeWaveResult is { ModelCallCount: 0, DisplayedNewCount: 1 } &&
    freezeWaveResult.Lines.Single() is { Route: "reviewed_runtime_term", FinalOutput: "控住这波兵线" } &&
    reviewedRuntimeProvider.LastRequest is null);
var typedEventProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService typedEventService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: typedEventProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrFusionMetadata typedPingFusion = selectedRegionBody.Fusion with
{
    Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
    {
        "runtime_event_ping", "lol_system_notification", "lol_system_action_without_delimiter"
    }).ToArray()
};
OcrShadowLineResult typedKnownPing = selectedRegionBody with
{
    Text = "04:42 [系统] Exodia (Zeri)：signals that enemies are missing",
    Fusion = typedPingFusion
};
IncrementalGeneralChatShadowLine typedKnownPingResult = typedEventService.Process(
    new[] { typedKnownPing }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [typedKnownPing.LineNumber] = "typed-known-ping" }).Lines.Single();
continuousRuntimeChecks += Check(
    typedKnownPingResult is { Route: "runtime_ping_template", FinalOutput: "敌人消失" } &&
    typedEventProvider.LastRequest is null);
OcrShadowLineResult typedUnknownSystem = selectedRegionBody with
{
    Text = "04:43 [系统] (游戏)：Objective event with damaged unreadable payload",
    Fusion = typedPingFusion with
    {
        Reasons = typedPingFusion.Reasons.Where(reason => reason != "runtime_event_ping")
            .Concat(new[] { "runtime_event_system" }).ToArray()
    }
};
IncrementalGeneralChatShadowLine typedUnknownSystemResult = typedEventService.Process(
    new[] { typedUnknownSystem }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [typedUnknownSystem.LineNumber] = "typed-unknown-system" }).Lines.Single();
continuousRuntimeChecks += Check(
    typedUnknownSystemResult is
    {
        Route: "structured_event_review", FinalOutput: null,
        FailureReason: "unknown_system_template_no_provider_call"
    } && typedEventProvider.LastRequest is null);
var itemPingProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService itemPingService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: itemPingProvider, reviewedRuntimeTermsPath: reviewedTermsPath,
    itemLexiconPath: candidate2LexiconPath, itemRuntimeAliasesPath: runtimeItemAliasesPath);
OcrShadowLineResult itemPingLine = selectedRegionBody with { Text = "03:13 神龙大侠（布朗姆）：飞翼月甲 - 需要37金币" };
IncrementalGeneralChatShadowResult itemPingResult = itemPingService.Process(
    new[] { itemPingLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [itemPingLine.LineNumber] = "item-ping-occurrence" });
ocrShadowChecks += Check(itemPingResult is { ModelCallCount: 0, DisplayedNewCount: 1 } &&
    itemPingResult.Lines.Single() is
    {
        Route: "official_item_ping_runtime",
        FinalOutput: "飞翼月甲 — 还需要 37 金币",
        SenderRelation: "ally"
    } && itemPingProvider.LastRequest is null);
var koreanSystemProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService koreanSystemService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: koreanSystemProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult koreanSystemLine = selectedRegionBody with
{
    Text = "14:11 [系统] Exuan (케인)：님이 가고 있음",
    Sources = new[] { new OcrShadowLineSource(0, "ko-KR", 1, "님이 가고 있음", "14:11") },
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_system_notification", "lol_source_identity_blue", "lol_allied_identity_row"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowResult koreanSystemResult = koreanSystemService.Process(
    new[] { koreanSystemLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [koreanSystemLine.LineNumber] = "ko-system-occurrence" });
ocrShadowChecks += Check(koreanSystemResult.Lines.Single() is
    { Route: "reviewed_runtime_term", FinalOutput: "正在路上", SenderRelation: "ally" } &&
    koreanSystemProvider.LastRequest is null);
var goldSystemService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: new RecordingLocalTranslationProvider(), reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult goldSystemLine = selectedRegionBody with
{
    Text = "15:50 [系统] (游戏)：防御塔已被摧毁",
    Sources = new[] { new OcrShadowLineSource(0, "zh-CN", 1, "防御塔已被摧毁", "15:50") },
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_system_notification", "lol_system_gold_row"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowLine goldSystemOutput = goldSystemService.Process(
    new[] { goldSystemLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [goldSystemLine.LineNumber] = "gold-system-occurrence" }).Lines.Single();
TranslationSidecarDisplayLine goldSystemRendered = sidecarFormatter.Format(
    goldSystemOutput.ChatPrefix, goldSystemOutput.FinalOutput!, "zh-CN", goldSystemOutput.SenderRelation);
ocrShadowChecks += Check(
    goldSystemOutput is { FinalOutput: "防御塔已被摧毁", SenderRelation: "gold" } &&
    goldSystemRendered.Segments.Any(segment => segment.Text.Contains("防御塔已被摧毁", StringComparison.Ordinal) &&
        segment.Role == TranslationSidecarTextRole.System));
var sameLanguageCassiopeiaProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService sameLanguageCassiopeiaService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: sameLanguageCassiopeiaProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult sameLanguageCassiopeiaLine = selectedRegionBody with
{
    Text = "15:51 [Team] Player Name (Cassiopeia): Cassiopeia现在正在中路并且准备支援我们",
    Sources = new[] { new OcrShadowLineSource(0, "auto", 1, "Cassiopeia现在正在中路并且准备支援我们", "15:51") },
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[] { "lol_source_identity_blue" }).ToArray()
    }
};
IncrementalGeneralChatShadowLine sameLanguageCassiopeiaOutput = sameLanguageCassiopeiaService.Process(
    new[] { sameLanguageCassiopeiaLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [sameLanguageCassiopeiaLine.LineNumber] = "same-language-cassiopeia" }).Lines.Single();
TranslationSidecarDisplayLine sameLanguageCassiopeiaDisplay = sidecarFormatter.Format(
    sameLanguageCassiopeiaOutput.ChatPrefix, sameLanguageCassiopeiaOutput.FinalOutput!, "zh-CN",
    sameLanguageCassiopeiaOutput.SenderRelation);
ocrShadowChecks += Check(
    sameLanguageCassiopeiaOutput is
    { Route: "source_locale_official_terms", FinalOutput: "魔蛇之拥现在正在中路并且准备支援我们", SenderRelation: "ally" } &&
    sameLanguageCassiopeiaOutput.ChatPrefix?.Contains("Player Name (魔蛇之拥)", StringComparison.Ordinal) == true &&
    sameLanguageCassiopeiaOutput.CanonicalIds.Contains("game.lol.official:champion.cassiopeia", StringComparer.OrdinalIgnoreCase) &&
    sameLanguageCassiopeiaProvider.LastRequest is null &&
    sameLanguageCassiopeiaDisplay.Segments.Any(segment => segment is
        { Role: TranslationSidecarTextRole.AlliedIdentity, Text: var identity } &&
        identity.Contains("魔蛇之拥", StringComparison.Ordinal) &&
        !identity.Contains("Player Name", StringComparison.Ordinal)));
var actorCassiopeiaProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService actorCassiopeiaService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: actorCassiopeiaProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult actorCassiopeiaLine = selectedRegionBody with
{
    Text = "15:52 Player Name (Cassiopeia) is on the way",
    Sources = new[] { new OcrShadowLineSource(0, "en-US", 1, "15:52 Player Name (Cassiopeia) is on the way", "15:52") },
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_system_notification", "lol_source_identity_blue"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowLine actorCassiopeiaOutput = actorCassiopeiaService.Process(
    new[] { actorCassiopeiaLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [actorCassiopeiaLine.LineNumber] = "actor-cassiopeia-system" }).Lines.Single();
ocrShadowChecks += Check(
    actorCassiopeiaOutput is
    { Route: "reviewed_runtime_term", FinalOutput: "正在路上", SenderRelation: "ally" } &&
    actorCassiopeiaOutput.ChatPrefix?.Contains("Player Name (魔蛇之拥)", StringComparison.Ordinal) == true &&
    !actorCassiopeiaOutput.MessageBody.Contains("Cassiopeia", StringComparison.OrdinalIgnoreCase) &&
    actorCassiopeiaProvider.LastRequest is null);
var kaenicProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService kaenicService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: kaenicProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult kaenicLine = selectedRegionBody with
{
    Text = "15:53 [Team] Bolemtin (Singed): Kaenic Rookern",
    Sources = new[] { new OcrShadowLineSource(0, "en-US", 1, "Kaenic Rookern", "15:53") }
};
IncrementalGeneralChatShadowLine kaenicOutput = kaenicService.Process(
    new[] { kaenicLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [kaenicLine.LineNumber] = "kaenic-direct-official" }).Lines.Single();
ocrShadowChecks += Check(
    kaenicOutput is { Route: "official_term_runtime", FinalOutput: "败魔" } &&
    kaenicOutput.CanonicalIds.Contains("game.lol.official:item.2504", StringComparer.OrdinalIgnoreCase) &&
    kaenicProvider.LastRequest is null);
var parenthesizedPingProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService parenthesizedPingService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: parenthesizedPingProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult parenthesizedPingLine = selectedRegionBody with
{
    Text = "15:54 [系统] (游戏)：Bolemtin|Singed (cautious)",
    Sources = new[] { new OcrShadowLineSource(0, "en-US", 1, "Bolemtin|Singed (cautious)", "15:54") },
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_system_notification", "lol_source_identity_blue", "lol_allied_identity_row"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowLine parenthesizedPingOutput = parenthesizedPingService.Process(
    new[] { parenthesizedPingLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [parenthesizedPingLine.LineNumber] = "parenthesized-short-ping" }).Lines.Single();
ocrShadowChecks += Check(
    parenthesizedPingOutput is { Route: "reviewed_runtime_term", FinalOutput: "小心！", SenderRelation: "ally" } &&
    parenthesizedPingOutput.ChatPrefix?.Contains("Bolemtin|Singed", StringComparison.Ordinal) == true &&
    parenthesizedPingProvider.LastRequest is null);
var identityProtectionProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService identityProtectionService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: identityProtectionProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult shutdownSystemLine = selectedRegionBody with
{
    Text = "17:52 [系统] grookey (Lee Sin)：has shut down Minh Trinh Korea (Zed)!",
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_system_notification", "lol_allied_identity_row"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowResult identityProtectionResult = identityProtectionService.Process(
    new[] { shutdownSystemLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [shutdownSystemLine.LineNumber] = "shutdown-system-occurrence" });
ocrShadowChecks += Check(
    identityProtectionProvider.LastRequest?.Text.Contains("OFFICIAL_CHAMPION_IDENTITY_LOCKED", StringComparison.Ordinal) == true &&
    identityProtectionResult.Lines.Single().FinalOutput?.Contains("影流之主", StringComparison.Ordinal) == true &&
    identityProtectionResult.Lines.Single().FinalOutput?.Contains("Minh Trinh Korea", StringComparison.Ordinal) == false &&
    identityProtectionResult.Lines.Single().SenderRelation == "ally" &&
    identityProtectionResult.Lines.Single().OpposingIdentityRenderings?.Contains("影流之主") == true);
var officialTermProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService officialTermService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: officialTermProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult officialTermLine = selectedRegionBody with
{
    Text = "18:01 [TEAM] Player Name (Braum): buy Control Ward",
    Sources = new[] { new OcrShadowLineSource(0, "en-US", 1, "buy Control Ward", "18:01") }
};
IncrementalGeneralChatShadowResult officialTermResult = officialTermService.Process(
    new[] { officialTermLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [officialTermLine.LineNumber] = "official-control-ward-occurrence" });
ocrShadowChecks += Check(
    officialTermProvider.LastRequest?.Text.Contains("OFFICIAL_TERM_LOCKED", StringComparison.Ordinal) == true &&
    officialTermProvider.LastRequest?.Text.Contains("Player Name", StringComparison.Ordinal) == false &&
    officialTermResult.Lines.Single().FinalOutput?.Contains("控制守卫", StringComparison.Ordinal) == true &&
    officialTermResult.Lines.Single().CanonicalIds.Contains("game.lol.official:item.2055", StringComparer.OrdinalIgnoreCase));
var blockedObjectiveProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService blockedObjectiveService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: blockedObjectiveProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult blockedObjectiveLine = selectedRegionBody with
{
    Text = "18:02 [TEAM] Player Name (Braum): take Hex Dragon",
    Sources = new[] { new OcrShadowLineSource(0, "en-US", 1, "take Hex Dragon", "18:02") }
};
IncrementalGeneralChatShadowResult blockedObjectiveResult = blockedObjectiveService.Process(
    new[] { blockedObjectiveLine }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [blockedObjectiveLine.LineNumber] = "blocked-hex-dragon-occurrence" });
ocrShadowChecks += Check(
    blockedObjectiveProvider.LastRequest is null &&
    blockedObjectiveResult.Lines.Single() is
    { Route: "review", DedupStatus: "fail_closed", FinalOutput: null,
      FailureReason: "blocked_official_term_ambiguity_no_model_guess" });
var targetedPingProvider = new RecordingLocalTranslationProvider();
IncrementalGeneralChatShadowService targetedPingService = IncrementalGeneralChatShadowService.CreateExplicit(
    ocrShadowService, provider: targetedPingProvider, reviewedRuntimeTermsPath: reviewedTermsPath);
OcrShadowLineResult alliedTargetedPing = selectedRegionBody with
{
    Text = "03:09 [系统] Bolemtin (Singed)：has targeted Thebausffs - (Irelia)",
    Fusion = selectedRegionBody.Fusion with
    {
        Reasons = selectedRegionBody.Fusion.Reasons.Concat(new[]
        {
            "lol_system_notification", "lol_allied_identity_row", "lol_system_gold_row"
        }).ToArray()
    }
};
IncrementalGeneralChatShadowResult targetedPingResult = targetedPingService.Process(
    new[] { alliedTargetedPing }, new ShadowMatchContextOption("test", "lol.global", "in_game_chat"), "zh-CN",
    new Dictionary<int, string> { [alliedTargetedPing.LineNumber] = "targeted-ping-occurrence" });
IncrementalGeneralChatShadowLine targetedPingOutput = targetedPingResult.Lines.Single();
TranslationSidecarDisplayLine targetedPingDisplay = sidecarFormatter.Format(
    targetedPingOutput.ChatPrefix,
    targetedPingOutput.FinalOutput!,
    "zh-CN",
    targetedPingOutput.SenderRelation,
    targetedPingOutput.OpposingIdentityRenderings,
    sourceGoldEmphasis: targetedPingOutput.Fusion.Reasons.Contains("lol_system_gold_row", StringComparer.Ordinal));
ocrShadowChecks += Check(
    targetedPingResult.ModelCallCount == 0 &&
    targetedPingOutput is
    {
        Route: "system_target_ping_template",
        FinalOutput: "已将目标锁定为 刀锋舞者",
        SenderRelation: "ally",
        OpposingIdentityRenderings: var targets
    } && targets?.Contains("刀锋舞者") == true &&
    targetedPingProvider.LastRequest is null &&
    targetedPingDisplay.Segments.Any(segment => segment is
        { Role: TranslationSidecarTextRole.AlliedIdentity, Text: var text } &&
        text.Contains("炼金术士", StringComparison.Ordinal) && !text.Contains("Bolemtin", StringComparison.Ordinal)) &&
    targetedPingDisplay.Segments.Any(segment => segment is
        { Role: TranslationSidecarTextRole.System, Text: var text } && text.Contains("已将目标锁定为", StringComparison.Ordinal)) &&
    targetedPingDisplay.Segments.Any(segment => segment is
        { Role: TranslationSidecarTextRole.EnemyIdentity, Text: "刀锋舞者" }));
var emptyProvider = CreateSafetyProvider(translateGemmaProfile, "{\"response\":\"   \"}");
GeneralTranslationResult emptyProviderResult = emptyProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !emptyProviderResult.IsAvailable &&
    emptyProviderResult.Text is null &&
    emptyProviderResult.FailureReason == "local_translation_empty_output");
var multilineProvider = CreateSafetyProvider(translateGemmaProfile, "{\"response\":\"第一行\\n第二行\"}");
GeneralTranslationResult multilineProviderResult = multilineProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !multilineProviderResult.IsAvailable &&
    multilineProviderResult.Text is null &&
    multilineProviderResult.FailureReason == "local_translation_format_violation_multiline");
var thinkingProvider = CreateSafetyProvider(translateGemmaProfile, "{\"response\":\"<think>reasoning</think>译文\"}");
GeneralTranslationResult thinkingProviderResult = thinkingProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !thinkingProviderResult.IsAvailable &&
    thinkingProviderResult.Text is null &&
    thinkingProviderResult.FailureReason == "local_translation_format_violation_prefix_or_thinking");
var noThinkLeakProvider = CreateSafetyProvider(qwenProfile, "{\"response\":\"守住中路 no think\"}");
GeneralTranslationResult noThinkLeakResult = noThinkLeakProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !noThinkLeakResult.IsAvailable &&
    noThinkLeakResult.Text is null &&
    noThinkLeakResult.FailureReason == "local_translation_control_token_leak");
var noThinkBatchLeakProvider = CreateSafetyProvider(
    qwenProfile,
    "{\"response\":\"{\\\"items\\\":[{\\\"id\\\":\\\"line-1\\\",\\\"translation\\\":\\\"/no_think\\\"}]}\"}");
BatchTranslationAttempt noThinkBatchLeakResult = noThinkBatchLeakProvider.TranslateBatchWithDiagnostics(
    new[] { new BatchTranslationItem("line-1", "hold mid", "en-US", "zh-CN") });
ocrShadowChecks += Check(
    noThinkBatchLeakResult.Items.Single() is
        { FinalOutput: null, Error: "local_translation_control_token_leak" });
var prefixProvider = CreateSafetyProvider(translateGemmaProfile, "{\"response\":\"Translation: 译文\"}");
GeneralTranslationResult prefixProviderResult = prefixProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !prefixProviderResult.IsAvailable &&
    prefixProviderResult.Text is null &&
    prefixProviderResult.FailureReason == "local_translation_format_violation_prefix_or_thinking");
var missingModelProvider = new OllamaLocalTranslationProvider(
    translateGemmaProfile,
    new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.NotFound, "{\"error\":\"model not found\"}"))
    { BaseAddress = new Uri("http://127.0.0.1:11434/") });
GeneralTranslationResult missingModelResult = missingModelProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !missingModelResult.IsAvailable &&
    missingModelResult.Text is null &&
    missingModelResult.FailureReason == "ollama_local_provider_error:HttpRequestException");
var serviceStoppedProvider = new OllamaLocalTranslationProvider(
    translateGemmaProfile,
    new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("connection refused")))
    { BaseAddress = new Uri("http://127.0.0.1:11434/") });
GeneralTranslationResult serviceStoppedResult = serviceStoppedProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !serviceStoppedResult.IsAvailable &&
    serviceStoppedResult.Text is null &&
    serviceStoppedResult.FailureReason == "ollama_local_provider_error:HttpRequestException");
var timeoutProvider = new OllamaLocalTranslationProvider(
    translateGemmaProfile,
    new HttpClient(new ThrowingHttpMessageHandler(new TaskCanceledException("timed out")))
    { BaseAddress = new Uri("http://127.0.0.1:11434/") });
GeneralTranslationResult timeoutResult = timeoutProvider.Translate(
    new GeneralTranslationRequest("hold mid", "en-US", "zh-CN"));
ocrShadowChecks += Check(
    !timeoutResult.IsAvailable &&
    timeoutResult.Text is null &&
    timeoutResult.FailureReason == "ollama_local_provider_error:TaskCanceledException");
var recordingProvider = new RecordingLocalTranslationProvider();
var protectedHybridOrchestrator = new ChatProcessingRouteOrchestrator(
    ocrShadowService,
    recordingProvider,
    new ProtectedHybridTranslationAggregator(recordingProvider));
ChatProcessingResult protectedHybrid = protectedHybridOrchestrator.Process(
    new ChatProcessingRequest(
        "top no f, I can repeat gank",
        "en-US",
        "zh-CN",
        runtimeRouteContext,
        new ChatProcessingPolicy(
            ChatProcessingRoutes.Hybrid,
            new ChatSemanticPolicy(
                "required",
                new[] { "game.lol:summoner.flash.unavailable" },
                Array.Empty<string>()))));
ocrShadowChecks += Check(
    recordingProvider.LastRequest?.Text.Contains("[[SG_LOCKED_1]]", StringComparison.Ordinal) == true &&
    recordingProvider.LastRequest?.Text.Contains("no f", StringComparison.OrdinalIgnoreCase) == false &&
    protectedHybrid.OutputText?.Contains("上路没闪", StringComparison.Ordinal) == true);
var corruptMarkerProvider = new CorruptMarkerTranslationProvider();
var corruptHybridOrchestrator = new ChatProcessingRouteOrchestrator(
    ocrShadowService,
    corruptMarkerProvider,
    new ProtectedHybridTranslationAggregator(corruptMarkerProvider));
ChatProcessingResult corruptHybrid = corruptHybridOrchestrator.Process(
    new ChatProcessingRequest(
        "top no f, I can repeat gank",
        "en-US",
        "zh-CN",
        runtimeRouteContext,
        new ChatProcessingPolicy(
            ChatProcessingRoutes.Hybrid,
            new ChatSemanticPolicy(
                "required",
                new[] { "game.lol:summoner.flash.unavailable" },
                Array.Empty<string>()))));
ocrShadowChecks += Check(
    corruptHybrid.Status == "hybrid_unavailable" &&
    corruptHybrid.OutputText is null &&
    corruptHybrid.HybridAggregation?.FailureReason == "hybrid_locked_marker_not_preserved");

DomainLexicon lexiconFixture = DomainLexiconLoader.LoadJsonl(domainLexiconFixturePath);
var lexiconIndex = new DomainLexiconIndex(lexiconFixture);
var gameLexiconContext = new DomainLexiconMatchContext("fixture.game", "fixture.global", "in_game_chat", "1.8");
DomainLexiconMatchResult broadLexiconMatch = lexiconIndex.Match(
    "em  ber has long sword; bot mid flash F TP wave reset base clean",
    "en-US",
    gameLexiconContext);
string[] broadIds = broadLexiconMatch.FormalMatches.Select(match => match.CanonicalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
domainLexiconChecks += Check(
    broadIds.Contains("fixture.game:champion.ember") &&
    broadIds.Contains("fixture.game:item.long-sword") &&
    broadIds.Contains("fixture.game:location.bot") &&
    broadIds.Contains("fixture.game:location.mid") &&
    broadIds.Contains("fixture.game:spell.flash") &&
    broadIds.Contains("fixture.game:spell.teleport") &&
    broadIds.Contains("fixture.game:strategy.wave-reset"));
domainLexiconChecks += Check(
    broadLexiconMatch.FormalMatches.Any(match => match.CanonicalId == "fixture.game:item.long-sword" && match.MatchedText == "long sword") &&
    !broadLexiconMatch.FormalMatches.Any(match => match.AliasId == "fixture.game:alias.sword"));
domainLexiconChecks += Check(
    broadLexiconMatch.FormalMatches.First(match => match.CanonicalId == "fixture.game:spell.flash").ToSlotValue().EntityId ==
    "fixture.game:spell.flash");
DomainLexiconMatchResult topAmbiguity = lexiconIndex.Match("top", "en-US", gameLexiconContext);
domainLexiconChecks += Check(
    topAmbiguity.FormalMatches.Count == 0 &&
    topAmbiguity.AmbiguityReasons.Contains("ambiguous_alias:position_top"));
DomainLexiconMatchResult nonGame = lexiconIndex.Match(
    "mid flash F TP wave reset base clean", "en-US",
    new DomainLexiconMatchContext("non_game", "other", "chat", "1.8"));
domainLexiconChecks += Check(nonGame.FormalMatches.Count == 0 && nonGame.CandidateShadowMatches.Count == 0);
DomainLexiconMatchResult expired = lexiconIndex.Match("legacy orb", "en-US", gameLexiconContext);
domainLexiconChecks += Check(expired.FormalMatches.Count == 0);
DomainLexiconMatchResult candidateHidden = lexiconIndex.Match("hollow", "en-US", gameLexiconContext);
DomainLexiconMatchResult candidateShadow = lexiconIndex.Match(
    "hollow", "en-US", gameLexiconContext, new DomainLexiconMatchOptions(IncludeCandidateShadow: true));
domainLexiconChecks += Check(
    candidateHidden.FormalMatches.Count == 0 && candidateHidden.CandidateShadowMatches.Count == 0 &&
    candidateShadow.FormalMatches.Count == 0 && candidateShadow.CandidateShadowMatches.Single().CanonicalId == "fixture.game:item.hollow");
DomainLexiconRenderedEntry localeFallback = lexiconIndex.Render("fixture.game:objective.beacon", "zh-Hans-SG");
domainLexiconChecks += Check(localeFallback.Text == "信标" && localeFallback.UsedFallback);
var lexiconShadowProcessor = new DomainLexiconShadowProcessor(
    lexiconIndex,
    DomainLexiconShadowProfile.ExplicitTestOnly(),
    protectedHybridOrchestrator);
DomainLexiconShadowResult protectedLexicon = lexiconShadowProcessor.Process(
    new ChatProcessingRequest(
        "ember reset", "en-US", "zh-CN", runtimeRouteContext,
        new ChatProcessingPolicy(ChatProcessingRoutes.GeneralTranslation,
            new ChatSemanticPolicy("optional", Array.Empty<string>(), Array.Empty<string>()))),
    gameLexiconContext);
domainLexiconChecks += Check(
    protectedLexicon.ForcedHybridForFormalLexicon &&
    protectedLexicon.Processing.ProcessingRoute == ChatProcessingRoutes.Hybrid &&
    protectedLexicon.LexiconLockedSpans.Count == 2 &&
    recordingProvider.LastRequest?.Text.Contains("[[DL_LOCKED_1]]", StringComparison.Ordinal) == true &&
    recordingProvider.LastRequest?.Text.Contains("ember", StringComparison.OrdinalIgnoreCase) == false &&
    protectedLexicon.Processing.OutputText?.Contains("余烬", StringComparison.Ordinal) == true &&
    protectedLexicon.Processing.OutputText?.Contains("兵线重置", StringComparison.Ordinal) == true);
var candidateLexiconShadowProcessor = new DomainLexiconShadowProcessor(
    lexiconIndex,
    DomainLexiconShadowProfile.ExplicitTestOnly(includeCandidateShadow: true),
    protectedHybridOrchestrator);
DomainLexiconShadowResult candidateLexicon = candidateLexiconShadowProcessor.Process(
    new ChatProcessingRequest("hollow", "en-US", "zh-CN", runtimeRouteContext,
        new ChatProcessingPolicy(ChatProcessingRoutes.GeneralTranslation,
            new ChatSemanticPolicy("optional", Array.Empty<string>(), Array.Empty<string>()))),
    gameLexiconContext);
domainLexiconChecks += Check(
    candidateLexicon.Lexicon.FormalMatches.Count == 0 &&
    candidateLexicon.Lexicon.CandidateShadowMatches.Count == 1 &&
    candidateLexicon.LexiconLockedSpans.Count == 0 &&
    !candidateLexicon.ForcedHybridForFormalLexicon &&
    candidateLexicon.Processing.ProcessingRoute == ChatProcessingRoutes.GeneralTranslation);
var disabledLexiconShadowProcessor = new DomainLexiconShadowProcessor(
    lexiconIndex,
    DomainLexiconShadowProfile.Disabled,
    protectedHybridOrchestrator);
DomainLexiconShadowResult disabledLexicon = disabledLexiconShadowProcessor.Process(
    new ChatProcessingRequest("ember", "en-US", "zh-CN", runtimeRouteContext,
        new ChatProcessingPolicy(ChatProcessingRoutes.GeneralTranslation,
            new ChatSemanticPolicy("optional", Array.Empty<string>(), Array.Empty<string>()))),
    gameLexiconContext);
domainLexiconChecks += Check(
    disabledLexicon.Lexicon.FormalMatches.Count == 0 &&
    disabledLexicon.LexiconLockedSpans.Count == 0 &&
    !disabledLexicon.ForcedHybridForFormalLexicon &&
    disabledLexicon.Processing.ProcessingRoute == ChatProcessingRoutes.GeneralTranslation);

candidateLexiconCompatibilityChecks += Check(
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidateLexiconPath))) ==
    "5804A00B807D6F6E91CB4C60AD1D477D1AAEEF55D466F689C728C6597FD5BF85");
DomainLexiconCandidateLoadResult candidateLexiconLoad = DomainLexiconLoader.LoadCandidateSidecarJsonl(candidateLexiconMetadataPath);
candidateLexiconCompatibilityChecks += Check(candidateLexiconLoad.Lexicon?.Entries.Count == 499 &&
    !candidateLexiconLoad.Issues.Any(issue => issue.Code == "invalid_json"));
candidateLexiconCompatibilityChecks += Check(candidateLexiconLoad.Lexicon!.Entries.All(entry =>
    string.Equals(entry.FormalEligibility, "candidate_only", StringComparison.OrdinalIgnoreCase)));
var candidateLexiconIndex = new DomainLexiconIndex(candidateLexiconLoad.Lexicon!);
DomainLexiconEntry candidateChampion = candidateLexiconLoad.Lexicon!.Entries
    .First(entry => string.Equals(entry.Type, "champion", StringComparison.OrdinalIgnoreCase));
DomainLexiconAlias candidateChampionAlias = candidateChampion.Aliases
    .First(alias => string.Equals(alias.Locale, "en-US", StringComparison.OrdinalIgnoreCase));
var candidateLexiconContext = new DomainLexiconMatchContext(
    null, null, null, "16.15.1",
    candidateChampion.ContextScopes.SelectMany(scope => scope.Tags ?? Array.Empty<string>()).ToArray());
DomainLexiconMatchResult candidateDefault = candidateLexiconIndex.Match(candidateChampionAlias.Surface, "en-US", candidateLexiconContext);
DomainLexiconMatchResult candidateLexiconShadowMatch = candidateLexiconIndex.Match(candidateChampionAlias.Surface, "en-US", candidateLexiconContext, new DomainLexiconMatchOptions(true));
candidateLexiconCompatibilityChecks += Check(candidateDefault.FormalMatches.Count == 0 &&
    candidateDefault.CandidateShadowMatches.Count == 0 &&
    candidateLexiconShadowMatch.FormalMatches.Count == 0 && candidateLexiconShadowMatch.CandidateShadowMatches.Count == 1);
candidateLexiconCompatibilityChecks += Check(candidateLexiconIndex.Match(
        candidateChampionAlias.Surface, "en-US", candidateLexiconContext with { Patch = "16.14.1" },
        new DomainLexiconMatchOptions(true)).CandidateShadowMatches.Count == 0 &&
    candidateLexiconIndex.Match(candidateChampionAlias.Surface, "en-US",
        new DomainLexiconMatchContext(null, null, null, "16.15.1", Array.Empty<string>()),
        new DomainLexiconMatchOptions(true)).CandidateShadowMatches.Count == 0);
var candidateConflicts = candidateLexiconLoad.Lexicon!.Entries
    .SelectMany(entry => entry.Aliases.Select(alias => (Entry: entry, Alias: alias)))
    .GroupBy(item => $"{item.Alias.Locale}\u001f{DomainLexiconTextNormalizer.Normalize(item.Alias.Normalized)}", StringComparer.OrdinalIgnoreCase)
    .Where(group => group.Select(item => item.Entry.CanonicalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
    .ToArray();
candidateLexiconCompatibilityChecks += Check(candidateConflicts.Length == 125 && candidateConflicts.All(group =>
{
    var sample = group.First();
    DomainLexiconMatchResult result = candidateLexiconIndex.Match(
        sample.Alias.Surface,
        sample.Alias.Locale,
        new DomainLexiconMatchContext(null, null, null, "16.15.1", sample.Entry.ContextScopes.SelectMany(scope => scope.Tags ?? Array.Empty<string>()).ToArray()),
        new DomainLexiconMatchOptions(true));
    return result.FormalMatches.Count == 0 && result.CandidateShadowMatches.Count == 0 && result.AmbiguityReasons.Count > 0;
}));
var actualCandidateShadowProcessor = new DomainLexiconShadowProcessor(
    candidateLexiconIndex,
    DomainLexiconShadowProfile.ExplicitTestOnly(includeCandidateShadow: true),
    protectedHybridOrchestrator);
DomainLexiconShadowResult actualCandidateBridge = actualCandidateShadowProcessor.Process(
    new ChatProcessingRequest(candidateChampionAlias.Surface, "en-US", "zh-CN", runtimeRouteContext,
        new ChatProcessingPolicy(ChatProcessingRoutes.GeneralTranslation,
            new ChatSemanticPolicy("optional", Array.Empty<string>(), Array.Empty<string>()))),
    candidateLexiconContext);
candidateLexiconCompatibilityChecks += Check(
    actualCandidateBridge.Lexicon.FormalMatches.Count == 0 &&
    actualCandidateBridge.Lexicon.CandidateShadowMatches.Count == 1 &&
    actualCandidateBridge.LexiconLockedSpans.Count == 0 &&
    !actualCandidateBridge.ForcedHybridForFormalLexicon &&
    actualCandidateBridge.Processing.ProcessingRoute == ChatProcessingRoutes.GeneralTranslation &&
    recordingProvider.LastRequest?.Text == candidateChampionAlias.Surface);

candidateValidationFailures.AddRange(ValidateCandidatePack(candidatePackSet, candidateGraphPack));

foreach (ObservationRecord observation in candidateGraphPack.Observations)
{
    IReadOnlyList<SlangGraphSemanticMatch> matches = candidateMatcher
        .Match(observation.RawText, observation.Locale, includeDraft: true, ContextFor(observation));

    foreach (ObservationLabel label in observation.Labels)
    {
        if (string.Equals(label.Polarity, "negative", StringComparison.OrdinalIgnoreCase))
        {
            if (matches.Any(match => string.Equals(match.ConceptId, label.ConceptId, StringComparison.OrdinalIgnoreCase)))
            {
                candidateValidationFailures.Add(
                    $"{observation.ObservationId}: negative label {label.ConceptId} was matched.");
            }

            continue;
        }

        if (string.Equals(label.Polarity, "uncertain", StringComparison.OrdinalIgnoreCase))
        {
            candidateDeferredLabels.Add(
                $"{observation.ObservationId}: uncertain label {label.ConceptId} is excluded from the positive regression gate.");
            continue;
        }

        SlangGraphSemanticMatch? match = matches
            .Where(item => string.Equals(item.ConceptId, label.ConceptId, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(item => SlotsMatch(item, label.Slots));

        if (match is null)
        {
            candidateValidationFailures.Add(
                $"{observation.ObservationId}: expected positive {label.ConceptId} with slots " +
                $"{FormatSlots(label.Slots)}, actual matches: {FormatMatches(matches)}.");
        }
    }
}

candidateRegressionChecks = candidateGraphPack.Observations.Count == 9 && candidateValidationFailures.Count == 0 ? 9 : 0;

candidate2ValidationFailures.AddRange(ValidateCandidatePack(
    candidate2PackSet,
    candidate2GraphPack,
    "pack.game.lol.chat",
    "0.2.0-candidate.4"));

foreach (ObservationRecord observation in candidate2GraphPack.Observations.Where(item =>
             item.ObservationId.StartsWith("game.lol:observation.batch-002.", StringComparison.OrdinalIgnoreCase)))
{
    IReadOnlyList<SlangGraphSemanticMatch> matches = candidate2Matcher
        .Match(observation.RawText, observation.Locale, includeDraft: true, ContextFor(observation));

    foreach (ObservationLabel label in observation.Labels)
    {
        bool isNegative = string.Equals(label.Polarity, "negative", StringComparison.OrdinalIgnoreCase);
        bool hasExpected = matches.Any(match =>
            string.Equals(match.ConceptId, label.ConceptId, StringComparison.OrdinalIgnoreCase) &&
            SlotsMatch(match, label.Slots));

        if (isNegative && hasExpected)
        {
            candidate2ValidationFailures.Add(
                $"{observation.ObservationId}: negative label {label.ConceptId} was matched.");
        }
        else if (!isNegative && !hasExpected)
        {
            candidate2ValidationFailures.Add(
                $"{observation.ObservationId}: expected {label.Polarity} {label.ConceptId} with slots " +
                $"{FormatSlots(label.Slots)}, actual matches: {FormatMatches(matches)}.");
        }
    }
}

int candidate2ObservationCount = candidate2GraphPack.Observations.Count(item =>
    item.ObservationId.StartsWith("game.lol:observation.batch-002.", StringComparison.OrdinalIgnoreCase));
candidate2RegressionChecks = candidate2ObservationCount == 12 && candidate2ValidationFailures.Count == 0 ? 12 : 0;

int supplementalObservationCount = candidate2GraphPack.Observations.Count(item =>
    item.ObservationId.StartsWith("game.lol:observation.batch-004.", StringComparison.OrdinalIgnoreCase));
foreach (ObservationRecord observation in candidate2GraphPack.Observations.Where(item =>
             item.ObservationId.StartsWith("game.lol:observation.batch-004.", StringComparison.OrdinalIgnoreCase)))
{
    IReadOnlyList<SlangGraphSemanticMatch> matches = candidate2Matcher
        .Match(observation.RawText, observation.Locale, includeDraft: true, ContextFor(observation));

    foreach (ObservationLabel label in observation.Labels)
    {
        bool isNegative = string.Equals(label.Polarity, "negative", StringComparison.OrdinalIgnoreCase);
        bool hasExpected = matches.Any(match =>
            string.Equals(match.ConceptId, label.ConceptId, StringComparison.OrdinalIgnoreCase) &&
            SlotsMatch(match, label.Slots));

        if (isNegative && hasExpected)
        {
            candidate2ValidationFailures.Add(
                $"{observation.ObservationId}: supplemental negative label {label.ConceptId} was matched.");
        }
        else if (!isNegative && !hasExpected)
        {
            candidate2ValidationFailures.Add(
                $"{observation.ObservationId}: supplemental expected {label.Polarity} {label.ConceptId} with slots " +
                $"{FormatSlots(label.Slots)}, actual matches: {FormatMatches(matches)}.");
        }
    }
}

supplementalRegressionChecks = supplementalObservationCount == 4 &&
                               candidate2ValidationFailures.Count == 0 ? 4 : 0;

dependencyFailureChecks += ExpectInvalidData(() =>
    SlangGraphPackSetLoader.Load(new[] { graphPackPath }));
dependencyFailureChecks += ExpectInvalidData(() =>
    SlangGraphPackSetLoader.Load(new[] { wrongCorePath, graphPackPath }));
dependencyFailureChecks += ExpectInvalidData(() =>
    SlangGraphPackSetLoader.Load(new[] { cycleAPath, cycleBPath }));
var graphMatcher = new SlangGraphSemanticMatcher(graphPackSet.CombinedGraph);
SlangGraphSemanticMatch? lolPatternMatch = graphMatcher
    .Match("top no flash", "en-US", includeDraft: true, new SlangGraphMatchContext("lol.global", "in_game_chat"))
    .FirstOrDefault(match => match.SourceRecordId == "game.lol:pattern.en.flash-unavailable.001");
genericMatcherChecks += Check(lolPatternMatch is not null);
genericMatcherChecks += Check(SlotValueIs(lolPatternMatch, "subject", "game.lol:lane.top"));
genericMatcherChecks += Check(lolPatternMatch?.Evidence.Any(item =>
    item.Kind == "entity_resolution" && item.RecordId == "game.lol:lane.top") == true);

var genericMatcher = new SlangGraphSemanticMatcher(genericSlotExtractor);
SlangGraphSemanticMatch? genericSlotMatch = genericMatcher
    .Match("a has 3 stacks for 1:30 in safe mode", "en-US", includeDraft: true, new SlangGraphMatchContext(null, "fixture"))
    .SingleOrDefault();
genericMatcherChecks += Check(genericSlotMatch is not null);
genericMatcherChecks += Check(SlotValueIs(genericSlotMatch, "subject", "fixture.extractor:person.alpha"));
genericMatcherChecks += Check(SlotValueIs(genericSlotMatch, "count", "3"));
genericMatcherChecks += Check(SlotValueIs(genericSlotMatch, "window", "PT90S"));
genericMatcherChecks += Check(SlotValueIs(genericSlotMatch, "mode", "safe"));
genericMatcherChecks += Check(genericSlotMatch?.Evidence.Any(item =>
    item.Kind == "entity_resolution" && item.Detail.StartsWith("entity_alias:", StringComparison.Ordinal)) == true);

SlangGraphSemanticMatch? softwarePatternMatch = graphMatcher
    .Match("CI can't deploy", "en-US", includeDraft: true, new SlangGraphMatchContext("software.global", "chat"))
    .FirstOrDefault(match => match.SourceRecordId == "work.software:pattern.en.cant-deploy.001");
crossDomainChecks += Check(softwarePatternMatch is not null);
crossDomainChecks += Check(SlotValueIs(softwarePatternMatch, "subject", "work.software:system.ci"));
crossDomainChecks += Check(SlotValueIs(softwarePatternMatch, "capability", "work.software:capability.deployment"));

var navigator = new SlangGraphRelationNavigator(graphPackSet.CombinedGraph);
IReadOnlyList<SlangGraphConceptConnection> lolConnections = navigator.FindRelatedConcepts(
    "game.lol:summoner.flash.unavailable",
    maxHops: 2);
SlangGraphConceptConnection? softwareConnection = lolConnections.FirstOrDefault(connection =>
    connection.ConceptId == "work.software:deployment.unavailable");
crossDomainChecks += Check(softwareConnection?.Steps.Count == 2);
crossDomainChecks += Check(softwareConnection?.Steps.All(step => step.RelationType == "specialization_of") == true);

SlangGraphReleaseGateReport draftGraphReport = SlangGraphReleaseGate.Evaluate(graphPackSet.CombinedGraph);
releaseGateChecks += Check(!draftGraphReport.CanRelease);
releaseGateChecks += Check(SlangGraphReleaseGate.TryCreateSnapshot(
    reviewReleaseReady,
    "fixture-release-001",
    new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero),
    out SlangGraphReleaseSnapshot? readySnapshot,
    out SlangGraphReleaseGateReport readyReport));
releaseGateChecks += Check(readyReport.CanRelease && readySnapshot?.ContentSha256.Length == 64);
releaseGateChecks += Check(readySnapshot?.Graph.Reviews.Count == 2);
SlangGraphReleaseGateReport releaseCandidateReport = SlangGraphReleaseGate.Evaluate(releaseCandidatePack);
releaseCandidateChecks += Check(releaseCandidateReport.CanRelease);
releaseCandidateChecks += Check(SlangGraphReleaseGate.TryCreateSnapshot(
    releaseCandidatePack,
    "lol-0.3.0-rc.1",
    new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero),
    out SlangGraphReleaseSnapshot? releaseCandidateSnapshot,
    out SlangGraphReleaseGateReport releaseCandidateSnapshotReport) &&
    releaseCandidateSnapshot?.ContentSha256.Length == 64 &&
    releaseCandidateSnapshotReport.CanRelease);

releaseCandidateIntegrityFailures.AddRange(ValidateCandidatePack(
    releaseCandidatePackSet,
    releaseCandidatePack,
    "pack.game.lol.chat",
    "0.3.0-rc.1"));
string releaseCandidateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(releaseCandidatePackPath)));
if (!string.Equals(releaseCandidateSha256, "2F6046C70B60D5419EC389DCEEA44C34B71CA28085487718790F5D378C3DF0BE", StringComparison.Ordinal))
{
    releaseCandidateIntegrityFailures.Add($"SHA-256 changed: {releaseCandidateSha256}.");
}

releaseCandidateIntegrityFailures.AddRange(ValidateReleaseTraceability(releaseCandidatePack));

if (releaseCandidatePack.Observations.Any(item =>
        item.ReviewStatus is "pending" or "needs_changes" ||
        item.ObservationId.Contains("gank", StringComparison.OrdinalIgnoreCase) ||
        item.Context?.Contains("hardware.fr", StringComparison.OrdinalIgnoreCase) == true) ||
    releaseCandidatePack.Evidence.Any(item =>
        item.ReviewStatus is "pending" or "needs_changes" ||
        item.Notes?.Contains("hardware.fr", StringComparison.OrdinalIgnoreCase) == true))
{
    releaseCandidateIntegrityFailures.Add("RC contains pending, needs_changes, bare gank, or Hardware.fr material.");
}

releaseCandidateIntegrityChecks = releaseCandidateIntegrityFailures.Count == 0 ? 5 : 0;

foreach (ReleaseHoldoutCase holdoutCase in releaseHoldout)
{
    if (!HasCompleteHoldoutMetadata(holdoutCase))
    {
        releaseCandidateHoldoutFailures.Add($"{holdoutCase.CaseId}: incomplete locale, community, channel, source, timestamp, redaction, or provenance metadata.");
        continue;
    }

    IReadOnlyList<SlangGraphSemanticMatch> matches = releaseCandidateMatcher.Match(
        holdoutCase.RawText,
        holdoutCase.Locale,
        includeDraft: false,
        new SlangGraphMatchContext(holdoutCase.Community, holdoutCase.Channel));

    if (holdoutCase.ExpectedConceptId is not null)
    {
        SlangGraphSemanticMatch? expectedMatch = matches.FirstOrDefault(match =>
            string.Equals(match.ConceptId, holdoutCase.ExpectedConceptId, StringComparison.OrdinalIgnoreCase) &&
            SlotsMatch(match, holdoutCase.ExpectedSlots));

        if (expectedMatch is null)
        {
            releaseCandidateHoldoutFailures.Add(
                $"{holdoutCase.CaseId}: expected {holdoutCase.ExpectedConceptId} with slots {FormatSlots(holdoutCase.ExpectedSlots)}, actual matches: {FormatMatches(matches)}.");
        }
        else if (holdoutCase.RequireEvidenceChain &&
                 (!expectedMatch.Evidence.Any(item => item.Kind == "match_record") ||
                  !expectedMatch.Evidence.Any(item => item.Kind == "evidence_record")))
        {
            releaseCandidateHoldoutFailures.Add($"{holdoutCase.CaseId}: match is missing its record or evidence explanation chain.");
        }
    }
    else if (matches.Count > 0)
    {
        releaseCandidateHoldoutFailures.Add($"{holdoutCase.CaseId}: expected no released match, actual matches: {FormatMatches(matches)}.");
    }

    foreach (string forbiddenConceptId in holdoutCase.ForbiddenConceptIds)
    {
        if (matches.Any(match => string.Equals(match.ConceptId, forbiddenConceptId, StringComparison.OrdinalIgnoreCase)))
        {
            releaseCandidateHoldoutFailures.Add($"{holdoutCase.CaseId}: forbidden concept {forbiddenConceptId} was matched.");
        }
    }
}

releaseCandidateHoldoutChecks = releaseHoldout.Count == 6 && releaseCandidateHoldoutFailures.Count == 0 ? 6 : 0;

releasePackChecks += Check(string.Equals(releasePack.Header.PackVersion, "0.3.0", StringComparison.OrdinalIgnoreCase));
releasePackChecks += Check(string.Equals(releasePack.Header.Status, "verified", StringComparison.OrdinalIgnoreCase));
releasePackChecks += Check(SlangGraphReleaseGate.Evaluate(releasePack).CanRelease);
releasePackChecks += Check(
    File.ReadLines(releaseCandidatePackPath).Skip(1)
        .SequenceEqual(File.ReadLines(releasePackPath).Skip(1), StringComparer.Ordinal));

localizedRenderingChecks += Check(renderingPack.Renderings.Count == 6);
localizedRenderingChecks += Check(renderingPack.Renderings.All(rendering =>
    string.Equals(rendering.Status, "pending", StringComparison.OrdinalIgnoreCase)));
localizedRenderingChecks += Check(!SlangGraphReleaseGate.Evaluate(renderingPack).CanRelease);

SlangGraphSemanticMatch? localizedCareMatch = releaseMatcher.Match(
        "top care",
        "en-US",
        includeDraft: false,
        new SlangGraphMatchContext("lol.global", "in_game_chat"))
    .FirstOrDefault(match => match.ConceptId == "game.lol:lane.caution");
localizedRenderingChecks += Check(localizedCareMatch is not null);

var localizedRenderer = new SlangGraphLocalizedRenderer(renderingPackSet.CombinedGraph);
SlangGraphRenderedConcept zhCare = localizedRenderer.Render(
    "game.lol:lane.caution",
    "zh-CN",
    localizedCareMatch?.Slots,
    includeDraft: true);
localizedRenderingChecks += Check(zhCare.Text == "上路小心" && zhCare.ResolvedLocale == "zh-CN");

SlangGraphRenderedConcept enCare = localizedRenderer.Render(
    "game.lol:lane.caution",
    "en-US",
    localizedCareMatch?.Slots,
    includeDraft: true);
localizedRenderingChecks += Check(enCare.Text == "Be careful: top" && !enCare.UsedFallback);

SlangGraphRenderedConcept enGbCare = localizedRenderer.Render(
    "game.lol:lane.caution",
    "en-GB",
    localizedCareMatch?.Slots,
    includeDraft: true);
localizedRenderingChecks += Check(enGbCare.Text == "Be careful: top" && enGbCare.UsedFallback);

SlangGraphRenderedConcept noSlotFlash = localizedRenderer.Render(
    "game.lol:summoner.flash.unavailable",
    "zh-CN",
    includeDraft: true);
localizedRenderingChecks += Check(noSlotFlash.Text == "闪现不可用");

SlangGraphRenderedConcept unpublishedFallback = localizedRenderer.Render(
    "game.lol:lane.caution",
    "zh-CN",
    localizedCareMatch?.Slots,
    includeDraft: false);
localizedRenderingChecks += Check(
    unpublishedFallback.RenderingId is null &&
    unpublishedFallback.Text == releasePack.Concepts.First(concept =>
        concept.ConceptId == "game.lol:lane.caution").Definition);

localizedRenderingRcChecks += Check(
    renderingRcPack.Header.PackVersion == "0.1.0-rc.1" &&
    renderingRcPack.Header.Status == "verified");
localizedRenderingRcChecks += Check(
    renderingRcPack.Renderings.Count == 6 &&
    renderingRcPack.Renderings.All(rendering => rendering.Status == "verified"));
localizedRenderingRcChecks += Check(
    renderingRcPack.Evidence.Count == 6 &&
    renderingRcPack.Evidence.All(evidence =>
        evidence.ReviewStatus == "verified" &&
        evidence.ReviewerId == "reviewer.user.localization.001"));
localizedRenderingRcChecks += Check(
    renderingRcPack.Reviews.Count == 12 &&
    renderingRcPack.Reviews.All(review => review.Decision == "approved"));
localizedRenderingRcChecks += Check(renderingRcPack.Renderings.All(rendering =>
    renderingRcPack.Reviews.Any(review =>
        review.SubjectId == rendering.RenderingId && review.Decision == "approved")));
localizedRenderingRcChecks += Check(renderingRcPack.Evidence.All(evidence =>
    renderingRcPack.Reviews.Any(review =>
        review.SubjectId == evidence.EvidenceId && review.Decision == "approved")));
localizedRenderingRcChecks += Check(SlangGraphReleaseGate.Evaluate(renderingRcPack).CanRelease);

var rcRenderer = new SlangGraphLocalizedRenderer(renderingRcPackSet.CombinedGraph);
SlangGraphRenderedConcept rcZhCare = rcRenderer.Render(
    "game.lol:lane.caution",
    "zh-CN",
    localizedCareMatch?.Slots,
    includeDraft: false);
localizedRenderingRcChecks += Check(
    rcZhCare.Text == "上路小心" &&
    rcZhCare.RenderingId == "rendering.game.lol.lane-caution.zh-cn.001");

SlangGraphRenderedConcept rcEnGbCare = rcRenderer.Render(
    "game.lol:lane.caution",
    "en-GB",
    localizedCareMatch?.Slots,
    includeDraft: false);
localizedRenderingRcChecks += Check(
    rcEnGbCare.Text == "Be careful: top" &&
    rcEnGbCare.ResolvedLocale == "en-US" &&
    rcEnGbCare.UsedFallback);
localizedRenderingRcChecks += Check(
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(releasePackPath))) ==
    OcrShadowSemanticService.ExpectedPackSha256);

localizedRenderingReleaseChecks += Check(
    renderingReleasePack.Header.PackVersion == "0.1.0" &&
    renderingReleasePack.Header.Status == "verified");
localizedRenderingReleaseChecks += Check(SlangGraphReleaseGate.Evaluate(renderingReleasePack).CanRelease);
localizedRenderingReleaseChecks += Check(
    File.ReadLines(renderingRcPackPath).Skip(1)
        .SequenceEqual(File.ReadLines(renderingReleasePackPath).Skip(1), StringComparer.Ordinal));
localizedRenderingReleaseChecks += Check(
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(renderingReleasePackPath))) ==
    OcrShadowSemanticService.ExpectedRenderingPackSha256);
var releaseRenderer = new SlangGraphLocalizedRenderer(renderingReleasePackSet.CombinedGraph);
localizedRenderingReleaseChecks += Check(
    releaseRenderer.Render(
        "game.lol:lane.caution",
        "zh-CN",
        localizedCareMatch?.Slots,
        includeDraft: false).Text == "上路小心");
IReadOnlyList<InboxTriageResult> inboxSuggestions =
    OcrTextInboxTriageService.CreateSuggestions(catalog, inboxRecords, includeDraft: true);

ChatAnnotationEvaluation evaluation = ChatAnnotationEvaluator.Evaluate(
    catalog,
    annotations,
    includeDraft: true);
DataCoverageReport coverage = ChatDataQualityAnalyzer.Analyze(
    catalog.Dataset,
    annotations);
IReadOnlyList<DataCurationWorkItem> curationQueue =
    ChatDataCurationQueue.Create(catalog.Dataset, coverage);

Console.WriteLine($"Dataset: {catalog.Dataset.DatasetId} {catalog.Dataset.DatasetVersion}");
Console.WriteLine($"Graph pack: {graphPack.Header.PackId} {graphPack.Header.PackVersion}");
Console.WriteLine($"Graph domains: {graphPack.Domains.Count}");
Console.WriteLine($"Combined graph domains: {graphPackSet.CombinedGraph.Domains.Count}");
Console.WriteLine($"Dependency failure checks: {dependencyFailureChecks}/3");
Console.WriteLine($"Generic matcher checks: {genericMatcherChecks}/9");
Console.WriteLine($"Cross-domain pack checks: {crossDomainChecks}/5");
Console.WriteLine($"Batch-001 observation regression checks: {candidateRegressionChecks}/9");
Console.WriteLine($"Batch-001 candidate validation failures: {candidateValidationFailures.Count}");
foreach (string failure in candidateValidationFailures)
{
    Console.WriteLine($"- FAILURE {failure}");
}
Console.WriteLine($"Batch-001 deferred uncertain labels: {candidateDeferredLabels.Count}");
foreach (string deferredLabel in candidateDeferredLabels)
{
    Console.WriteLine($"- DEFERRED {deferredLabel}");
}
Console.WriteLine($"Batch-002 candidate validation failures: {candidate2ValidationFailures.Count}");
foreach (string failure in candidate2ValidationFailures)
{
    Console.WriteLine($"- FAILURE {failure}");
}
Console.WriteLine($"Batch-002 observation regression checks: {candidate2RegressionChecks}/12");
Console.WriteLine($"Second-round supplemental observation checks: {supplementalRegressionChecks}/4");
Console.WriteLine($"Release gate checks: {releaseGateChecks}/4");
Console.WriteLine($"0.3.0-rc.1 release gate checks: {releaseCandidateChecks}/2");
Console.WriteLine($"0.3.0-rc.1 integrity checks: {releaseCandidateIntegrityChecks}/5");
foreach (string failure in releaseCandidateIntegrityFailures)
{
    Console.WriteLine($"- RC INTEGRITY FAILURE {failure}");
}
Console.WriteLine($"0.3.0-rc.1 independent holdout checks: {releaseCandidateHoldoutChecks}/6");
foreach (string failure in releaseCandidateHoldoutFailures)
{
    Console.WriteLine($"- RC HOLDOUT FAILURE {failure}");
}
Console.WriteLine($"0.3.0 formal release checks: {releasePackChecks}/4");
Console.WriteLine($"OCR shadow adapter checks: {ocrShadowChecks}/110");
Console.WriteLine($"Continuous runtime trace/change-gate checks: {continuousRuntimeChecks}/50");
Console.WriteLine($"Domain Lexicon runtime fixture checks: {domainLexiconChecks}/11");
Console.WriteLine($"Domain Lexicon candidate-shadow compatibility checks: {candidateLexiconCompatibilityChecks}/7");
Console.WriteLine($"Localized rendering candidate checks: {localizedRenderingChecks}/9");
Console.WriteLine($"Localized rendering RC checks: {localizedRenderingRcChecks}/10");
Console.WriteLine($"Localized rendering formal release checks: {localizedRenderingReleaseChecks}/5");
foreach (SlangGraphReleaseGateItem item in releaseCandidateReport.Items.Where(item => !item.Ready))
{
    Console.WriteLine($"- RC GATE {item.RecordId}: {string.Join(", ", item.Reasons)}");
}
Console.WriteLine($"Annotations: {annotations.Count}");
Console.WriteLine($"Inbox candidates: {inboxCandidates.Count}");
Console.WriteLine($"Inbox suggestions: {inboxSuggestions.Sum(item => item.Suggestions.Count)}");
Console.WriteLine($"Passed: {evaluation.PassedCount}");
Console.WriteLine($"Failed: {evaluation.FailedCount}");
Console.WriteLine($"Runtime-ready concepts: {coverage.ReadyConceptCount}/{coverage.Concepts.Count}");
Console.WriteLine($"Blocked concepts: {coverage.BlockedConceptCount}");
Console.WriteLine($"Top curation item: {curationQueue.FirstOrDefault()?.ConceptId ?? "none"}");
foreach (string failedCheck in failedNamedChecks)
{
    Console.WriteLine($"- CHECK FAILURE {failedCheck}");
}

foreach (ChatAnnotationEvaluationItem item in evaluation.Items.Where(item => !item.Passed))
{
    Console.WriteLine($"- {item.AnnotationId}: {string.Join(" ", item.Problems)}");
}

return evaluation.FailedCount == 0 && dependencyFailureChecks == 3 && genericMatcherChecks == 9 && crossDomainChecks == 5 && candidateRegressionChecks == 9 && candidate2RegressionChecks == 12 && supplementalRegressionChecks == 4 && releaseGateChecks == 4 && releaseCandidateChecks == 2 && releaseCandidateIntegrityFailures.Count == 0 && releaseCandidateHoldoutFailures.Count == 0 && releaseCandidateIntegrityChecks == 5 && releaseCandidateHoldoutChecks == 6 && releasePackChecks == 4 && ocrShadowChecks == 110 && continuousRuntimeChecks == 50 && domainLexiconChecks == 11 && candidateLexiconCompatibilityChecks == 7 && localizedRenderingChecks == 9 && localizedRenderingRcChecks == 10 && localizedRenderingReleaseChecks == 5 ? 0 : 1;

static SlangGraphMatchContext ContextFor(ObservationRecord observation) =>
    new(observation.Community, observation.Channel);

static IReadOnlyList<ReleaseHoldoutCase> LoadReleaseHoldout(string path)
{
    var cases = new List<ReleaseHoldoutCase>();

    foreach (string line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
    {
        ReleaseHoldoutCase? holdoutCase = JsonSerializer.Deserialize<ReleaseHoldoutCase>(line);
        if (holdoutCase is null || !string.Equals(holdoutCase.RecordType, "holdout_case", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release holdout contains an invalid record.");
        }

        cases.Add(holdoutCase);
    }

    return cases;
}

static bool HasCompleteHoldoutMetadata(ReleaseHoldoutCase holdoutCase) =>
    !string.IsNullOrWhiteSpace(holdoutCase.CaseId) &&
    !string.IsNullOrWhiteSpace(holdoutCase.RawText) &&
    !string.IsNullOrWhiteSpace(holdoutCase.Locale) &&
    !string.IsNullOrWhiteSpace(holdoutCase.Community) &&
    !string.IsNullOrWhiteSpace(holdoutCase.Channel) &&
    !string.IsNullOrWhiteSpace(holdoutCase.Source) &&
    holdoutCase.ObservedAt is not null &&
    !string.IsNullOrWhiteSpace(holdoutCase.RedactionStatus) &&
    !string.IsNullOrWhiteSpace(holdoutCase.Provenance);

static IReadOnlyList<string> ValidateReleaseTraceability(SlangGraphPack pack)
{
    var failures = new List<string>();
    IReadOnlyDictionary<string, ReviewRecord> latestReviews = pack.Reviews
        .GroupBy(review => review.SubjectId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(review => review.ReviewedAt)
                .ThenByDescending(review => review.ReviewId, StringComparer.Ordinal)
                .First(),
            StringComparer.OrdinalIgnoreCase);

    foreach (string recordId in pack.Concepts.Select(item => item.ConceptId)
                 .Concat(pack.Expressions.Select(item => item.ExpressionId))
                 .Concat(pack.Entities.Select(item => item.EntityId))
                 .Concat(pack.Observations.Select(item => item.ObservationId))
                 .Concat(pack.Evidence.Select(item => item.EvidenceId)))
    {
        if (!latestReviews.TryGetValue(recordId, out ReviewRecord? review) ||
            !string.Equals(review.Decision, "approved", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"published record {recordId} does not have a latest approved review.");
        }
    }

    return failures;
}

static int ExpectInvalidData(Action action)
{
    try
    {
        action();
        return 0;
    }
    catch (InvalidDataException)
    {
        return 1;
    }
}

static OllamaLocalTranslationProvider CreateSafetyProvider(
    OllamaTranslationProfile profile,
    string responseJson) =>
    new(
        profile,
        new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.OK, responseJson))
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/")
        });

int Check(bool passed, [CallerArgumentExpression(nameof(passed))] string expression = "")
{
    if (!passed) failedNamedChecks.Add(expression.ReplaceLineEndings(" "));
    return passed ? 1 : 0;
}

static bool SlotValueIs(SlangGraphSemanticMatch? match, string slotName, string expectedValue)
{
    return match is not null &&
           match.Slots.TryGetValue(slotName, out IReadOnlyList<SlangGraphSlotValue>? values) &&
           values.Any(value => string.Equals(value.Value, expectedValue, StringComparison.OrdinalIgnoreCase));
}

static IReadOnlyList<string> ValidateCandidatePack(
    SlangGraphPackSet packSet,
    SlangGraphPack candidatePack,
    string expectedPackId = "pack.game.lol.chat.batch-001",
    string expectedPackVersion = "0.2.0-candidate.1")
{
    var failures = new List<string>();

    if (!string.Equals(candidatePack.Header.PackId, expectedPackId, StringComparison.OrdinalIgnoreCase))
    {
        failures.Add($"pack header has unexpected pack_id '{candidatePack.Header.PackId}'.");
    }

    if (!string.Equals(candidatePack.Header.PackVersion, expectedPackVersion, StringComparison.OrdinalIgnoreCase))
    {
        failures.Add($"pack header has unexpected pack_version '{candidatePack.Header.PackVersion}'.");
    }

    if (!string.Equals(candidatePack.Header.SchemaVersion, "slang-graph/0.2", StringComparison.OrdinalIgnoreCase))
    {
        failures.Add($"pack header has unexpected schema_version '{candidatePack.Header.SchemaVersion}'.");
    }

    if (!string.Equals(candidatePack.Header.Status, "draft", StringComparison.OrdinalIgnoreCase))
    {
        failures.Add($"pack header must remain draft, found '{candidatePack.Header.Status}'.");
    }

    if (!candidatePack.Header.Dependencies.Any(dependency =>
            string.Equals(dependency.PackId, "pack.core.communication", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(dependency.VersionRange, "0.2.*", StringComparison.OrdinalIgnoreCase)))
    {
        failures.Add("pack header is missing dependency pack.core.communication 0.2.*.");
    }

    var typedIds = new List<(string Kind, string Id)>();
    typedIds.AddRange(candidatePack.Domains.Select(item => ("domain", item.DomainId)));
    typedIds.AddRange(candidatePack.Concepts.Select(item => ("concept", item.ConceptId)));
    typedIds.AddRange(candidatePack.Expressions.Select(item => ("expression", item.ExpressionId)));
    typedIds.AddRange(candidatePack.Patterns.Select(item => ("pattern", item.PatternId)));
    typedIds.AddRange(candidatePack.Entities.Select(item => ("entity", item.EntityId)));
    typedIds.AddRange(candidatePack.Relations.Select(item => ("relation", item.RelationId)));
    typedIds.AddRange(candidatePack.Sources.Select(item => ("source", item.SourceId)));
    typedIds.AddRange(candidatePack.Evidence.Select(item => ("evidence", item.EvidenceId)));
    typedIds.AddRange(candidatePack.Observations.Select(item => ("observation", item.ObservationId)));

    foreach (string emptyId in typedIds.Where(item => string.IsNullOrWhiteSpace(item.Id)).Select(item => item.Kind))
    {
        failures.Add($"{emptyId} contains an empty ID.");
    }

    foreach (IGrouping<string, (string Kind, string Id)> duplicate in typedIds
                 .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                 .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                 .Where(group => group.Count() > 1))
    {
        failures.Add($"duplicate ID '{duplicate.Key}' across {string.Join(", ", duplicate.Select(item => item.Kind))}.");
    }

    HashSet<string> entityIds = candidatePack.Entities
        .Select(entity => entity.EntityId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, EntityRecord> entitiesById = candidatePack.Entities
        .ToDictionary(entity => entity.EntityId, StringComparer.OrdinalIgnoreCase);
    Dictionary<string, ConceptRecord> conceptsById = candidatePack.Concepts
        .ToDictionary(concept => concept.ConceptId, StringComparer.OrdinalIgnoreCase);

    foreach (ObservationRecord observation in candidatePack.Observations)
    {
        foreach (ObservationLabel label in observation.Labels)
        {
            foreach (KeyValuePair<string, string> slot in label.Slots)
            {
                if (!entityIds.Contains(slot.Value))
                {
                    failures.Add(
                        $"{observation.ObservationId}.{slot.Key} references unknown entity '{slot.Value}'.");
                    continue;
                }

                if (conceptsById.TryGetValue(label.ConceptId, out ConceptRecord? concept))
                {
                    SlotDefinition? definition = concept.SlotSchema.FirstOrDefault(item =>
                        string.Equals(item.Name, slot.Key, StringComparison.OrdinalIgnoreCase));

                    if (definition is not null &&
                        !definition.EffectiveEntityTypes.Contains(
                            entitiesById[slot.Value].EntityType,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        failures.Add(
                            $"{observation.ObservationId}.{slot.Key} entity '{slot.Value}' has type " +
                            $"'{entitiesById[slot.Value].EntityType}', outside the slot's allowed entity_types.");
                    }
                }
            }
        }
    }

    if (!packSet.Packs.Any(pack =>
            string.Equals(pack.Header.PackId, "pack.core.communication", StringComparison.OrdinalIgnoreCase)))
    {
        failures.Add("dependency pack.core.communication was not loaded.");
    }

    return failures;
}

static bool SlotsMatch(
    SlangGraphSemanticMatch match,
    IReadOnlyDictionary<string, string> expectedSlots)
{
    foreach (KeyValuePair<string, string> expectedSlot in expectedSlots)
    {
        if (!match.Slots.TryGetValue(expectedSlot.Key, out IReadOnlyList<SlangGraphSlotValue>? values) ||
            !values.Any(value => string.Equals(value.Value, expectedSlot.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
    }

    return true;
}

static string FormatSlots(IReadOnlyDictionary<string, string> slots)
{
    return slots.Count == 0
        ? "{}"
        : "{" + string.Join(", ", slots.Select(item => $"{item.Key}={item.Value}")) + "}";
}

static string FormatMatches(IReadOnlyList<SlangGraphSemanticMatch> matches)
{
    return matches.Count == 0
        ? "none"
        : string.Join(", ", matches.Select(match => $"{match.ConceptId} {FormatSlots(match.Slots.ToDictionary(item => item.Key, item => item.Value.FirstOrDefault()?.Value ?? "", StringComparer.OrdinalIgnoreCase))}"));
}

sealed record ReleaseHoldoutCase(
    [property: JsonPropertyName("record_type")] string RecordType,
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("raw_text")] string RawText,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("community")] string Community,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt,
    [property: JsonPropertyName("redaction_status")] string RedactionStatus,
    [property: JsonPropertyName("provenance")] string Provenance,
    [property: JsonPropertyName("expected_concept_id")] string? ExpectedConceptId,
    [property: JsonPropertyName("expected_slots")] IReadOnlyDictionary<string, string> ExpectedSlots,
    [property: JsonPropertyName("forbidden_concept_ids")] IReadOnlyList<string> ForbiddenConceptIds,
    [property: JsonPropertyName("require_evidence_chain")] bool RequireEvidenceChain);

sealed class RecordingLocalTranslationProvider : ILocalTranslationAttemptProvider
{
    public GeneralTranslationRequest? LastRequest { get; private set; }

    public GeneralTranslationResult Translate(GeneralTranslationRequest request)
    {
        LocalTranslationAttempt attempt = TranslateWithDiagnostics(request);
        return new GeneralTranslationResult("succeeded", attempt.FinalOutput, "recording-test", null);
    }

    public LocalTranslationAttempt TranslateWithDiagnostics(GeneralTranslationRequest request)
    {
        LastRequest = request;
        string response = request.Text + "，继续。";
        return new LocalTranslationAttempt(
            "recording-test",
            "recording-test",
            "recording-test-digest",
            "recording-test-template",
            request.Text,
            request.Text,
            response,
            response,
            0,
            null,
            UsedFallback: false);
    }
}

sealed class CorruptMarkerTranslationProvider : ILocalTranslationAttemptProvider
{
    public GeneralTranslationResult Translate(GeneralTranslationRequest request)
    {
        LocalTranslationAttempt attempt = TranslateWithDiagnostics(request);
        return new GeneralTranslationResult("succeeded", attempt.FinalOutput, "corrupt-marker-test", null);
    }

    public LocalTranslationAttempt TranslateWithDiagnostics(GeneralTranslationRequest request)
    {
        string output = request.Text.Replace("[[SG_LOCKED_1]]", "[[SG_LOCKED_1]", StringComparison.Ordinal);
        return new LocalTranslationAttempt(
            "corrupt-marker-test",
            "corrupt-marker-test",
            "corrupt-marker-test-digest",
            "corrupt-marker-test-template",
            request.Text,
            request.Text,
            output,
            output,
            0,
            null,
            UsedFallback: false);
    }
}

sealed class StaticHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public StaticHttpMessageHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
    }
}

sealed class ThrowingHttpMessageHandler : HttpMessageHandler
{
    private readonly Exception _exception;

    public ThrowingHttpMessageHandler(Exception exception)
    {
        _exception = exception;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(_exception);
}
