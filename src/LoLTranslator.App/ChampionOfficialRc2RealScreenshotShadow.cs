using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>
/// Explicit, default-off bridge for real screenshot diagnostics.  RC.2 remains
/// candidate-only: this service never makes a formal match and never changes a
/// Pack.  It loads only the reviewed official champion-name RC.
/// </summary>
public sealed class ChampionOfficialRc2RealScreenshotShadowService
{
    public const string ExpectedRcSha256 = "040181DD0464CCE8D1131A1AA47AB0D2A0B07CA69D4DEADD3316A626A1FFB5EE";
    public const string ExpectedRcVersion = "0.1.0-champion-official-rc.2";
    private static readonly string[] ScopeTags = ["league_of_legends", "champion", "in_game_chat"];
    private static readonly Regex LatinScript = new(@"[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex CjkScript = new(@"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]", RegexOptions.Compiled);
    // This guards an ordinary English construction without naming any champion.
    // It is intentionally conservative: uncertain common-word uses are not locked.
    private static readonly Regex OrdinaryEnglishContinuation = new(
        @"^\s+(?:new|logo|product|products|market|marketing|identity|value|values|message|design|style|strategy)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly DomainLexiconIndex _index;
    private readonly OcrShadowSemanticService _semanticService;
    private readonly DomainLexiconRcShadowProcessor _processor;
    private readonly ProtectedHybridTranslationAggregator _aggregator;
    private readonly RuntimeTypedNormalizationCandidate _normalization;

    private ChampionOfficialRc2RealScreenshotShadowService(
        DomainLexiconIndex index,
        OcrShadowSemanticService semanticService,
        DomainLexiconRcShadowProcessor processor,
        ProtectedHybridTranslationAggregator aggregator,
        RuntimeTypedNormalizationCandidate normalization)
    {
        _index = index;
        _semanticService = semanticService;
        _processor = processor;
        _aggregator = aggregator;
        _normalization = normalization;
    }

    public static ChampionOfficialRc2RealScreenshotShadowService CreateExplicit(
        OcrShadowSemanticService semanticService,
        string? rcPath = null,
        ILocalTranslationAttemptProvider? provider = null,
        string? normalizationArtifactPath = null,
        string? normalizationReviewPath = null)
    {
        ArgumentNullException.ThrowIfNull(semanticService);
        string resolvedPath = rcPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "data", "packs", "lol", "domain-lexicon.v0.1.champion-official.rc.2.jsonl");
        string resolvedNormalizationArtifact = normalizationArtifactPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "collection", "lol", "real-screenshot-shadow-001", "runtime-normalization-candidate.jsonl");
        string resolvedNormalizationReview = normalizationReviewPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "collection", "lol", "real-screenshot-shadow-001", "run005-review-normalization.jsonl");
        ValidateRc(resolvedPath);
        RuntimeTypedNormalizationCandidate normalization = RuntimeTypedNormalizationCandidate.LoadExplicit(
            resolvedNormalizationArtifact, resolvedNormalizationReview);
        DomainLexiconIndex index = new(DomainLexiconLoader.LoadJsonl(resolvedPath));
        ILocalTranslationAttemptProvider resolvedProvider = provider ??
            ControlledShadowQwen3RuntimeFactory.Create(ControlledShadowQwen3Profile.ExplicitTestOnly()).GeneralTranslationProvider
                as ILocalTranslationAttemptProvider
            ?? throw new InvalidOperationException("Qwen3 controlled-shadow provider does not support protected attempts.");
        var aggregator = new ProtectedHybridTranslationAggregator(resolvedProvider);
        var orchestrator = new ChatProcessingRouteOrchestrator(semanticService, resolvedProvider, aggregator);
        return new ChampionOfficialRc2RealScreenshotShadowService(
            index,
            semanticService,
            new DomainLexiconRcShadowProcessor(index, DomainLexiconRcShadowProfile.ExplicitTestOnly(), orchestrator),
            aggregator,
            normalization);
    }

    public ChampionOfficialRc2RealScreenshotShadowResult Process(
        IReadOnlyList<OcrShadowLineResult> fusedLines,
        ShadowMatchContextOption context,
        string targetLocale)
    {
        ArgumentNullException.ThrowIfNull(fusedLines);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);

        var results = new List<ChampionOfficialRc2RealScreenshotShadowLine>();
        foreach (OcrShadowLineResult line in fusedLines)
        {
            ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(line.Text);
            string messageBody = envelope.Body;
            if (line.Fusion.IsAmbiguous)
            {
                results.Add(new ChampionOfficialRc2RealScreenshotShadowLine(
                    line.LineNumber, line.Text, envelope.Prefix, messageBody, SourceLocales(line), line.Fusion, "fusion_ambiguous", null,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RuntimeTypedEntityNormalization>(), null, null, null, 0, "fusion_ambiguous_no_provider_call"));
                continue;
            }

            if (!IsLolInGameChatContext(context))
            {
                results.Add(new ChampionOfficialRc2RealScreenshotShadowLine(
                    line.LineNumber, line.Text, envelope.Prefix, messageBody, SourceLocales(line), line.Fusion, "shadow_context_incompatible", null,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RuntimeTypedEntityNormalization>(), null, null, null, 0,
                    "shadow_context_incompatible_no_provider_call"));
                continue;
            }

            IReadOnlyList<string> candidateLocales = ResolveCandidateLocales(line);
            if (candidateLocales.Count == 0)
            {
                results.Add(new ChampionOfficialRc2RealScreenshotShadowLine(
                    line.LineNumber, line.Text, envelope.Prefix, messageBody, SourceLocales(line), line.Fusion, "unsupported_ocr_locale", null,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RuntimeTypedEntityNormalization>(), null, null, null, 0, "official_rc2_supports_en_US_and_zh_CN_only"));
                continue;
            }

            // The candidate is intentionally scoped to the already opt-in RC.2
            // champion shadow flow.  A raw, exact official-name preflight is an
            // additional safety anchor: a bare OCR token must not become a
            // general-purpose correction just because the game context was set.
            RuntimeTypedNormalizationResult typedNormalization = _normalization.NormalizeExact(messageBody, context);
            if (typedNormalization.Applied && !HasOfficialEntityAnchor(messageBody, candidateLocales))
            {
                typedNormalization = RuntimeTypedNormalizationResult.NoMatch(
                    messageBody,
                    "normalization_requires_official_entity_anchor");
            }
            if (string.Equals(typedNormalization.FailureReason, "normalization_approximate_surface_requires_review", StringComparison.Ordinal))
            {
                results.Add(new ChampionOfficialRc2RealScreenshotShadowLine(
                    line.LineNumber, line.Text, envelope.Prefix, messageBody, SourceLocales(line), line.Fusion, "normalization_requires_review", null,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RuntimeTypedEntityNormalization>(), null, null, null, 0,
                    typedNormalization.FailureReason));
                continue;
            }

            // `top` is already a verified lane entity in the frozen formal
            // Pack. Resolve it through the generic Pack entity API only when
            // this line did not consume the separate candidate t0P surface.
            // This is diagnostic typed-slot metadata, not a new expression or
            // a Qwen-protected rendering.
            IReadOnlyList<SlangGraphEntityResolution> verifiedTypedEntities = typedNormalization.Applied
                ? Array.Empty<SlangGraphEntityResolution>()
                : candidateLocales
                    .SelectMany(locale => _semanticService.ResolveVerifiedEntities(
                        messageBody,
                        locale,
                        context,
                        new[] { "lane" }))
                    .GroupBy(entity => entity.EntityId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(entity => entity.MatchedText.Length).First())
                    .ToArray();
            HybridLockedSpan[] verifiedTypedLocks = verifiedTypedEntities
                .Select((entity, index) =>
                {
                    SlangGraphRenderedEntity rendering = _semanticService.RenderVerifiedEntity(entity.EntityId, targetLocale);
                    return new HybridLockedSpan(
                        entity.Start,
                        entity.Length,
                        $"[[SG_ENTITY_LOCKED_{index + 1}]]",
                        rendering.Text);
                })
                .ToArray();
            HybridLockedSpan[] candidateTypedLocks = typedNormalization.Entity is { } candidateEntity
                ? new[]
                {
                    new HybridLockedSpan(
                        candidateEntity.Start,
                        candidateEntity.Length,
                        "[[SG_ENTITY_LOCKED_1]]",
                        _semanticService.RenderVerifiedEntity(candidateEntity.TargetEntity, targetLocale).Text)
                }
                : Array.Empty<HybridLockedSpan>();
            HybridLockedSpan[] typedEntityLocks = candidateTypedLocks.Concat(verifiedTypedLocks).ToArray();

            var selected = candidateLocales
                .Select(locale => (SourceLocale: locale, Match: _index.Match(
                    messageBody,
                    locale,
                    LexiconContext(),
                    new DomainLexiconMatchOptions(IncludeCandidateShadow: true))))
                .Where(attempt => attempt.Match.CandidateShadowMatches.Count > 0)
                .OrderByDescending(attempt => attempt.Match.CandidateShadowMatches.Count)
                .FirstOrDefault();
            DomainLexiconMatchResult? preflight = selected.Match;
            if (preflight is null || preflight.CandidateShadowMatches.Count == 0)
            {
                results.Add(new ChampionOfficialRc2RealScreenshotShadowLine(
                    line.LineNumber, line.Text, envelope.Prefix, messageBody, SourceLocales(line), line.Fusion, "no_official_champion_match", null,
                    Array.Empty<string>(), Array.Empty<string>(), AppliedNormalizations(typedNormalization), null, null, null, 0,
                    preflight is null || preflight.AmbiguityReasons.Count == 0 ? "no_rc2_candidate_match_no_provider_call" : string.Join(";", preflight.AmbiguityReasons),
                    VerifiedTypedEntities: verifiedTypedEntities));
                continue;
            }

            if (IsOrdinaryEnglishCommonWordUse(messageBody, preflight.CandidateShadowMatches))
            {
                results.Add(new ChampionOfficialRc2RealScreenshotShadowLine(
                    line.LineNumber, line.Text, envelope.Prefix, messageBody, SourceLocales(line), line.Fusion, "common_word_context_requires_review", selected.SourceLocale,
                    preflight.CandidateShadowMatches.Select(match => match.CanonicalId).ToArray(), Array.Empty<string>(), AppliedNormalizations(typedNormalization), null, null, null, 0,
                    "ordinary_english_common_word_context_no_provider_call",
                    VerifiedTypedEntities: verifiedTypedEntities));
                continue;
            }

            DomainLexiconRcShadowResult processed = _processor.Process(
                new ChatProcessingRequest(
                    messageBody,
                    selected.SourceLocale,
                    targetLocale,
                    context,
                    new ChatProcessingPolicy(ChatProcessingRoutes.GeneralTranslation,
                        new ChatSemanticPolicy("optional", Array.Empty<string>(), Array.Empty<string>())),
                    typedEntityLocks),
                LexiconContext());
            LocalTranslationAttempt? attempt = _aggregator.LastAttempt;
            string status = processed.Processing.Status;
            string? output = processed.Processing.OutputText;
            string? failure = attempt?.Error ?? processed.Processing.HybridAggregation?.FailureReason;
            if (string.Equals(failure, "ollama_local_provider_error:HttpRequestException", StringComparison.Ordinal))
            {
                failure = "local_provider_unavailable";
            }
            if (status == "hybrid_completed" &&
                output is not null &&
                ChatMessageEnvelopeExtractor.Extract(output).HasHighConfidencePrefix)
            {
                status = "hybrid_unavailable";
                output = null;
                failure = "model_added_chat_envelope";
            }
            results.Add(new ChampionOfficialRc2RealScreenshotShadowLine(
                line.LineNumber,
                line.Text,
                envelope.Prefix,
                messageBody,
                SourceLocales(line),
                line.Fusion,
                status,
                selected.SourceLocale,
                processed.RcShadowMatches.Select(match => match.CanonicalId).ToArray(),
                processed.RcLockedSpans.Select(span => span.VerifiedRendering)
                    .Concat(typedEntityLocks.Select(span => span.VerifiedRendering)).ToArray(),
                AppliedNormalizations(typedNormalization),
                _aggregator.LastProtectedInput,
                attempt?.RawOutput,
                output,
                attempt?.ElapsedMilliseconds ?? 0,
                failure,
                verifiedTypedEntities));
        }

        return new ChampionOfficialRc2RealScreenshotShadowResult(
            "champion_official_rc2_real_screenshot_shadow.v0.1",
            ExpectedRcSha256,
            targetLocale,
            results,
            results.Count(line => line.Status == "hybrid_completed"),
            results.Count(line => line.FailureReason?.Contains("no_provider_call", StringComparison.Ordinal) == true),
            results.Count(line => line.Status == "hybrid_unavailable"));
    }

    public static string FormatDisplay(ChampionOfficialRc2RealScreenshotShadowResult result)
    {
        var lines = new List<string>
        {
            "RC.2 官方英雄名 + Qwen3 本地 Shadow（已显式启用，非正式输出）",
            $"RC.2 SHA-256: {result.RcSha256}",
            $"目标语言: {result.TargetLocale}",
            "说明: 只对可靠融合且命中官方英雄名的行调用本地 Qwen3；其余行不调用模型。",
            string.Empty
        };
        foreach (ChampionOfficialRc2RealScreenshotShadowLine line in result.Lines)
        {
            lines.Add($"第 {line.LineNumber} 行：{line.Status}");
            lines.Add($"  OCR 来源: {string.Join(", ", line.OcrLocales)}；融合: {line.Fusion.Status} ({line.Fusion.Confidence:0.00})");
            if (line.ChatPrefix is not null) lines.Add($"  原聊天前缀: {line.ChatPrefix}");
            if (line.CanonicalIds.Count > 0)
            {
                lines.Add($"  官方实体: {string.Join(", ", line.CanonicalIds)}");
                lines.Add($"  锁定本地化: {string.Join("；", line.LockedRenderings)}");
            }
            foreach (RuntimeTypedEntityNormalization normalization in line.TypedNormalizations)
            {
                lines.Add($"  Typed normalization: {normalization.TargetEntity} ({normalization.TargetSlot})");
            }
            foreach (SlangGraphEntityResolution entity in line.VerifiedTypedEntities ?? Array.Empty<SlangGraphEntityResolution>())
            {
                lines.Add($"  Verified typed entity: {entity.EntityId} ({entity.EntityType})");
            }
            if (line.FinalOutput is not null) lines.Add($"  Shadow 输出: {line.FinalOutput}");
            if (line.FailureReason is not null) lines.Add($"  未输出原因: {line.FailureReason}");
            lines.Add($"  耗时: {line.ElapsedMilliseconds} ms");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static DomainLexiconMatchContext LexiconContext() => new(null, null, null, "16.15.1", ScopeTags);

    private static bool IsLolInGameChatContext(ShadowMatchContextOption context) =>
        string.Equals(context.Channel, "in_game_chat", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(context.Community) &&
        context.Community.StartsWith("lol.", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SourceLocales(OcrShadowLineResult line) =>
        line.Sources.Select(source => source.RecognizerLanguageTag).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyList<string> ResolveCandidateLocales(OcrShadowLineResult line)
    {
        var locales = new List<string>();
        foreach (string locale in SourceLocales(line))
        {
            if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)) locales.Add("en-US");
            if (locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) locales.Add("zh-CN");
        }
        // OCR engines can correctly read a foreign official surface while
        // reporting their own UI locale. Script merely widens the two frozen,
        // official alias locales; matching remains exact and scoped.
        if (LatinScript.IsMatch(line.Text)) locales.Add("en-US");
        if (CjkScript.IsMatch(line.Text)) locales.Add("zh-CN");
        return locales.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsOrdinaryEnglishCommonWordUse(
        string text,
        IReadOnlyList<DomainLexiconEntityMatch> matches) =>
        matches.Any(match =>
            string.Equals(match.Locale, "en-US", StringComparison.OrdinalIgnoreCase) &&
            match.AliasSurface.All(character => character <= 127 && char.IsLetter(character)) &&
            OrdinaryEnglishContinuation.IsMatch(text[(match.Start + match.Length)..]));

    private bool HasOfficialEntityAnchor(string messageBody, IReadOnlyList<string> candidateLocales) =>
        candidateLocales.Any(locale => _index.Match(
                messageBody,
                locale,
                LexiconContext(),
                new DomainLexiconMatchOptions(IncludeCandidateShadow: true))
            .CandidateShadowMatches.Count > 0);

    private static IReadOnlyList<RuntimeTypedEntityNormalization> AppliedNormalizations(RuntimeTypedNormalizationResult result) =>
        result.Applied && result.Entity is not null ? new[] { result.Entity } : Array.Empty<RuntimeTypedEntityNormalization>();

    internal static void ValidateRc(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 Champion Official RC.2。", path);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(hash, ExpectedRcSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Champion Official RC.2 哈希不匹配。");
        DomainLexicon lexicon = DomainLexiconLoader.LoadJsonl(path);
        if (lexicon.Header.LexiconVersion != ExpectedRcVersion || lexicon.Entries.Count != 173 ||
            lexicon.Entries.Any(entry => entry.Kind != "entity" || entry.Type != "champion" || entry.Aliases.Count != 2 || entry.LocalizedRenderings.Count != 2 ||
                entry.Aliases.Any(alias => alias.Register != "official" || alias.FormalEligibility != "candidate_only")))
        {
            throw new InvalidDataException("Champion Official RC.2 范围或正式资格不符合 shadow 约束。");
        }
    }
}

public sealed record ChampionOfficialRc2RealScreenshotShadowResult(
    string SessionType,
    string RcSha256,
    string TargetLocale,
    IReadOnlyList<ChampionOfficialRc2RealScreenshotShadowLine> Lines,
    int ModelCallCount,
    int NoProviderCallCount,
    int FailClosedCount);

public sealed record ChampionOfficialRc2RealScreenshotShadowLine(
    int LineNumber,
    string OcrText,
    string? ChatPrefix,
    string MessageBody,
    IReadOnlyList<string> OcrLocales,
    OcrFusionMetadata Fusion,
    string Status,
    string? SourceLocale,
    IReadOnlyList<string> CanonicalIds,
    IReadOnlyList<string> LockedRenderings,
    IReadOnlyList<RuntimeTypedEntityNormalization> TypedNormalizations,
    string? ProtectedInput,
    string? RawProviderOutput,
    string? FinalOutput,
    long ElapsedMilliseconds,
    string? FailureReason,
    IReadOnlyList<SlangGraphEntityResolution>? VerifiedTypedEntities = null);

/// <summary>
/// Keeps a high-confidence chat envelope out of both entity matching and the
/// local translation provider. It is domain-neutral and deliberately requires
/// a timestamp plus sender/channel, or a channel plus sender, before removal.
/// </summary>
public sealed record ChatMessageEnvelope(
    string? Prefix, string Body, bool HasHighConfidencePrefix,
    string? Timestamp = null, string? Channel = null, string? PlayerOrRole = null,
    bool RepairedMisplacedDelimiter = false)
{
    public bool RecoveredMissingDelimiter { get; init; }
}

public static class ChatMessageEnvelopeExtractor
{
    // Recover only an explicit chat channel followed by a complete identity
    // slot. No guessing of timestamps, missing parentheses or arbitrary prose.
    private static readonly Regex ChannelChampionWithoutDelimiter = new(
        @"^\s*(?:(?<timestamp>[0-9OoIl]{1,2}\s*[:：.]\s*[0-9OoIl]{2})\s*)?(?<channel>\[(?:Team|All|队伍|隊伍|所有人|팀|전체)\])\s*(?<sender>[^:：\r\n\[\]]{1,112}?[\(（][^\)）\r\n]{1,48}[\)）])\s+(?<body>[^:：\s][^\r\n]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Player names are opaque identifiers, not natural-language text. Accept
    // digits, mixed scripts and punctuation without trying to decide whether
    // the name itself looks meaningful. Timestamp/channel + the trailing
    // parenthesized champion + the final body delimiter provide the safety
    // anchors. The opaque identity is preserved outside the translated body.
    private static readonly Regex PlayerChampionEnvelope = new(
        @"^\s*(?:(?<timestamp>[0-9OoIl]{1,2}\s*[:：.]\s*[0-9OoIl]{2})\s*)?(?:(?<channel>\[[^\]\r\n]{1,24}\])\s*)?(?<sender>[^:：\r\n]{0,112}?[\(（][^\)）\r\n]{1,48}[\)）])\s*[:：]\s*(?<body>\S[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MisplacedDelimiterPlayerChampionEnvelope = new(
        @"^\s*(?<timestamp>[0-9OoIl]{1,2}\s*[:：.]\s*[0-9OoIl]{2})\s*(?:(?<channel>\[[^\]\r\n]{1,24}\])\s*)?[:：]\s*(?<sender>[^:：\r\n]{0,112}?[\(（][^\)）\r\n]{1,48}[\)）])\s+(?<body>\S[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StructuredEnvelope = new(
        @"^\s*(?:(?<timestamp>\d{1,2}\s*[:：]\s*\d{2})\s*)?(?<channel>\[[^\]\r\n]{1,24}\])\s*(?<sender>[\(（][^\)）\r\n]{1,48}[\)）])\s*[:：]\s*(?<body>\S[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Envelope = new(
        @"^\s*(?:(?<timestamp>\d{1,2}\s*[:：]\s*\d{2})\s*)?(?:(?<channel>\[[^\]\r\n]{1,24}\])\s*)?(?:(?<sender>\([^\)\r\n]{1,48}\)|[^\[\]:：\r\n]{1,112})\s*)?[:：]\s*(?<body>\S[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OcrDamagedEnvelope = new(
        @"^\s*(?<timestamp>[0-9Oo]{1,2}\s*[:：.]\s*[0-9Oo]{2})\s+(?<prefix>.{2,112}?)\s*[:：]\s*(?<body>\S[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ChatMessageEnvelope Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ChatMessageEnvelope(null, text, false);
        Match missingDelimiter = ChannelChampionWithoutDelimiter.Match(text);
        if (missingDelimiter.Success)
        {
            Group body = missingDelimiter.Groups["body"];
            return new ChatMessageEnvelope(text[..body.Index], body.Value.Trim(), true,
                missingDelimiter.Groups["timestamp"].Value, missingDelimiter.Groups["channel"].Value,
                missingDelimiter.Groups["sender"].Value.Trim()) { RecoveredMissingDelimiter = true };
        }
        Match playerChampion = PlayerChampionEnvelope.Match(text);
        if (playerChampion.Success &&
            (playerChampion.Groups["timestamp"].Success || playerChampion.Groups["channel"].Success))
        {
            Group body = playerChampion.Groups["body"];
            return new ChatMessageEnvelope(text[..body.Index], body.Value.Trim(), true,
                playerChampion.Groups["timestamp"].Value,
                playerChampion.Groups["channel"].Value,
                playerChampion.Groups["sender"].Value.Trim());
        }
        // OCR engines can emit the colon fragment before the player identity,
        // especially on compressed/low-resolution streams. Recover only the
        // strongly anchored form: timestamp, optional channel, misplaced
        // delimiter, opaque player identity ending in (Champion), then body.
        // The repaired identity stays outside translation and can inherit the
        // original source colour like a normally ordered chat envelope.
        Match misplacedDelimiter = MisplacedDelimiterPlayerChampionEnvelope.Match(text);
        if (misplacedDelimiter.Success)
        {
            string timestamp = misplacedDelimiter.Groups["timestamp"].Value.Trim();
            string channel = misplacedDelimiter.Groups["channel"].Value.Trim();
            string sender = misplacedDelimiter.Groups["sender"].Value.Trim();
            string prefix = string.Join(' ', new[] { timestamp, channel, sender }
                .Where(value => !string.IsNullOrWhiteSpace(value))) + "：";
            return new ChatMessageEnvelope(
                prefix,
                misplacedDelimiter.Groups["body"].Value.Trim(),
                true,
                timestamp,
                channel,
                sender,
                RepairedMisplacedDelimiter: true);
        }
        Match structured = StructuredEnvelope.Match(text);
        if (structured.Success)
        {
            Group structuredBody = structured.Groups["body"];
            return new ChatMessageEnvelope(text[..structuredBody.Index], structuredBody.Value.Trim(), true,
                structured.Groups["timestamp"].Value, structured.Groups["channel"].Value, structured.Groups["sender"].Value);
        }
        Match match = Envelope.Match(text);
        if (match.Success)
        {
            bool timestamp = match.Groups["timestamp"].Success;
            bool channel = match.Groups["channel"].Success;
            bool sender = match.Groups["sender"].Success;
            if ((timestamp && (channel || sender)) || (channel && sender))
            {
                Group body = match.Groups["body"];
                return new ChatMessageEnvelope(text[..body.Index], body.Value.Trim(), true,
                    match.Groups["timestamp"].Value, match.Groups["channel"].Value, match.Groups["sender"].Value);
            }
        }

        // Paddle can confuse 0/O, punctuation, or one channel bracket while
        // retaining the timestamp and final sender/body delimiter. Those two
        // ordered anchors are sufficient to strip the damaged prefix without
        // treating an arbitrary colon-containing sentence as an envelope.
        Match damagedOcr = OcrDamagedEnvelope.Match(text);
        if (damagedOcr.Success)
        {
            Group body = damagedOcr.Groups["body"];
            return new ChatMessageEnvelope(text[..body.Index], body.Value.Trim(), true,
                damagedOcr.Groups["timestamp"].Value);
        }

        return TryExtractDamagedTimestampEnvelope(text, out ChatMessageEnvelope damaged)
            ? damaged
            : new ChatMessageEnvelope(null, text, false);
    }

    // OCR can drop a closing channel bracket or the first timestamp digit. A
    // fallback remains high-confidence only when it sees a leading damaged
    // timestamp marker, channel opener, parenthesized speaker, and a final
    // body delimiter in that order. It is generic chat structure, not a game
    // parser, and deliberately never splits an arbitrary colon-containing line.
    private static bool TryExtractDamagedTimestampEnvelope(string text, out ChatMessageEnvelope envelope)
    {
        envelope = new ChatMessageEnvelope(null, text, false);
        int cursor = 0;
        SkipWhitespace(text, ref cursor);
        if (cursor >= text.Length || !IsDelimiter(text[cursor])) return false;
        cursor++;
        SkipWhitespace(text, ref cursor);

        int digits = 0;
        while (cursor < text.Length && char.IsDigit(text[cursor]) && digits < 3)
        {
            cursor++;
            digits++;
        }
        if (digits is < 1 or > 2) return false;
        int afterTimestamp = cursor;
        SkipWhitespace(text, ref cursor);
        if (cursor == afterTimestamp || cursor >= text.Length || text[cursor] != '[') return false;
        cursor++;

        int senderOpen = text.IndexOf('(', cursor);
        if (senderOpen < 0 || senderOpen - cursor is < 1 or > 96) return false;
        int senderClose = text.IndexOf(')', senderOpen + 1);
        if (senderClose < 0 || senderClose - senderOpen - 1 is < 1 or > 48) return false;
        cursor = senderClose + 1;
        SkipWhitespace(text, ref cursor);
        if (cursor >= text.Length || !IsDelimiter(text[cursor])) return false;
        cursor++;
        SkipWhitespace(text, ref cursor);
        if (cursor >= text.Length || char.IsWhiteSpace(text[cursor])) return false;

        envelope = new ChatMessageEnvelope(text[..cursor], text[cursor..].Trim(), true);
        return true;
    }

    private static bool IsDelimiter(char value) => value is ':' or '\uFF1A';

    private static void SkipWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
    }
}

/// <summary>
/// A local-only, opt-in audit record. It deliberately excludes screenshots,
/// absolute paths, raw OCR candidates, player prefixes, and Evidence fields.
/// </summary>
public static class ChampionOfficialRc2RealScreenshotShadowLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly Regex ChatPrefix = new(@"^\s*(?:\d{1,2}\s*[:：]\s*\d{2}\s*)?(?:\[[^\]\r\n]{1,24}\]\s*)?(?:\([^\)\r\n]{1,40}\)\s*)?[^:：\r\n]{1,40}[:：]\s*", RegexOptions.Compiled);
    private static readonly Regex AbsolutePath = new(@"(?ix)(?:\b[a-z]:[\\/][^\s]+|\\\\[^\s\\/]+[\\/][^\s]+|file://\S+)", RegexOptions.Compiled);

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoLTranslator", "real-screenshot-shadow.v0.1.jsonl");

    public static string Append(ChampionOfficialRc2RealScreenshotShadowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string? directory = Path.GetDirectoryName(DefaultPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("无法确定本地 Shadow 日志目录。");
        Directory.CreateDirectory(directory);
        var record = new
        {
            record_type = result.SessionType,
            recorded_at = DateTimeOffset.UtcNow,
            record_origin = "real_screenshot_shadow_local_only",
            formal_eligibility = "not_eligible",
            intake_status = "local_shadow_only_not_for_data_import",
            redaction_status = "runtime_chat_prefix_masked_not_export_ready",
            screenshot_in_log = false,
            screenshot_written_to_dataset = false,
            rc_sha256 = result.RcSha256,
            target_locale = result.TargetLocale,
            model_call_count = result.ModelCallCount,
            fail_closed_count = result.FailClosedCount,
            lines = result.Lines.Select(line => new
            {
                line_number = line.LineNumber,
                ocr_text = Redact(line.MessageBody),
                chat_envelope_detected = line.ChatPrefix is not null,
                ocr_locales = line.OcrLocales,
                fusion_status = line.Fusion.Status,
                fusion_confidence = line.Fusion.Confidence,
                fusion_reasons = line.Fusion.Reasons,
                status = line.Status,
                source_locale = line.SourceLocale,
                canonical_ids = line.CanonicalIds,
                locked_renderings = line.LockedRenderings,
                typed_normalizations = line.TypedNormalizations.Select(normalization => new
                {
                    normalization_id = normalization.NormalizationId,
                    target_entity = normalization.TargetEntity,
                    target_slot = normalization.TargetSlot
                }),
                verified_typed_entities = (line.VerifiedTypedEntities ?? Array.Empty<SlangGraphEntityResolution>()).Select(entity => new
                {
                    entity_id = entity.EntityId,
                    entity_type = entity.EntityType,
                    extraction_kind = entity.ExtractionKind
                }),
                protected_input = Redact(line.ProtectedInput),
                raw_provider_output = Redact(line.RawProviderOutput),
                final_output = Redact(line.FinalOutput),
                elapsed_milliseconds = line.ElapsedMilliseconds,
                failure_reason = line.FailureReason
            })
        };
        File.AppendAllText(DefaultPath, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        return DefaultPath;
    }

    private static string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string redacted = ChatPrefix.Replace(value.Trim(), string.Empty);
        redacted = AbsolutePath.Replace(redacted, "[local-path]");
        redacted = redacted.Replace(Environment.UserName, "[local-user]", StringComparison.OrdinalIgnoreCase)
            .Replace(Environment.MachineName, "[local-machine]", StringComparison.OrdinalIgnoreCase);
        return redacted;
    }
}
