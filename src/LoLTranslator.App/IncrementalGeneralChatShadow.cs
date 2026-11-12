using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>
/// Explicit, session-local general-chat Shadow. It is separate from the
/// champion-only RC.2 mode: reliable new bodies may call Qwen, while verified
/// formal semantics/entities and RC.2 official names remain protected spans.
/// </summary>
public sealed class IncrementalGeneralChatShadowService
{
    private const int DefaultCacheCapacity = 256;
    private static readonly Regex PartialEnvelope = new(
        @"^\s*(?:(?:\d{1,2}\s*[:：]|[:：]\s*\d{1,2})\s*\[|\[[^\]\r\n]{0,96}\([^\)\r\n]{1,48}\)\s*[:：])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemNotification = new(
        @"(?:已选择|已選擇)\s*(?:路线|路線)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemTargetIdentity = new(
        @"(?:has\s*(?:targeted|shut\s+down|slain)|shut\s+down|slain\s+by|님이)\s+(?<identity>(?<player>[\p{L}\p{N}_.'’\-]+(?:\s+[\p{L}\p{N}_.'’\-]+){0,4})\s*[-–—]?\s*[\(（](?<champion>[^\)）\r\n]{1,32})[\)）])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SystemTargetedPing = new(
        @"^\s*has\s*targeted\s+(?<identity>[\p{L}\p{N}_.'’\-]+(?:\s+[\p{L}\p{N}_.'’\-]+){0,4}\s*[\(（][^\)）\r\n]{1,32}[\)）])\s*[!！.]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SystemActorEnvelope = new(
        @"^\s*(?:(?<timestamp>[0-9OoIl]{1,2}\s*[:：.]\s*[0-9OoIl]{2})\s*)?(?<identity>[\p{L}\p{N}_.'’\- ]{1,72}\s*[\(（][^\)）\r\n]{1,32}[\)）])\s*(?<body>(?:has\s*targeted|signals?\b|is\s+(?:on\s+the\s+way|asking\s+for)|has\s+(?:disconnected|reconnected|slain|shut\s+down|drawn\s+first\s+blood)|purchased\b|used\b|destroyed\b|님이\b)[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ParenthesizedPingAction = new(
        @"^\s*(?<identity>[\p{L}\p{N}_.'’\- ]{1,72}(?:(?:\s*[\(（][^\)）\r\n]{1,32}[\)）])|(?:\s*[|｜]\s*[\p{L}\p{N}_.'’\- ]{1,32}))?)\s*[\(（]\s*(?<action>cautious|caution|be\s+cautious|on\s+the\s+way|on\s+my\s+way|omw|assistance|need\s+assistance|asking\s+for\s+assistance|enemy\s+missing|enemies\s+missing)\s*[\)）]\s*[.!！。]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly OcrShadowSemanticService _semanticService;
    private readonly DomainLexiconIndex _rcIndex;
    private readonly ILocalTranslationAttemptProvider _provider;
    private readonly ProtectedHybridTranslationAggregator _aggregator;
    private readonly RuntimeTypedNormalizationCandidate _normalization;
    private readonly ReviewedRuntimeTermResolver _reviewedRuntimeTerms;
    private readonly OfficialItemPingResolver _itemPingResolver;
    private readonly OfficialGameEntityOcrRepair _officialEntityOcrRepair;
    private readonly OfficialTrilingualRuntimeTerms _officialTrilingualTerms;
    private readonly RuntimePresentationProjector _presentation;
    private readonly int _cacheCapacity;
    private readonly HashSet<string> _seenFingerprints = new(StringComparer.Ordinal);
    private readonly Queue<string> _fingerprintOrder = new();
    private readonly Dictionary<string, DateTimeOffset> _retryNotBefore = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;

    private IncrementalGeneralChatShadowService(
        OcrShadowSemanticService semanticService,
        DomainLexiconIndex rcIndex,
        ILocalTranslationAttemptProvider provider,
        RuntimeTypedNormalizationCandidate normalization,
        ReviewedRuntimeTermResolver reviewedRuntimeTerms,
        OfficialItemPingResolver itemPingResolver,
        OfficialGameEntityOcrRepair officialEntityOcrRepair,
        OfficialTrilingualRuntimeTerms officialTrilingualTerms,
        int cacheCapacity,
        Func<DateTimeOffset>? utcNow = null)
    {
        _semanticService = semanticService;
        _rcIndex = rcIndex;
        _provider = provider;
        _aggregator = new ProtectedHybridTranslationAggregator(provider);
        _normalization = normalization;
        _reviewedRuntimeTerms = reviewedRuntimeTerms;
        _itemPingResolver = itemPingResolver;
        _officialEntityOcrRepair = officialEntityOcrRepair;
        _officialTrilingualTerms = officialTrilingualTerms;
        _presentation = new(officialTrilingualTerms, officialEntityOcrRepair);
        _cacheCapacity = cacheCapacity;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static IncrementalGeneralChatShadowService CreateExplicit(
        OcrShadowSemanticService semanticService,
        string? rcPath = null,
        ILocalTranslationAttemptProvider? provider = null,
        string? normalizationArtifactPath = null,
        string? normalizationReviewPath = null,
        string? reviewedRuntimeTermsPath = null,
        string? itemLexiconPath = null,
        string? itemRuntimeAliasesPath = null,
        string? officialTrilingualTermsPath = null,
        string? officialTrilingualAmbiguityPath = null,
        int cacheCapacity = DefaultCacheCapacity,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(semanticService);
        if (cacheCapacity is < 16 or > 4096) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));

        string resolvedRc = rcPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "data", "packs", "lol", "domain-lexicon.v0.1.champion-official.rc.2.jsonl");
        string artifact = normalizationArtifactPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "collection", "lol", "real-screenshot-shadow-001", "runtime-normalization-candidate.jsonl");
        string review = normalizationReviewPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "collection", "lol", "real-screenshot-shadow-001", "run005-review-normalization.jsonl");
        string runtimeTerms = reviewedRuntimeTermsPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "runtime", "lol-reviewed-runtime-terms.v0.1.jsonl");
        string itemLexicon = itemLexiconPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "packs", "lol", "domain-lexicon.v0.1.candidate.2.jsonl");
        string itemAliases = itemRuntimeAliasesPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "runtime", "lol-client-item-display-aliases.v0.1.jsonl");
        string officialTerms = officialTrilingualTermsPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "runtime", "official-trilingual-runtime-terms.v0.1.jsonl");
        string officialAmbiguities = officialTrilingualAmbiguityPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "runtime", "official-trilingual-runtime-term-ambiguities.v0.1.jsonl");

        ChampionOfficialRc2RealScreenshotShadowService.ValidateRc(resolvedRc);
        RuntimeTypedNormalizationCandidate normalization = RuntimeTypedNormalizationCandidate.LoadExplicit(artifact, review);
        ILocalTranslationAttemptProvider resolvedProvider = provider ??
            ControlledShadowQwen3RuntimeFactory.Create(ControlledShadowQwen3Profile.ExplicitTestOnly()).GeneralTranslationProvider
                as ILocalTranslationAttemptProvider
            ?? throw new InvalidOperationException("Qwen3 controlled-shadow provider does not support diagnostics.");

        return new IncrementalGeneralChatShadowService(
            semanticService,
            new DomainLexiconIndex(DomainLexiconLoader.LoadJsonl(resolvedRc)),
            resolvedProvider,
            normalization,
            ReviewedRuntimeTermResolver.Load(runtimeTerms),
            OfficialItemPingResolver.Load(itemLexicon, itemAliases),
            OfficialGameEntityOcrRepair.Load(resolvedRc, itemLexicon),
            OfficialTrilingualRuntimeTerms.LoadOfficialSourceApproved(officialTerms, officialAmbiguities),
            cacheCapacity,
            utcNow);
    }

    public void ResetSession()
    {
        _seenFingerprints.Clear();
        _fingerprintOrder.Clear();
        _retryNotBefore.Clear();
    }

    /// <summary>
    /// Count only. The UI may display this session state, but never the bodies
    /// or the player information from which the fingerprints were derived.
    /// </summary>
    public int CachedBodyCount => _seenFingerprints.Count;

    public bool HasProcessedBodies => CachedBodyCount > 0;

    /// <summary>Continuous runtime entry. Event identity and classification may not be reconstructed downstream.</summary>
    public IncrementalGeneralChatShadowResult ProcessEvents(IReadOnlyList<VisibleChatMessage> events,
        ShadowMatchContextOption context, string targetLocale)
    {
        if (events.Any(item => item.Line.RuntimeEvent is null || item.OccurrenceKey != item.Line.RuntimeEvent.Id))
            throw new ArgumentException("Continuous runtime requires an admitted event contract.", nameof(events));
        OcrShadowLineResult[] rows = events.Select((item, index) => item.Line with { LineNumber = index + 1 }).ToArray();
        var byNumber = rows.ToDictionary(row => row.LineNumber, row => row.RuntimeEvent!);
        var keys = byNumber.ToDictionary(pair => pair.Key, pair => pair.Value.Id);
        var prepared = new List<OcrShadowLineResult>();
        var resolvedBeforeModel = new List<IncrementalGeneralChatShadowLine>();
        var plans = new Dictionary<int, PreparedRuntimePresentation>();
        foreach (OcrShadowLineResult row in rows)
        {
            RuntimePresentationPreparation preparation = _presentation.Prepare(row, targetLocale);
            if (preparation.Plan is not { } plan)
            {
                resolvedBeforeModel.Add(NoOutput(row, row.RuntimeEvent!.Envelope, "presentation_review", "unsafe_before_translation",
                    preparation.FailureReason ?? "presentation_preflight_failed", row.RuntimeEvent.Id) with
                    { PresentationRejection = new(preparation.FailureReason ?? "presentation_preflight_failed", true) });
                continue;
            }
            plans.Add(row.LineNumber, plan);
            if (row.RuntimeEvent!.Kind == RuntimeChatEventKind.PlayerText &&
                ChatEmoticonPolicy.IsRecognized(plan.TranslationEnvelope.Body))
            {
                resolvedBeforeModel.Add(NoOutput(row, plan.TranslationEnvelope, "player_emoticon_passthrough",
                    "new_body", "", row.RuntimeEvent.Id) with
                    { FinalOutput = plan.TranslationEnvelope.Body.Trim(), FailureReason = null });
                continue;
            }
            // Resolve bounded player-chat intents before batch selection. This
            // keeps them out of model requests while retaining event IDs and the
            // same sole safe-presentation path as every other result.
            if (row.RuntimeEvent!.Kind == RuntimeChatEventKind.PlayerText && IsLolInGameChatContext(context) &&
                ConversationIntentResolver.Current.TryResolve(plan.TranslationEnvelope.Body, targetLocale, out var intent))
            {
                resolvedBeforeModel.Add(NoOutput(row, plan.TranslationEnvelope, "conversation_intent_runtime", "new_body", "", row.RuntimeEvent.Id) with
                { FinalOutput = intent!.Text, FailureReason = null });
                continue;
            }
            prepared.Add(row with { PreparedPresentation = plan });
        }
        IncrementalGeneralChatShadowResult result = Process(prepared, context, targetLocale, keys);
        var projected = result.Lines.Concat(resolvedBeforeModel).OrderBy(line => line.LineNumber).Select(line =>
        {
            SafeRuntimeDisplayMessage? display = null;
            string? failure = line.FailureReason;
            RuntimePresentationRejection? rejection = line.PresentationRejection;
            if (line.FinalOutput is not null && plans.TryGetValue(line.LineNumber, out var plan))
            {
                display = _presentation.Compose(plan, line.FinalOutput, targetLocale, out failure);
                if (display is null) rejection = new(failure ?? "presentation_projection_failed", false);
            }
            return line with
            {
                RuntimeEvent = byNumber[line.LineNumber], SessionFingerprint = byNumber[line.LineNumber].Id,
                SafeDisplay = display, FinalOutput = display is null ? null : line.FinalOutput,
                FailureReason = failure, PresentationRejection = rejection,
                DedupStatus = display is null ? "fail_closed" : "displayed_new"
            };
        }).ToArray();
        return result with
        {
            Lines = projected, NoOutputCount = projected.Count(line => line.FinalOutput is null),
            DisplayedNewCount = projected.Count(line => line.SafeDisplay is not null)
        };
    }

    public IncrementalGeneralChatShadowResult Process(
        IReadOnlyList<OcrShadowLineResult> fusedLines,
        ShadowMatchContextOption context,
        string targetLocale,
        IReadOnlyDictionary<int, string>? occurrenceKeys = null)
    {
        ArgumentNullException.ThrowIfNull(fusedLines);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);

        if (_provider is IBatchLocalTranslationAttemptProvider batchProvider && fusedLines.Count > 1 &&
            TryBuildPlainBatch(fusedLines, context, targetLocale, occurrenceKeys, out List<(OcrShadowLineResult Source, ChatMessageEnvelope Envelope, string Body, string Fingerprint, string Locale)> batchItems))
        {
            BatchTranslationAttempt batch = batchProvider.TranslateBatchWithDiagnostics(batchItems.Select((item, index) =>
                new BatchTranslationItem($"m{index + 1}", item.Body, item.Locale, targetLocale)).ToArray());
            var batchLines = new List<IncrementalGeneralChatShadowLine>();
            for (int index = 0; index < batchItems.Count; index++)
            {
                var item = batchItems[index];
                BatchTranslationItemResult output = batch.Items[index];
                string? final = output.FinalOutput;
                string? failure = output.Error;
                if (final is not null && (ChatMessageEnvelopeExtractor.Extract(final).HasHighConfidencePrefix || OutputGroundingValidator.HasUngroundedDesktopUiTerm(item.Body, final)))
                { final = null; failure = "batch_output_grounding_or_envelope_rejected"; }
                if (final is not null && TranslationOutputQuality.Failure(item.Body, final, item.Locale, targetLocale) is { } qualityFailure)
                { final = null; failure = qualityFailure; }
                if (final is not null && item.Source.RuntimeEvent is null) MarkSeen(item.Fingerprint);
                batchLines.Add(new IncrementalGeneralChatShadowLine(item.Source.LineNumber, item.Envelope.Prefix, item.Body, item.Source.Fusion,
                    SourceLocales(item.Source), item.Fingerprint, "general_translation", final is null ? "fail_closed" : "displayed_new",
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), item.Body,
                    output.RawOutput, final, batch.ElapsedMilliseconds, failure, ResolveSenderRelation(item.Source, item.Envelope)));
            }
            return new IncrementalGeneralChatShadowResult("incremental_general_chat_shadow.v0.1", ChampionOfficialRc2RealScreenshotShadowService.ExpectedRcSha256,
                OcrShadowSemanticService.ExpectedPackSha256, OcrShadowSemanticService.ExpectedRenderingPackSha256, ControlledShadowQwen3Profile.ExpectedDigest,
                targetLocale, batchLines, 1, 0, batchLines.Count(line => line.FinalOutput is null), batchLines.Count(line => line.FinalOutput is not null));
        }

        var lines = new List<IncrementalGeneralChatShadowLine>();
        foreach (OcrShadowLineResult source in fusedLines.OrderBy(line => line.LineNumber))
        {
            ChatMessageEnvelope envelope = RuntimeChatOcrTextRepair.Repair(
                _officialEntityOcrRepair.Repair(ExtractRuntimeEnvelope(source)));
            string body = envelope.Body;
            if (source.Fusion.IsAmbiguous)
            {
                lines.Add(NoOutput(source, envelope, "review", "fusion_ambiguous", "fusion_ambiguous_no_provider_call"));
                continue;
            }

            bool structuredEnvelopeAnchor = source.Fusion.Reasons.Contains("structured_chat_envelope_anchor", StringComparer.Ordinal);
            bool selectedChatRegionRow = source.Fusion.Reasons.Contains("selected_chat_region_coordinate_row", StringComparer.Ordinal);
            if (!string.Equals(source.Fusion.Status, "fused", StringComparison.OrdinalIgnoreCase) &&
                !structuredEnvelopeAnchor && !selectedChatRegionRow)
            {
                lines.Add(NoOutput(source, envelope, "review", "fusion_not_reliable", "fusion_not_reliable_no_provider_call"));
                continue;
            }

            if (!IsLolInGameChatContext(context))
            {
                lines.Add(NoOutput(source, envelope, "review", "context_incompatible", "shadow_context_incompatible_no_provider_call"));
                continue;
            }

            if (!envelope.HasHighConfidencePrefix && PartialEnvelope.IsMatch(source.Text))
            {
                lines.Add(NoOutput(source, envelope, "review", "unreliable_chat_envelope", "unreliable_chat_envelope_requires_review"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                lines.Add(NoOutput(source, envelope, "review", "empty_body", "empty_message_body_no_provider_call"));
                continue;
            }

            if (!envelope.HasHighConfidencePrefix && SystemNotification.IsMatch(body))
            {
                lines.Add(NoOutput(source, envelope, "review", "system_notification", "system_notification_no_provider_call"));
                continue;
            }

            // The runtime event ledger supplies an occurrence identity. A
            // body alone is not a message identity: players may legitimately send
            // the same text twice in one visible chat sequence.
            string fingerprint = occurrenceKeys is not null && occurrenceKeys.TryGetValue(source.LineNumber, out string? occurrenceKey)
                ? occurrenceKey
                : Fingerprint(body);
            if (source.RuntimeEvent is null && _seenFingerprints.Contains(fingerprint))
            {
                lines.Add(new IncrementalGeneralChatShadowLine(
                    source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                    "deduplicated", "deduplicated_success", Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<string>(), null, null, null, 0, null));
                continue;
            }

            if (source.RuntimeEvent is null && _retryNotBefore.TryGetValue(fingerprint, out DateTimeOffset retryAt) && _utcNow() < retryAt)
            {
                lines.Add(NoOutput(source, envelope, "review", "retry_cooldown", "retry_cooldown", fingerprint));
                continue;
            }

            IncrementalGeneralChatShadowLine processed = ProcessNewBody(source, envelope, body, fingerprint, context, targetLocale);
            if (!string.IsNullOrWhiteSpace(processed.FinalOutput))
            {
                if (source.RuntimeEvent is null) MarkSeen(fingerprint);
                _retryNotBefore.Remove(fingerprint);
                lines.Add(processed with { DedupStatus = "displayed_new" });
            }
            else
            {
                if (source.RuntimeEvent is null && RequiresRetryCooldown(processed))
                {
                    _retryNotBefore[fingerprint] = _utcNow().AddSeconds(10);
                }
                lines.Add(processed with { DedupStatus = "fail_closed" });
            }
        }

        return new IncrementalGeneralChatShadowResult(
            "incremental_general_chat_shadow.v0.1",
            ChampionOfficialRc2RealScreenshotShadowService.ExpectedRcSha256,
            OcrShadowSemanticService.ExpectedPackSha256,
            OcrShadowSemanticService.ExpectedRenderingPackSha256,
            ControlledShadowQwen3Profile.ExpectedDigest,
            targetLocale,
            lines,
            lines.Count(line => line.ModelCalled),
            lines.Count(line => line.Route == "deduplicated"),
            lines.Count(line => line.FinalOutput is null && line.Route is not "deduplicated"),
            lines.Count(line => !string.IsNullOrWhiteSpace(line.FinalOutput)));
    }

    private IncrementalGeneralChatShadowLine ProcessNewBody(
        OcrShadowLineResult source,
        ChatMessageEnvelope envelope,
        string body,
        string fingerprint,
        ShadowMatchContextOption context,
        string targetLocale)
    {
        IReadOnlyList<string> locales = ResolveCandidateLocales(source);
        if (source.RuntimeEvent is { Kind: RuntimeChatEventKind.Unknown })
            return NoOutput(source, envelope, "structured_event_review", "unclassified_event",
                "unclassified_event_no_provider_call", fingerprint);
        string? senderRelation = ResolveSenderRelation(source, envelope);
        if (locales.Count == 0)
        {
            return NoOutput(source, envelope, "review", "unsupported_ocr_locale", "unsupported_ocr_locale_no_provider_call", fingerprint);
        }
        envelope = envelope with
        {
            Prefix = _officialTrilingualTerms.RenderChampionSlotsExact(envelope.Prefix, locales, targetLocale)
        };
        string primaryLocale = ResolvePrimaryLocale(body, locales);

        IReadOnlyList<OfficialTrilingualRuntimeTermMatch> officialMatches =
            _officialTrilingualTerms.MatchExact(body, locales, targetLocale);
        bool typedPing = source.RuntimeEvent is { } pingContract ? pingContract.Kind == RuntimeChatEventKind.Ping :
            source.Fusion.Reasons.Contains("runtime_event_ping", StringComparer.Ordinal);
        bool typedSystem = source.RuntimeEvent is { } systemContract ? systemContract.Kind == RuntimeChatEventKind.System :
            source.Fusion.Reasons.Contains("runtime_event_system", StringComparer.Ordinal);
        string knownStructuredRendering = string.Empty;
        bool knownStructuredTemplate = (typedPing || typedSystem) &&
            RuntimeStructuredEventRenderer.TryRenderPing(body, targetLocale, out knownStructuredRendering);
        // A complete typed slot (for example "Infernal Drake" in a stolen-
        // objective event) supplies the exact context demanded by the blocked
        // short-form policy. Bare "drake" and ordinary prose still fail closed.
        if (_officialTrilingualTerms.ContainsBlockedAmbiguousSurface(body) && !knownStructuredTemplate)
        {
            return NoOutput(source, envelope, "review", "official_term_ambiguity",
                "blocked_official_term_ambiguity_no_model_guess", fingerprint);
        }

        if (_itemPingResolver.TryResolve(body, locales, targetLocale, out OfficialItemPingResolution? itemPing))
        {
            return new IncrementalGeneralChatShadowLine(
                source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                "official_item_ping_runtime", "new_body", new[] { itemPing!.CanonicalId }, Array.Empty<string>(),
                Array.Empty<string>(), new[] { itemPing.TargetItemName }, body, null, itemPing.Rendering, 0, null, senderRelation ?? "ally");
        }

        if (typedPing && knownStructuredTemplate)
        {
            string pingRendering = knownStructuredRendering;
            pingRendering = RenderStructuredTerms(pingRendering, locales, targetLocale);
            return new IncrementalGeneralChatShadowLine(
                source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                "runtime_ping_template", "new_body", officialMatches.Select(match => match.CanonicalId).ToArray(),
                Array.Empty<string>(), Array.Empty<string>(), officialMatches.Select(match => match.TargetText).ToArray(),
                body, null, pingRendering, 0, null, senderRelation);
        }
        if (typedSystem && knownStructuredTemplate)
        {
            string systemRendering = knownStructuredRendering;
            systemRendering = RenderStructuredTerms(systemRendering, locales, targetLocale);
            return new IncrementalGeneralChatShadowLine(
                source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                "runtime_system_template", "new_body", officialMatches.Select(match => match.CanonicalId).ToArray(),
                Array.Empty<string>(), Array.Empty<string>(), officialMatches.Select(match => match.TargetText).ToArray(),
                body, null, systemRendering, 0, null, senderRelation);
        }

        if (officialMatches.Count > 0 && IsFullyCoveredByOfficialTerms(body, officialMatches))
        {
            string renderedBody = _officialTrilingualTerms.RenderExactMatches(body, officialMatches);
            return new IncrementalGeneralChatShadowLine(
                source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                "official_term_runtime", "new_body",
                officialMatches.Select(match => match.CanonicalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Array.Empty<string>(), Array.Empty<string>(), officialMatches.Select(match => match.TargetText).ToArray(),
                body, null, renderedBody, 0, null, senderRelation);
        }

        // The overlay still mirrors a reliable same-language line, but avoids a
        // costly model invocation. This is especially important for localized
        // League system notifications containing Latin player names.
        if (SameLanguage(primaryLocale, targetLocale))
        {
            string renderedBody = _officialTrilingualTerms.RenderExactMatches(body, officialMatches);
            return new IncrementalGeneralChatShadowLine(
                source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                officialMatches.Count == 0 ? "source_locale_passthrough" : "source_locale_official_terms",
                "new_body", officialMatches.Select(match => match.CanonicalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Array.Empty<string>(), Array.Empty<string>(), officialMatches.Select(match => match.TargetText).ToArray(),
                body, null, renderedBody, 0, null, senderRelation);
        }

        (string SourceLocale, DomainLexiconMatchResult Match) selectedRc = locales
            .Select(locale => (SourceLocale: locale, Match: _rcIndex.Match(
                body,
                locale,
                LexiconContext(),
                new DomainLexiconMatchOptions(IncludeCandidateShadow: true))))
            .OrderByDescending(item => item.Match.CandidateShadowMatches.Count)
            .ThenByDescending(item => ScriptAffinity(body, item.SourceLocale))
            .First();
        DomainLexiconEntityMatch[] rcMatches = selectedRc.Match.CandidateShadowMatches.ToArray();
        Match[] systemTargetMatches = IsStructuredEvent(source)
            ? SystemTargetIdentity.Matches(body).Cast<Match>().ToArray()
            : Array.Empty<Match>();
        string[] opposingIdentityRenderings = IsStructuredEvent(source)
            ? systemTargetMatches.Select(match => RenderIdentity(match.Groups["identity"], officialMatches))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
        if (IsStructuredEvent(source) &&
            TryRenderTargetedPing(body, targetLocale, officialMatches, out string? targetedPingRendering, out string? targetIdentity))
        {
            return new IncrementalGeneralChatShadowLine(
                source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                "system_target_ping_template", "new_body", Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), new[] { targetIdentity! }, body, null, targetedPingRendering, 0, null,
                senderRelation, new[] { targetIdentity! });
        }
        if (_reviewedRuntimeTerms.TryResolveExact(body, selectedRc.SourceLocale, targetLocale, out ReviewedRuntimeTermResolution? reviewedTerm))
        {
            return new IncrementalGeneralChatShadowLine(
                source.LineNumber, envelope.Prefix, body, source.Fusion, SourceLocales(source), fingerprint,
                "reviewed_runtime_term", "new_body", new[] { reviewedTerm!.TermId }, Array.Empty<string>(),
                Array.Empty<string>(), new[] { reviewedTerm.Rendering }, body, null, reviewedTerm.Rendering, 0, null, senderRelation);
        }
        // Ping and system events are structured game UI, not free-form chat.
        // Unknown templates fail closed instead of asking a language model to
        // invent an interpretation (the source of unrelated white-text rows).
        if (typedPing || typedSystem)
        {
            string typedFailure = typedPing
                ? "unknown_ping_template_no_provider_call"
                : "unknown_system_template_no_provider_call";
            return NoOutput(source, envelope, "structured_event_review", "typed_event_fail_closed", typedFailure,
                fingerprint, officialMatches.Select(match => match.CanonicalId).ToArray());
        }
        if (ChampionOfficialRc2RealScreenshotShadowService.IsOrdinaryEnglishCommonWordUse(body, rcMatches))
        {
            return NoOutput(source, envelope, "review", "common_word_context_requires_review", "ordinary_english_common_word_context_no_provider_call", fingerprint,
                rcMatches.Select(match => match.CanonicalId).ToArray());
        }

        RuntimeTypedNormalizationResult candidateNormalization = _normalization.NormalizeExact(body, context);
        if (candidateNormalization.Applied && rcMatches.Length == 0)
        {
            candidateNormalization = RuntimeTypedNormalizationResult.NoMatch(body, "normalization_requires_official_entity_anchor");
        }
        if (string.Equals(candidateNormalization.FailureReason, "normalization_approximate_surface_requires_review", StringComparison.Ordinal))
        {
            return NoOutput(source, envelope, "review", "normalization_requires_review", candidateNormalization.FailureReason ?? "normalization_requires_review", fingerprint,
                rcMatches.Select(match => match.CanonicalId).ToArray());
        }

        IReadOnlyList<SlangGraphSemanticMatch> semanticMatches = _semanticService.Match(
                body,
                OcrShadowSemanticService.AutoSemanticLocale,
                context)
            .SingleOrDefault()?.Matches ?? Array.Empty<SlangGraphSemanticMatch>();
        SlangGraphRenderedConcept[] renderings = semanticMatches
            .Select(match => _semanticService.Render(match, targetLocale))
            .ToArray();
        IReadOnlyList<HybridLockedSpan> semanticLocks = HybridLockedSpanPlanner.Plan(new HybridAggregationRequest(
            body,
            selectedRc.SourceLocale,
            targetLocale,
            semanticMatches,
            renderings,
            new GeneralTranslationResult("unavailable", null, null, "planned_before_provider")));

        var locks = new List<HybridLockedSpan>(semanticLocks);
        var additionalLocks = new List<HybridLockedSpan>();
        if (systemTargetMatches.Length > 0)
        {
            // Lock the complete target identity first and render only its
            // official champion. Adding the inner champion lock first would
            // leave the opaque player ID outside the protected span.
            AddNonOverlapping(locks, systemTargetMatches.Select((match, index) =>
            {
                Group identity = match.Groups["identity"];
                return new HybridLockedSpan(
                    identity.Index,
                    identity.Length,
                    $"[[OFFICIAL_CHAMPION_IDENTITY_LOCKED_{index + 1}]]",
                    RenderIdentity(identity, officialMatches));
            }), additionalLocks);
        }
        AddNonOverlapping(locks, officialMatches.Select((match, index) => new HybridLockedSpan(
            match.Start,
            match.Length,
            $"[[OFFICIAL_TERM_LOCKED_{index + 1}]]",
            match.TargetText)), additionalLocks);
        AddNonOverlapping(locks, rcMatches.Select((match, index) =>
        {
            DomainLexiconRenderedEntry rendering = _rcIndex.RenderShadow(match.CanonicalId, targetLocale);
            return new HybridLockedSpan(match.Start, match.Length, $"[[RC_LOCKED_{index + 1}]]", rendering.Text);
        }), additionalLocks);
        if (candidateNormalization.Entity is { } normalization)
        {
            AddNonOverlapping(locks, new[]
            {
                new HybridLockedSpan(
                    normalization.Start,
                    normalization.Length,
                    "[[SG_ENTITY_LOCKED_CANDIDATE_1]]",
                    _semanticService.RenderVerifiedEntity(normalization.TargetEntity, targetLocale).Text)
            }, additionalLocks);
        }
        IReadOnlyList<SlangGraphEntityResolution> verifiedEntities = locales
            .SelectMany(locale => _semanticService.ResolveVerifiedEntities(body, locale, context))
            .GroupBy(entity => entity.EntityId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entity => entity.MatchedText.Length).First())
            .ToArray();
        AddNonOverlapping(locks, verifiedEntities.Select((entity, index) => new HybridLockedSpan(
            entity.Start,
            entity.Length,
            $"[[SG_ENTITY_LOCKED_{index + 1}]]",
            _semanticService.RenderVerifiedEntity(entity.EntityId, targetLocale).Text)), additionalLocks);

        string route = locks.Count == 0 ? "general_translation" : "hybrid";
        LocalTranslationAttempt? attempt;
        string? output;
        string? failure;
        if (locks.Count == 0)
        {
            attempt = _provider.TranslateWithDiagnostics(new GeneralTranslationRequest(body, selectedRc.SourceLocale, targetLocale));
            output = attempt.FinalOutput;
            failure = attempt.Error;
        }
        else
        {
            HybridAggregationResult aggregation = _aggregator.Aggregate(new HybridAggregationRequest(
                body,
                selectedRc.SourceLocale,
                targetLocale,
                semanticMatches,
                renderings,
                new GeneralTranslationResult("unavailable", null, null, "protected_hybrid"),
                additionalLocks));
            attempt = _aggregator.LastAttempt;
            output = aggregation.Text;
            failure = aggregation.FailureReason ?? attempt?.Error;
        }

        if (string.Equals(failure, "ollama_local_provider_error:HttpRequestException", StringComparison.Ordinal))
        {
            failure = "local_provider_unavailable";
        }
        if (output is not null && ChatMessageEnvelopeExtractor.Extract(output).HasHighConfidencePrefix)
        {
            output = null;
            failure = "model_added_chat_envelope";
        }
        if (output is not null && OutputGroundingValidator.HasUngroundedDesktopUiTerm(body, output))
        {
            output = null;
            failure = "output_ungrounded_desktop_ui_term";
        }
        if (output is not null && TranslationOutputQuality.Failure(body, output, selectedRc.SourceLocale, targetLocale) is { } qualityFailure)
        {
            output = null;
            failure = qualityFailure;
        }

        return new IncrementalGeneralChatShadowLine(
            source.LineNumber,
            envelope.Prefix,
            body,
            source.Fusion,
            SourceLocales(source),
            fingerprint,
            route,
            "new_body",
            officialMatches.Select(match => match.CanonicalId)
                .Concat(rcMatches.Select(match => match.CanonicalId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            semanticMatches.Select(match => match.ConceptId).ToArray(),
            verifiedEntities.Select(entity => entity.EntityId).ToArray(),
            locks.Select(lockSpan => lockSpan.VerifiedRendering).ToArray(),
            _aggregator.LastProtectedInput,
            attempt?.RawOutput,
            output,
            attempt?.ElapsedMilliseconds ?? 0,
            failure,
            senderRelation,
            opposingIdentityRenderings);
    }

    private static IncrementalGeneralChatShadowLine NoOutput(
        OcrShadowLineResult source,
        ChatMessageEnvelope envelope,
        string route,
        string dedupStatus,
        string failure,
        string? fingerprint = null,
        IReadOnlyList<string>? canonicalIds = null) =>
        new(
            source.LineNumber, envelope.Prefix, envelope.Body, source.Fusion, SourceLocales(source), fingerprint,
            route, dedupStatus, canonicalIds ?? Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), null, null, null, 0, failure);

    private void MarkSeen(string fingerprint)
    {
        _seenFingerprints.Add(fingerprint);
        _fingerprintOrder.Enqueue(fingerprint);
        while (_fingerprintOrder.Count > _cacheCapacity)
        {
            _seenFingerprints.Remove(_fingerprintOrder.Dequeue());
        }
    }

    private string RenderStructuredTerms(string rendering, IReadOnlyList<string> sourceLocales, string targetLocale)
    {
        IReadOnlyList<OfficialTrilingualRuntimeTermMatch> matches =
            _officialTrilingualTerms.MatchExact(rendering, sourceLocales.Concat(new[] { targetLocale }).Distinct().ToArray(), targetLocale);
        return _officialTrilingualTerms.RenderExactMatches(rendering, matches);
    }

    private static bool RequiresRetryCooldown(IncrementalGeneralChatShadowLine line) =>
        line.Route is "general_translation" or "hybrid" &&
        !string.IsNullOrWhiteSpace(line.FailureReason);

    private static bool TryRenderTargetedPing(
        string body,
        string targetLocale,
        IReadOnlyList<OfficialTrilingualRuntimeTermMatch> officialMatches,
        out string? rendering,
        out string? identity)
    {
        rendering = null;
        identity = null;
        Match match = SystemTargetedPing.Match(body);
        if (!match.Success) return false;
        Group identityGroup = match.Groups["identity"];
        identity = identityGroup.Value.Trim();
        if (identity.Length == 0) return false;
        OfficialTrilingualRuntimeTermMatch? champion = officialMatches.FirstOrDefault(term =>
            string.Equals(term.TermType, "champion", StringComparison.Ordinal) &&
            term.Start >= identityGroup.Index && term.Start + term.Length <= identityGroup.Index + identityGroup.Length);
        if (champion is not null)
        {
            int relativeStart = champion.Start - identityGroup.Index;
            identity = identity.Remove(relativeStart, champion.Length).Insert(relativeStart, champion.TargetText);
        }
        identity = ChampionOnlyIdentity(identity);
        if (targetLocale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            rendering = $"已将目标锁定为 {identity}";
        else if (targetLocale.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            rendering = $"대상을 {identity}(으)로 지정했습니다";
        else
            rendering = $"Targeted {identity}";
        return true;
    }

    private bool TryBuildPlainBatch(IReadOnlyList<OcrShadowLineResult> sources, ShadowMatchContextOption context, string targetLocale,
        IReadOnlyDictionary<int, string>? occurrenceKeys,
        out List<(OcrShadowLineResult Source, ChatMessageEnvelope Envelope, string Body, string Fingerprint, string Locale)> items)
    {
        items = new();
        if (!IsLolInGameChatContext(context)) return false;
        foreach (OcrShadowLineResult source in sources.OrderBy(line => line.LineNumber))
        {
            ChatMessageEnvelope envelope = RuntimeChatOcrTextRepair.Repair(
                _officialEntityOcrRepair.Repair(ExtractRuntimeEnvelope(source)));
            IReadOnlyList<string> locales = ResolveCandidateLocales(source);
            envelope = envelope with
            {
                Prefix = _officialTrilingualTerms.RenderChampionSlotsExact(envelope.Prefix, locales, targetLocale)
            };
            string body = envelope.Body;
            if (source.Fusion.IsAmbiguous || !string.Equals(source.Fusion.Status, "fused", StringComparison.OrdinalIgnoreCase) ||
                !envelope.HasHighConfidencePrefix || string.IsNullOrWhiteSpace(body) || locales.Count == 0) return false;
            if (IsStructuredEvent(source)) return false;
            // Only plaintext messages batch here. Any formal semantic/entity hit
            // stays on the existing protected single-item path.
            if (_semanticService.Match(body, OcrShadowSemanticService.AutoSemanticLocale, context).SelectMany(line => line.Matches).Any()) return false;
            DomainLexiconMatchResult lexicon = _rcIndex.Match(body, locales[0], LexiconContext(), new DomainLexiconMatchOptions(IncludeCandidateShadow: true));
            if (lexicon.CandidateShadowMatches.Count > 0 || _semanticService.ResolveVerifiedEntities(body, locales[0], context).Count > 0) return false;
            if (_reviewedRuntimeTerms.TryResolveExact(body, locales[0], targetLocale, out _)) return false;
            if (_itemPingResolver.TryResolve(body, locales, targetLocale, out _)) return false;
            if (_officialTrilingualTerms.MatchExact(body, locales, targetLocale).Count > 0) return false;
            if (_officialTrilingualTerms.ContainsBlockedAmbiguousSurface(body)) return false;
            string fingerprint = occurrenceKeys is not null && occurrenceKeys.TryGetValue(source.LineNumber, out string? key) ? key : Fingerprint(body);
            if (source.RuntimeEvent is null && _seenFingerprints.Contains(fingerprint)) return false;
            items.Add((source, envelope, body, fingerprint, locales[0]));
        }
        return items.Count > 0;
    }

    private static ChatMessageEnvelope ExtractRuntimeEnvelope(OcrShadowLineResult source)
    {
        if (source.PreparedPresentation is { } prepared) return prepared.TranslationEnvelope;
        if (source.RuntimeEvent is { } contract) return contract.Envelope;
        ChatMessageEnvelope envelope = ChatMessageEnvelopeExtractor.Extract(source.Text);
        if (source.Fusion.Reasons.Contains("lol_system_notification", StringComparer.Ordinal) &&
            TryExtractParenthesizedPingEnvelope(envelope, out ChatMessageEnvelope pingEnvelope))
            return pingEnvelope;
        if (envelope.HasHighConfidencePrefix ||
            !source.Fusion.Reasons.Contains("lol_system_notification", StringComparer.Ordinal))
            return envelope;
        Match system = SystemActorEnvelope.Match(source.Text);
        if (!system.Success) return envelope;
        Group body = system.Groups["body"];
        return new ChatMessageEnvelope(
            source.Text[..body.Index],
            body.Value.Trim(),
            true,
            system.Groups["timestamp"].Value,
            "[System]",
            system.Groups["identity"].Value.Trim());
    }

    private static bool TryExtractParenthesizedPingEnvelope(
        ChatMessageEnvelope envelope,
        out ChatMessageEnvelope pingEnvelope)
    {
        pingEnvelope = envelope;
        Match ping = ParenthesizedPingAction.Match(envelope.Body);
        if (!ping.Success) return false;
        string identity = ping.Groups["identity"].Value.Trim();
        string action = ping.Groups["action"].Value.Trim();
        string timestamp = envelope.Timestamp ?? string.Empty;
        string prefix = string.IsNullOrWhiteSpace(timestamp)
            ? $"[系统] {identity}："
            : $"{timestamp} [系统] {identity}：";
        pingEnvelope = new ChatMessageEnvelope(prefix, action, true, timestamp, "[System]", identity);
        return true;
    }

    private static bool IsFullyCoveredByOfficialTerms(
        string body,
        IReadOnlyList<OfficialTrilingualRuntimeTermMatch> matches)
    {
        for (int index = 0; index < body.Length; index++)
        {
            if (!char.IsLetterOrDigit(body[index])) continue;
            if (!matches.Any(match => index >= match.Start && index < match.Start + match.Length)) return false;
        }
        return true;
    }

    private static string RenderIdentity(
        Group identity,
        IReadOnlyList<OfficialTrilingualRuntimeTermMatch> officialMatches)
    {
        string rendered = identity.Value;
        foreach (OfficialTrilingualRuntimeTermMatch term in officialMatches
                     .Where(term => term.Start >= identity.Index &&
                                    term.Start + term.Length <= identity.Index + identity.Length)
                     .OrderByDescending(term => term.Start))
        {
            int relative = term.Start - identity.Index;
            rendered = rendered.Remove(relative, term.Length).Insert(relative, term.TargetText);
        }
        return ChampionOnlyIdentity(rendered);
    }

    private static string ChampionOnlyIdentity(string identity)
    {
        string value = identity.Trim();
        int open = value.LastIndexOfAny(['(', '（']);
        int close = value.LastIndexOfAny([')', '）']);
        return open >= 0 && close > open + 1
            ? value[(open + 1)..close].Trim()
            : value;
    }

    private static void AddNonOverlapping(
        ICollection<HybridLockedSpan> destination,
        IEnumerable<HybridLockedSpan> candidates,
        ICollection<HybridLockedSpan>? accepted = null)
    {
        foreach (HybridLockedSpan candidate in candidates.OrderBy(item => item.Start))
        {
            bool valid = candidate.Start >= 0 && candidate.Length > 0;
            bool overlaps = destination.Any(existing =>
                candidate.Start < existing.Start + existing.Length && existing.Start < candidate.Start + candidate.Length);
            if (valid && !overlaps)
            {
                destination.Add(candidate);
                accepted?.Add(candidate);
            }
        }
    }

    private static bool IsLolInGameChatContext(ShadowMatchContextOption context) =>
        string.Equals(context.Channel, "in_game_chat", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(context.Community) &&
        context.Community.StartsWith("lol.", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveSenderRelation(OcrShadowLineResult source, ChatMessageEnvelope envelope)
    {
        if (source.PreparedPresentation is { } prepared)
            return prepared.Actor?.Role switch
            {
                TranslationSidecarTextRole.AlliedIdentity => "ally",
                TranslationSidecarTextRole.EnemyIdentity => "enemy",
                _ => prepared.Actor is null ? "gold" : null
            };
        // An explicit team channel is authoritative. Pixel colour may be
        // contaminated by a red target/spell later in the OCR row, but it must
        // never turn a teammate into an enemy in the translation sidecar.
        string prefix = envelope.Prefix ?? string.Empty;
        if (prefix.Contains("[Team]", StringComparison.OrdinalIgnoreCase) ||
            prefix.Contains("[队伍]", StringComparison.OrdinalIgnoreCase) ||
            prefix.Contains("[隊伍]", StringComparison.OrdinalIgnoreCase) ||
            prefix.Contains("[WE]", StringComparison.OrdinalIgnoreCase))
            return "ally";
        // Otherwise direct source-pixel colour is the primary presentation
        // signal. The older relation reasons remain compatibility fallbacks.
        if (source.Fusion.Reasons.Contains("lol_source_identity_blue", StringComparer.Ordinal)) return "ally";
        if (source.Fusion.Reasons.Contains("lol_source_identity_red", StringComparer.Ordinal)) return "enemy";
        // A remembered actor relation is stronger than gold elsewhere in the
        // same row. Mixed Pings commonly contain a blue sender and a gold
        // action suffix; gold must color the action, not replace the sender.
        if (source.Fusion.Reasons.Contains("lol_allied_identity_row", StringComparer.Ordinal)) return "ally";
        if (source.Fusion.Reasons.Contains("lol_enemy_identity_row", StringComparer.Ordinal)) return "enemy";
        // A source row that was explicitly classified as a system notification
        // and contains dominant League system-gold pixels remains gold even
        // when OCR did not recover a usable actor/identity span.
        if (IsStructuredEvent(source) &&
            source.Fusion.Reasons.Contains("lol_system_gold_row", StringComparer.Ordinal)) return "gold";
        if (source.Fusion.Reasons.Contains("lol_source_identity_gold", StringComparer.Ordinal)) return "gold";
        if (source.Fusion.Reasons.Contains("lol_source_identity_white", StringComparer.Ordinal)) return "white";
        return null;
    }

    private static bool IsStructuredEvent(OcrShadowLineResult source) => source.RuntimeEvent is { } contract
        ? contract.Kind != RuntimeChatEventKind.PlayerText
        : source.Fusion.Reasons.Contains("lol_system_notification", StringComparer.Ordinal);

    private static DomainLexiconMatchContext LexiconContext() =>
        new(null, null, null, "16.15.1", new[] { "league_of_legends", "champion", "in_game_chat" });

    private static IReadOnlyList<string> SourceLocales(OcrShadowLineResult line) =>
        line.Sources.Select(source => source.RecognizerLanguageTag).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyList<string> ResolveCandidateLocales(OcrShadowLineResult line)
    {
        var locales = new List<string>();
        foreach (string locale in SourceLocales(line))
        {
            if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)) locales.Add("en-US");
            if (locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) locales.Add("zh-CN");
            if (locale.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) locales.Add("ko-KR");
        }
        int latin = line.Text.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        int han = line.Text.Count(character => character is >= '\u3400' and <= '\u9FFF');
        int hangul = line.Text.Count(character => character is >= '\u1100' and <= '\u11ff' or
            >= '\u3130' and <= '\u318f' or >= '\uac00' and <= '\ud7af');
        if (hangul > Math.Max(latin, han) && hangul > 0) locales.Insert(0, "ko-KR");
        if (han > Math.Max(latin, hangul) && han > 0) locales.Insert(0, "zh-CN");
        if (latin > 0) locales.Add("en-US");
        if (han > 0) locales.Add("zh-CN");
        if (hangul > 0) locales.Add("ko-KR");
        return locales.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolvePrimaryLocale(string text, IReadOnlyList<string> locales) =>
        locales.OrderByDescending(locale => ScriptAffinity(text, locale)).First();

    private static int ScriptAffinity(string text, string locale)
    {
        if (locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return text.Count(character => character is >= '\u3400' and <= '\u9FFF');
        if (locale.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return text.Count(character => character is >= '\u1100' and <= '\u11ff' or
                >= '\u3130' and <= '\u318f' or >= '\uac00' and <= '\ud7af');
        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return text.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return 0;
    }

    private static bool SameLanguage(string left, string right) =>
        string.Equals(left.Split('-', '_')[0], right.Split('-', '_')[0], StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(string body)
    {
        string normalized = Regex.Replace(body.Trim(), @"\s+", " ", RegexOptions.CultureInvariant).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static string FormatDisplay(IncrementalGeneralChatShadowResult result)
    {
        var display = new StringBuilder()
            .AppendLine("增量通用聊天 Shadow v0.1（显式测试，本地 Qwen3）")
            .AppendLine($"新增模型调用：{result.ModelCallCount}；会话去重：{result.DeduplicatedCount}；无输出：{result.NoOutputCount}");
        foreach (IncrementalGeneralChatShadowLine line in result.Lines)
        {
            display.Append($"第 {line.LineNumber} 行 · {line.Route}");
            if (!string.IsNullOrWhiteSpace(line.DedupStatus)) display.Append($" · {line.DedupStatus}");
            display.AppendLine();
            if (line.CanonicalIds.Count > 0 || line.SemanticConceptIds.Count > 0 || line.VerifiedTypedEntityIds.Count > 0)
            {
                display.AppendLine($"  保护：{string.Join(", ", line.CanonicalIds.Concat(line.SemanticConceptIds).Concat(line.VerifiedTypedEntityIds).Distinct(StringComparer.Ordinal))}");
            }
            if (!string.IsNullOrWhiteSpace(line.FinalOutput)) display.AppendLine($"  译文：{line.FinalOutput}");
            else display.AppendLine($"  无输出：{line.FailureReason ?? "未调用模型"}");
            if (line.ElapsedMilliseconds > 0) display.AppendLine($"  时延：{line.ElapsedMilliseconds} ms");
        }

        return display.ToString().TrimEnd();
    }
}

public static class OutputGroundingValidator
{
    // A broad UI/runtime vocabulary is only a last safety net after the normal
    // body-only and marker checks. It rejects a desktop/runtime proper noun
    // only when the source body did not contain the corresponding token.
    private static readonly string[] DesktopUiTerms =
    [ "windows", "powershell", "terminal", "command prompt", "ollama", "browser", "explorer", "桌面", "命令提示符", "终端", "奥拉玛" ];

    public static bool HasUngroundedDesktopUiTerm(string input, string output) =>
        DesktopUiTerms.Any(term => output.Contains(term, StringComparison.OrdinalIgnoreCase) && !input.Contains(term, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Small, UI-independent policy so that a region replacement cannot silently
/// discard incremental deduplication state. The UI supplies the user's choice;
/// it owns presentation of the confirmation dialog.
/// </summary>
public static class IncrementalGeneralChatSessionResetPolicy
{
    public static bool RequiresRegionReplacementConfirmation(int cachedBodyCount) => cachedBodyCount > 0;

    public static bool ShouldApplyRegionReplacement(int cachedBodyCount, bool userConfirmed) =>
        cachedBodyCount == 0 || userConfirmed;
}

public sealed record IncrementalGeneralChatShadowLine(
    int LineNumber,
    string? ChatPrefix,
    string MessageBody,
    OcrFusionMetadata Fusion,
    IReadOnlyList<string> OcrLocales,
    string? SessionFingerprint,
    string Route,
    string DedupStatus,
    IReadOnlyList<string> CanonicalIds,
    IReadOnlyList<string> SemanticConceptIds,
    IReadOnlyList<string> VerifiedTypedEntityIds,
    IReadOnlyList<string> LockedRenderings,
    string? ProtectedInput,
    string? RawProviderOutput,
    string? FinalOutput,
    long ElapsedMilliseconds,
    string? FailureReason,
    string? SenderRelation = null,
    IReadOnlyList<string>? OpposingIdentityRenderings = null)
{
    public bool ModelCalled => Route is "general_translation" or "hybrid";
    public RuntimeChatEvent? RuntimeEvent { get; init; }
    public SafeRuntimeDisplayMessage? SafeDisplay { get; init; }
    public RuntimePresentationRejection? PresentationRejection { get; init; }
}

public sealed record IncrementalGeneralChatShadowResult(
    string SessionType,
    string RcSha256,
    string SemanticPackSha256,
    string RenderingPackSha256,
    string ModelDigest,
    string TargetLocale,
    IReadOnlyList<IncrementalGeneralChatShadowLine> Lines,
    int ModelCallCount,
    int DeduplicatedCount,
    int NoOutputCount,
    int DisplayedNewCount)
{
    public int FailClosedCount => Lines.Count(line =>
        line.FinalOutput is null && line.Route is not "deduplicated");
}

public static class IncrementalGeneralChatShadowLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoLTranslator",
        "incremental-general-chat-shadow.v0.1.jsonl");

    public static string Append(IncrementalGeneralChatShadowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string? directory = Path.GetDirectoryName(DefaultPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Cannot resolve local Shadow log directory.");
        Directory.CreateDirectory(directory);
        var record = new
        {
            record_type = result.SessionType,
            recorded_at = DateTimeOffset.UtcNow,
            record_origin = "incremental_general_chat_shadow_local_only",
            formal_eligibility = "not_eligible",
            intake_status = "local_shadow_only_not_for_data_import",
            redaction_status = "body_fingerprint_and_output_local_only_not_export_ready",
            screenshot_in_log = false,
            screenshot_written_to_dataset = false,
            rc_sha256 = result.RcSha256,
            semantic_pack_sha256 = result.SemanticPackSha256,
            rendering_pack_sha256 = result.RenderingPackSha256,
            model_digest = result.ModelDigest,
            target_locale = result.TargetLocale,
            model_call_count = result.ModelCallCount,
            deduplicated_count = result.DeduplicatedCount,
            lines = result.Lines.Select(line => new
            {
                line_number = line.LineNumber,
                session_local_fingerprint = line.SessionFingerprint,
                dedup_status = line.DedupStatus,
                route = line.Route,
                fusion_status = line.Fusion.Status,
                fusion_confidence = line.Fusion.Confidence,
                failure_reason = line.FailureReason,
                canonical_ids = line.CanonicalIds,
                semantic_concept_ids = line.SemanticConceptIds,
                verified_typed_entity_ids = line.VerifiedTypedEntityIds,
                locked_renderings = line.LockedRenderings,
                final_output = line.FinalOutput,
                elapsed_milliseconds = line.ElapsedMilliseconds
            })
        };
        File.AppendAllText(DefaultPath, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        return DefaultPath;
    }
}
