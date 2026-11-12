using LoLTranslator.App;
using SlangGraph.Core;

internal static class RuntimePresentationChecks
{
    public static bool Run(OcrShadowLineResult prototype, OcrShadowSemanticService semantics)
    {
        int total = 0; var failures = new List<string>();
        void Check(bool ok, string name) { total++; if (!ok) failures.Add(name); }
        var provider = new OutputProvider();
        var service = IncrementalGeneralChatShadowService.CreateExplicit(semantics, provider: provider);
        var context = new ShadowMatchContextOption("test", "lol.global", "in_game_chat");
        var terms = OfficialTrilingualRuntimeTerms.LoadOfficialSourceApproved(
            System.IO.Path.Combine(AppContext.BaseDirectory, "data/runtime/official-trilingual-runtime-terms.v0.1.jsonl"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "data/runtime/official-trilingual-runtime-term-ambiguities.v0.1.jsonl"));
        var repair = OfficialGameEntityOcrRepair.Load(
            System.IO.Path.Combine(AppContext.BaseDirectory, "data/packs/lol/domain-lexicon.v0.1.champion-official.rc.2.jsonl"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "data/packs/lol/domain-lexicon.v0.1.candidate.2.jsonl"));
        var projector = new RuntimePresentationProjector(terms, repair);
        RuntimeSourceStyle Style(RuntimeTextColor actor, RuntimeTextColor? target = null, bool gold = false) =>
            new(actor, RuntimeColorEvidence.PixelRegion, gold, target is { } color
                ? new[] { new RuntimeIdentityColorHint("Ireha", color, RuntimeColorEvidence.PixelRegion) }
                : Array.Empty<RuntimeIdentityColorHint>());
        IReadOnlyList<VisibleChatMessage> Events(string text, RuntimeSourceStyle? style = null) => new RuntimeChatEventLedger().Observe(new[]
            { prototype with { Text = text, SourceStyle = style } }).Inserted;
        IncrementalGeneralChatShadowLine Translate(string text, RuntimeSourceStyle? style = null) => service.ProcessEvents(Events(text, style), context, "zh-CN").Lines.Single();
        string Name(string english) => terms.MatchExact(english, new[] { "en-US" }, "zh-CN").Single().TargetText;
        string Text(SafeRuntimeDisplayMessage? message) => message is null ? "" : string.Concat(message.Display.Segments.Select(segment => segment.Text));
        bool Colored(SafeRuntimeDisplayMessage? message, string name, TranslationSidecarTextRole role) =>
            message?.Display.Segments.Any(segment => segment.Text == name && segment.Role == role) == true;

        var invalid = Translate("04:42 [Team] Alice (not-a-champ): hello everyone");
        Check(provider.Calls == 1 && invalid.SafeDisplay is not null && provider.LastInput == "hello everyone" &&
            Colored(invalid.SafeDisplay, "未知英雄", TranslationSidecarTextRole.AlliedIdentity) && !Text(invalid.SafeDisplay).Contains("Alice"),
            "separated_player_body_survives_unknown_champion_without_exposing_id");
        int beforeUnsafe = provider.Calls;
        var unsafeIdentity = Translate("04:42 [Team] Alice: hello everyone");
        Check(unsafeIdentity.SafeDisplay is null && provider.Calls == beforeUnsafe, "missing_champion_boundary_remains_blocked");
        var unknownPing = Translate("04:42 Alice (not-a-champ) is on the way", Style(RuntimeTextColor.Blue));
        Check(unknownPing.SafeDisplay is not null && unknownPing.FinalOutput == "正在路上" && provider.Calls == beforeUnsafe &&
            Colored(unknownPing.SafeDisplay, "未知英雄", TranslationSidecarTextRole.AlliedIdentity) &&
            !Text(unknownPing.SafeDisplay).Contains("Alice"), "known_ping_body_degrades_unknown_actor_without_guess_or_model");
        var unknownColor = Translate("04:42 [All] Alice (Ashe): hello everyone", new(RuntimeTextColor.Unknown,
            RuntimeColorEvidence.Unknown, false, Array.Empty<RuntimeIdentityColorHint>()));
        Check(Colored(unknownColor.SafeDisplay, Name("Ashe"), TranslationSidecarTextRole.Normal) && unknownColor.SenderRelation is null,
            "independent_chat_body_can_use_neutral_identity_without_fabricating_relation");
        var team = Translate("04:42 [Team] xX_7|99 (Sinqed): hello everyone", Style(RuntimeTextColor.Red));
        Check(Colored(team.SafeDisplay, Name("Singed"), TranslationSidecarTextRole.AlliedIdentity), "team_channel_overrides_conflicting_sender_pixels_and_repairs_official_slot");
        Check(!Text(team.SafeDisplay).Contains("xX_7|99") && provider.LastInput == "hello everyone", "opaque_player_id_never_enters_model_or_safe_display");
        Check(Text(team.SafeDisplay).StartsWith("04:42 [队伍]") && Text(team.SafeDisplay).Contains("："), "typed_timestamp_channel_and_chat_delimiter_preserved");

        int calls = provider.Calls;
        var unresolved = Translate("04:42 Alice (Ashe) is on the way");
        Check(unresolved.SafeDisplay is null && unresolved.FailureReason == "actor_color_unresolved" && provider.Calls == calls, "missing_sender_color_does_not_become_fabricated_blue_or_white_row");
        var red = Translate("04:42 [All] Alice (Ashe): hello everyone", Style(RuntimeTextColor.Red));
        Check(Colored(red.SafeDisplay, Name("Ashe"), TranslationSidecarTextRole.EnemyIdentity), "enemy_origin_keeps_red_sender");

        var targetRed = Translate("04:42 Alice (Sinqed) has targeted Enemy_99 (Ireha)", Style(RuntimeTextColor.Blue, RuntimeTextColor.Red, true));
        Check(Colored(targetRed.SafeDisplay, Name("Singed"), TranslationSidecarTextRole.AlliedIdentity) &&
            Colored(targetRed.SafeDisplay, Name("Irelia"), TranslationSidecarTextRole.EnemyIdentity), "sender_and_target_have_independent_colors_and_corrections");
        Check(!Text(targetRed.SafeDisplay).Contains("Enemy_99") && !Text(targetRed.SafeDisplay).Contains("：") &&
            targetRed.SafeDisplay?.Display.Segments.Any(segment => segment.Role == TranslationSidecarTextRole.System) == true, "target_ping_hides_id_preserves_no_colon_and_gold_action");
        var sameSide = Translate("04:42 Alice (Singed) has targeted Enemy_99 (Irelia)", Style(RuntimeTextColor.Blue, RuntimeTextColor.Blue));
        Check(Colored(sameSide.SafeDisplay, Name("Irelia"), TranslationSidecarTextRole.AlliedIdentity), "target_is_not_automatically_opposite_sender");
        var redBoth = Translate("04:42 Alice (Singed) has targeted Enemy_99 (Irelia)", Style(RuntimeTextColor.Red, RuntimeTextColor.Red));
        Check(Colored(redBoth.SafeDisplay, Name("Singed"), TranslationSidecarTextRole.EnemyIdentity) &&
            Colored(redBoth.SafeDisplay, Name("Irelia"), TranslationSidecarTextRole.EnemyIdentity), "red_sender_does_not_force_target_blue");
        var unknownTarget = Translate("04:42 Alice (Singed) has targeted Enemy_99 (Irelia)", Style(RuntimeTextColor.Blue));
        Check(Colored(unknownTarget.SafeDisplay, Name("Irelia"), TranslationSidecarTextRole.Normal), "unknown_target_stays_neutral_not_guessed");
        var conflict = Style(RuntimeTextColor.Blue) with { Targets = new[]
        {
            new RuntimeIdentityColorHint("Irelia", RuntimeTextColor.Red, RuntimeColorEvidence.PixelRegion),
            new RuntimeIdentityColorHint("Irelia", RuntimeTextColor.Blue, RuntimeColorEvidence.PixelRegion)
        } };
        var conflicting = Translate("04:42 Alice (Singed) has targeted Enemy_99 (Irelia)", conflict);
        Check(Colored(conflicting.SafeDisplay, Name("Irelia"), TranslationSidecarTextRole.Normal), "conflicting_target_evidence_is_neutral");

        PreparedRuntimePresentation Prepare(string text, RuntimeSourceStyle? style = null) => projector.Prepare(Events(text, style).Single().Line, "zh-CN").Plan!;
        SafeRuntimeDisplayMessage? Compose(PreparedRuntimePresentation plan, string output) => projector.Compose(plan, output, "zh-CN", out _);
        var item = Prepare("04:42 [Team] Alice (Ashe): buy Control Ward");
        Check(Colored(Compose(item, "购买" + Name("Control Ward")), Name("Control Ward"), TranslationSidecarTextRole.Item), "source_grounded_official_item_has_its_own_gold_span");
        var chat = Prepare("04:42 [Team] Alice (Ashe): hello everyone");
        Check(!Colored(Compose(chat, Name("Control Ward")), Name("Control Ward"), TranslationSidecarTextRole.Item), "hallucinated_item_is_not_trusted_by_output_dictionary_alone");
        var flash = Prepare("04:42 Alice (Ashe): Irelia used Flash", Style(RuntimeTextColor.Blue));
        Check(Colored(Compose(flash, Name("Irelia") + "使用了" + Name("Flash")), Name("Flash"), TranslationSidecarTextRole.Item), "source_grounded_summoner_spell_is_gold_not_identity_red");
        var neutral = Prepare("04:42 Objective Bounties are falling off soon", Style(RuntimeTextColor.Unknown, gold: true));
        var neutralView = Compose(neutral, "目标赏金即将结束");
        Check(neutralView?.Display.Segments.Where(segment => segment.Role != TranslationSidecarTextRole.Muted).All(segment => segment.Role == TranslationSidecarTextRole.System) == true &&
            !Text(neutralView).Contains("游戏"), "neutral_system_has_gold_body_without_fabricated_actor");

        provider.Output = "Alice 说你好";
        var leaked = Translate("04:42 [Team] Alice (Ashe): hello everyone");
        Check(leaked.SafeDisplay is null && leaked.FinalOutput is null && leaked.FailureReason == "output_identity_or_envelope_leak", "model_leak_rejected_before_ui");
        provider.Output = "你好";
        calls = provider.Calls;
        var idInBody = Translate("04:42 [Team] Alice (Ashe): ask Alice for help");
        Check(idInBody.FailureReason == "player_identifier_in_model_body" && provider.Calls == calls, "recognized_sender_id_in_body_blocked_before_model");
        var ann = Translate("04:42 [Team] Ann (Ashe): we cannot wait");
        Check(ann.SafeDisplay is not null, "identifier_boundary_does_not_match_ann_inside_cannot");
        var source = Events("04:42 [Team] Alice (Ashe): hello everyone").Single();
        var result = service.ProcessEvents(new[] { source }, context, "zh-CN").Lines.Single();
        var tampered = result with { ChatPrefix = "09:99 Enemy_99 (Irelia):", SenderRelation = "enemy" };
        Check(tampered.SafeDisplay == result.SafeDisplay && result.RuntimeEvent == source.Line.RuntimeEvent &&
            result.SafeDisplay?.EventId == source.OccurrenceKey, "display_contract_keeps_identity_and_ignores_legacy_prefix_or_relation_mutation");
        var malformedGoldSource = prototype with
        {
            Text = "04:42 PrivatePlayer (Ashe) ???", SourceStyle = Style(RuntimeTextColor.Unknown, gold: true),
            Fusion = prototype.Fusion with { Reasons = prototype.Fusion.Reasons.Concat(new[] { "lol_system_gold_row" }).ToArray() }
        };
        var malformedGold = service.ProcessEvents(new RuntimeChatEventLedger().Observe(new[] { malformedGoldSource }).Inserted, context, "zh-CN").Lines.Single();
        Check(malformedGold.SafeDisplay is null && malformedGold.FailureReason == "neutral_event_structure_unresolved", "gold_pixels_alone_cannot_turn_raw_player_row_into_safe_neutral_text");
        Check(typeof(TranslationSidecarWindow).GetMethod("Present")?.GetParameters().Single().ParameterType == typeof(SafeRuntimeDisplayMessage) &&
            typeof(TranslationSidecarWindow).GetMethod("ApplyDetailed") is null &&
            typeof(TranslationSidecarWindow).GetMethod("HasTrustedPresentation") is null,
            "window_api_only_accepts_projected_display_not_translation_result");
        var batchProvider = new BatchOutputProvider();
        var batchService = IncrementalGeneralChatShadowService.CreateExplicit(semantics, provider: batchProvider);
        // The legacy batch route requires fused rows; keep single-source fixtures above
        // exercising the independent coordinate path rather than relaxing that guard.
        var batchPrototype = prototype with { Fusion = prototype.Fusion with { Status = "fused" } };
        var batchEvents = new RuntimeChatEventLedger().Observe(new[]
        {
            batchPrototype with { LineNumber = 1, Text = "04:42 [Team] Alice (Ashe): hello everyone" },
            batchPrototype with { LineNumber = 1, Text = "04:42 [Team] Bob: private unsafe input" },
            batchPrototype with { LineNumber = 1, Text = "04:42 [Team] Carol (Braum): thanks everyone" }
        }).Inserted;
        var batchResult = batchService.ProcessEvents(batchEvents, context, "zh-CN");
        Check(batchProvider.LastInputs.SequenceEqual(new[] { "hello everyone", "thanks everyone" }) && batchResult.ModelCallCount == 1,
            "batch_preflight_excludes_invalid_row_and_both_player_headers_before_model");
        Check(batchResult.Lines.Count == 3 && batchResult.Lines[1].SafeDisplay is null &&
            batchResult.Lines.Where(line => line.SafeDisplay is not null).Select(line => line.SafeDisplay!.EventId)
                .SequenceEqual(new[] { batchEvents[0].OccurrenceKey, batchEvents[2].OccurrenceKey }),
            "batch_filter_keeps_original_event_mapping_across_colliding_ocr_line_numbers");

        provider.Output = "Hello folks!";
        var echo = Translate("13:05 [Team] Agent Copter (Aliscar): hello folks");
        Check(echo.FinalOutput is null && echo.SafeDisplay is null && echo.FailureReason == "untranslated_source_echo",
            "foreign_source_echo_is_not_success_even_with_changed_case_or_punctuation");
        int beforeIntent = provider.Calls;
        var praise = Translate("13:05 [Team] Agent Copter (Aliscar): wp boys");
        Check(praise.FinalOutput == "打得好，兄弟们" && praise.SafeDisplay is not null && provider.Calls == beforeIntent,
            "structured_praise_bypasses_model_and_keeps_repaired_actor");
        provider.Output = "抱歉";
        var apology = Translate("13:06 [Team] always (Zer1): sry");
        Check(apology.SafeDisplay is not null && apology.FinalOutput == "抱歉" && provider.Calls == beforeIntent && !Text(apology.SafeDisplay).Contains("always"),
            "apology_not_lost_when_official_slot_cannot_be_repaired");
        Check(TranslationOutputQuality.Failure("hello", "hello", "en-US", "en-GB") is null &&
            TranslationOutputQuality.Failure("123", "123", "en-US", "zh-CN") is null,
            "same_language_and_numeric_outputs_are_not_false_echo_failures");
        batchProvider.Echo = true;
        var echoBatch = new RuntimeChatEventLedger().Observe(new[]
        {
            batchPrototype with { LineNumber = 1, Text = "13:05 [Team] Alice (Aliscar): hello folks" },
            batchPrototype with { LineNumber = 2, Text = "13:06 [Team] Bob (Zer1): see you later" }
        }).Inserted;
        var rejectedEchoes = batchService.ProcessEvents(echoBatch, context, "zh-CN");
        Check(rejectedEchoes.Lines.All(line => line.SafeDisplay is null && line.FailureReason == "untranslated_source_echo"),
            "batch_uses_same_quality_gate_as_single_chat");
        var intents = ConversationIntentResolver.Current;
        foreach (string boundary in new[] { "wp?", "not wp", "wp boys but we lost", "sry if you think so", "brb in 10", "ask sry", "wp PrivatePlayer", "ty for help", "\"wp\"" })
            Check(!intents.TryResolve(boundary, "zh-CN", out _), "intent_boundary_" + boundary);
        Check(intents.TryResolve("Well played, team!", "zh-CN", out var teamPraise) && teamPraise!.Text == "打得好，队友们" &&
            intents.TryResolve("thanks guys", "zh-CN", out var thanks) && thanks!.Text == "谢谢，兄弟们", "compositional_intent_and_address_variants");
        Check(!intents.TryResolve("wp boys", "ko-KR", out _), "unsupported_intent_locale_returns_to_model");
        batchProvider.Echo = false;
        var mixedEvents = new RuntimeChatEventLedger().Observe(new[]
        {
            batchPrototype with { LineNumber = 1, Text = "13:05 [Team] Alice (Aliscar): wp boys" },
            batchPrototype with { LineNumber = 2, Text = "13:06 [Team] Bob (Zer1): sry" },
            batchPrototype with { LineNumber = 3, Text = "13:06 [Team] Carol (Ashe): hello folks" },
            batchPrototype with { LineNumber = 4, Text = "13:06 [Team] Dave (Braum): see you later" }
        }).Inserted;
        var mixed = batchService.ProcessEvents(mixedEvents, context, "zh-CN");
        Check(batchProvider.LastInputs.SequenceEqual(new[] { "hello folks", "see you later" }) && mixed.ModelCallCount == 1 &&
            mixed.Lines.Select(l => l.SafeDisplay?.EventId).SequenceEqual(mixedEvents.Select(e => e.OccurrenceKey)),
            "intent_and_batch_outputs_keep_event_mapping_without_sending_known_intents");
        foreach (string failure in failures) Console.WriteLine("RUNTIME PRESENTATION FAIL: " + failure);
        Console.WriteLine($"Runtime safe-presentation checks: {total - failures.Count}/{total}");
        return failures.Count == 0;
    }

    private sealed class OutputProvider : ILocalTranslationAttemptProvider
    {
        public int Calls; public string? LastInput; public string Output = "你好";
        public GeneralTranslationResult Translate(GeneralTranslationRequest request) => new("succeeded", TranslateWithDiagnostics(request).FinalOutput, "test", null);
        public LocalTranslationAttempt TranslateWithDiagnostics(GeneralTranslationRequest request)
        {
            Calls++; LastInput = request.Text;
            return new("test", "test", "test", "test", request.Text, request.Text, Output, Output, 0, null, false);
        }
    }
    private sealed class BatchOutputProvider : IBatchLocalTranslationAttemptProvider
    {
        public bool Echo;
        public string[] LastInputs = Array.Empty<string>();
        public GeneralTranslationResult Translate(GeneralTranslationRequest request) => throw new InvalidOperationException("Expected batch path");
        public LocalTranslationAttempt TranslateWithDiagnostics(GeneralTranslationRequest request) => throw new InvalidOperationException("Expected batch path");
        public BatchTranslationAttempt TranslateBatchWithDiagnostics(IReadOnlyList<BatchTranslationItem> items)
        {
            LastInputs = items.Select(item => item.Text).ToArray();
            return new(items.Select(item => new BatchTranslationItemResult(item.ItemId, Echo ? item.Text : "你好", Echo ? item.Text : "你好", null)).ToArray(), 0, null);
        }
    }
}
