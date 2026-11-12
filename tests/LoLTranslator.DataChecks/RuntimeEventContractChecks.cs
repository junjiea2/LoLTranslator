using LoLTranslator.App;

internal static class RuntimeEventContractChecks
{
    public sealed record Result(int Total, IReadOnlyList<string> Failures);
    public static Result Run(OcrShadowLineResult prototype, OcrShadowSemanticService semantics)
    {
        prototype = prototype with { SourceStyle = new(RuntimeTextColor.Blue, RuntimeColorEvidence.PixelRegion,
            false, Array.Empty<RuntimeIdentityColorHint>()) };
        var failures = new List<string>();
        int total = 0;
        OcrShadowLineResult Row(int n, string text) => prototype with { LineNumber = n, Text = text };
        void Require(bool condition, string name) { total++; if (!condition) failures.Add(name); }
        var alice = Row(1, "04:42 Alice (Ashe) is on the way");
        var bob = Row(2, "04:42 Bob (Braum) is on the way");
        var ledger = new RuntimeChatEventLedger();
        _ = ledger.Observe(new[] { alice });
        var newActor = ledger.Observe(new[] { alice, bob });
        Require(newActor.Inserted.Count == 1, "same_second_different_actor_must_survive");
        var pair = new RuntimeChatEventLedger().Observe(new[] { alice, bob });
        var queue = new ContinuousTranslationMessageQueue();
        queue.Enqueue(pair.Inserted);
        Require(pair.Inserted.Count == 2 && queue.Count == 2, "queue_must_preserve_event_identity");
        var duplicate = new RuntimeChatEventLedger().Observe(new[] { alice, alice with { LineNumber = 2 } });
        Require(duplicate.Inserted.Count == 1, "same_observation_duplicate_must_not_be_emitted_twice");
        var chat = new RuntimeChatEventLedger().Observe(new[]
        {
            Row(1, "04:44 [Team] Alice (Ashe): I purchased a sandwich")
        });
        Require(chat.Inserted.SingleOrDefault()?.EventKind == RuntimeChatEventKind.PlayerText,
            "player_channel_must_not_be_reclassified_by_body_keyword");
        Require(pair.Inserted.First().Line.Text == alice.Text, "classification_must_preserve_original_ocr_text");
        var spelling = new RuntimeChatEventLedger().Observe(new[]
        {
            Row(1, "17:45 Alice (Ashe): Jayce used Ignite")
        });
        Require(spelling.Inserted.Count == 1 && !spelling.Inserted[0].Line.Fusion.Reasons.Contains(
            "lol_system_action_without_delimiter"), "colon_ping_must_preserve_delimiter");
        var timeline = new RuntimeChatEventLedger();
        _ = timeline.Observe(new[] { Row(1, "08:45 [Team] Alice (Ashe): go mid") });
        var historical = timeline.Observe(new[] { Row(1, "08:30 [Team] Alice (Ashe): wait bot") });
        Require(historical.Inserted.Count == 0 && historical.Decisions!.Any(d => d.Decision == "late_historical_event"),
            "late_discovery_must_be_recorded_not_appended_as_live_chat");
        var distinct = new RuntimeChatEventLedger().Observe(new[]
        {
            Row(1, "08:45 [Team] Alice (Ashe): go mid"),
            Row(2, "08:45 [Team] Alice (Ashe): no mid")
        });
        Require(distinct.Inserted.Count == 2, "same_actor_same_second_distinct_text_must_survive");
        var bounded = new ContinuousTranslationMessageQueue(1);
        RuntimeQueueAdmission admission = bounded.Enqueue(pair.Inserted);
        Require(admission.Accepted == 1 && admission.OverflowRejected.Count == 1 && bounded.Count == 1 &&
            bounded.OverflowRejectedCount == 1, "capacity_limit_requires_explicit_rejection");
        Require(bounded.Enqueue(pair.Inserted).AlreadyAdmitted == 2 && bounded.Count == 1,
            "queue_idempotency_must_use_event_ids_including_rejected_ids");

        var timeCheck = new RuntimeChatEventLedger();
        _ = timeCheck.Observe(new[] { Row(1, "08:45 [Team] Alice (Ashe): go mid") });
        var typo = timeCheck.Observe(new[] { Row(1, "28:46 [Team] Alice (Ashe): hello") });
        var validTime = timeCheck.Observe(new[] { Row(1, "08:46 [Team] Alice (Ashe): hello") });
        Require(typo.Inserted.Count == 0 && typo.Decisions!.Any(d => d.Decision == "held_implausible_timestamp_jump") &&
            validTime.Inserted.Count == 1, "ocr_timestamp_jump_must_not_poison_watermark");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var pings = new RuntimeChatEventLedger(() => now);
        _ = pings.Observe(new[] { alice });
        now = now.AddSeconds(4);
        var repeatedPing = pings.Observe(new[] { Row(1, "04:46 Alice (Ashe) is on the way") });
        now = now.AddSeconds(15);
        var laterPing = pings.Observe(new[] { Row(1, "05:01 Alice (Ashe) is on the way") });
        Require(repeatedPing.Inserted.Count == 0 && repeatedPing.Decisions!.Any(d => d.Decision == "suppressed_ping_burst") &&
            laterPing.Inserted.Count == 1, "ping_burst_policy_belongs_to_ledger_and_expires");

        var provider = new RecordingLocalTranslationProvider();
        var translator = IncrementalGeneralChatShadowService.CreateExplicit(semantics, provider: provider);
        var context = new ShadowMatchContextOption("contract", "lol.global", "in_game_chat");
        var pingResult = translator.ProcessEvents(pair.Inserted, context, "zh-CN");
        Require(pingResult.Lines.Count == 2 && pingResult.Lines.All(line => line.FinalOutput == "正在路上" &&
            line.RuntimeEvent?.Kind == RuntimeChatEventKind.Ping) && provider.LastRequest is null,
            "typed_ping_needs_no_string_type_tag_and_never_calls_model");
        Require(translator.ProcessEvents(pair.Inserted, context, "zh-CN").Lines.All(line => line.Route != "deduplicated"),
            "translation_service_must_not_own_semantic_dedup");

        var playerEvent = chat.Inserted.Single();
        var misleadingTags = playerEvent with
        {
            Line = playerEvent.Line with
            {
                Fusion = playerEvent.Line.Fusion with
                {
                    Reasons = playerEvent.Line.Fusion.Reasons.Concat(new[] { "runtime_event_system", "lol_system_notification" }).ToArray()
                }
            }
        };
        var playerResult = translator.ProcessEvents(new[] { misleadingTags }, context, "zh-CN").Lines.Single();
        Require(playerResult.RuntimeEvent?.Id == playerEvent.OccurrenceKey && playerResult.ModelCalled &&
            provider.LastRequest?.Text == "I purchased a sandwich",
            "typed_player_contract_overrides_legacy_reason_tags_and_keeps_id_out_of_model");
        var coverage = new TranslationCoverageTracker();
        coverage.ObserveFrame(new VisibleMessageDiffResult(new[] { misleadingTags }, 1));
        Require(coverage.Snapshot.QueuedMessages == 1 && coverage.Snapshot.QueuedSystemMessages == 0,
            "coverage_category_uses_typed_contract_not_conflicting_reason_tags");

        var unknown = new RuntimeChatEventLedger().Observe(new[]
        {
            Row(1, "09:00 Alice (Ashe) has slain Somebody (Braum)!")
        }).Inserted;
        var rejectingProvider = new RecordingLocalTranslationProvider();
        var rejectingTranslator = IncrementalGeneralChatShadowService.CreateExplicit(semantics, provider: rejectingProvider);
        var unknownResult = rejectingTranslator.ProcessEvents(unknown, context, "zh-CN").Lines.Single();
        Require(unknownResult.RuntimeEvent?.Kind == RuntimeChatEventKind.System && unknownResult.FinalOutput is null &&
            rejectingProvider.LastRequest is null, "unknown_typed_system_never_falls_through_to_model");

        var uncertain = new RuntimeChatEventLedger().Observe(new[]
        {
            Row(1, "09:01 Alice (Ashe): I purchased a sandwich")
        }).Inserted;
        var uncertainResult = rejectingTranslator.ProcessEvents(uncertain, context, "zh-CN").Lines.Single();
        Require(uncertainResult.RuntimeEvent?.Kind == RuntimeChatEventKind.Unknown &&
            uncertainResult.FinalOutput is null && rejectingProvider.LastRequest is null,
            "missing_channel_unclassified_body_must_not_be_guessed_or_sent_to_model");

        var punctuation = spelling.Inserted.Single().Line.RuntimeEvent;
        Require(punctuation is { SourceHasDelimiter: true } && pair.Inserted.All(item => item.Line.RuntimeEvent is
            { SourceHasDelimiter: false }), "delimiter_is_a_typed_source_property");
        var missingColonRows = new[] {
            Row(1, "13:35 [Team] Alice (Ashe) shutdown for mao"),
            Row(2, "13:35 [Team] Alice (Ashe) its fine") };
        var missingColon = new RuntimeChatEventLedger().Observe(missingColonRows).Inserted;
        Require(missingColon.Count == 2 && missingColon.All(m => m.EventKind == RuntimeChatEventKind.PlayerText),
            "same_second_missing_colon_distinct_bodies_are_independent_events");
        Require(missingColon.Count == 2 && missingColon[0].Line.RuntimeEvent!.Envelope.Body == "shutdown for mao" &&
            missingColon[1].Line.RuntimeEvent!.Envelope.Body == "its fine" &&
            missingColon.All(m => !m.Line.RuntimeEvent!.SourceHasDelimiter && m.Line.RuntimeEvent.Envelope.RecoveredMissingDelimiter),
            "recover_only_explicit_channel_complete_actor_boundary_without_inventing_colon");
        Require(missingColon.Select(m => m.Line.Text).SequenceEqual(missingColonRows.Select(r => r.Text)),
            "boundary_recovery_preserves_raw_ocr");
        Require(new[] { "13:35 Alice (Ashe) hello", "13:35 [Team] Alice Ashe hello", "13:35 [Team] Alice (Ashe hello",
            "garbage [Team] Alice (Ashe) hello" }.All(t => !ChatMessageEnvelopeExtractor.Extract(t).RecoveredMissingDelimiter),
            "missing_channel_or_damaged_parentheses_or_unknown_prefix_never_repaired_by_guess");
        var recoveredOutput = translator.ProcessEvents(new[] { missingColon[1] }, context, "zh-CN");
        Require(provider.LastRequest?.Text == "its fine", "recovered_body_not_opaque_player_name_enters_model");

        Dictionary<int, (string Hash, OcrBoundingBox Bounds)> Geometry(double y = 10) =>
            new() { [1] = ("coarse", new OcrBoundingBox(10, y, 400, 20)) };
        var stable = Row(1, "17:05 [All] Alice (Ashe): keep focusing brand guys");
        var drift = stable with { Text = "17:05 [All] A1ice (Ashe): keep focusing brand guys" };
        var visualLedger = new RuntimeChatEventLedger();
        visualLedger.Observe(new[] { stable }, Geometry());
        var driftResult = visualLedger.Observe(new[] { drift }, Geometry());
        Require(driftResult.Inserted.Count == 0 && driftResult.Decisions!.Any(d => d.Decision == "suppressed_visible_actor_ocr_variant"),
            "consecutive_same_visual_row_exact_body_slot_time_side_suppresses_actor_ocr_drift");
        var differentBody = drift with { Text = "17:05 [All] A1ice (Ashe): stop focusing brand guys" };
        Require(visualLedger.Observe(new[] { differentBody }, Geometry()).Inserted.Count == 1,
            "visual_association_must_not_merge_different_body");
        var movedLedger = new RuntimeChatEventLedger();
        movedLedger.Observe(new[] { stable }, Geometry());
        Require(movedLedger.Observe(new[] { drift }, Geometry(40)).Inserted.Count == 1,
            "same_second_same_body_other_visual_row_not_merged");
        var redLedger = new RuntimeChatEventLedger();
        redLedger.Observe(new[] { stable }, Geometry());
        Require(redLedger.Observe(new[] { drift with { SourceStyle = new(RuntimeTextColor.Red,
            RuntimeColorEvidence.PixelRegion, false, Array.Empty<RuntimeIdentityColorHint>()) } }, Geometry()).Inserted.Count == 1,
            "different_source_side_not_merged");
        var noGeometryLedger = new RuntimeChatEventLedger();
        noGeometryLedger.Observe(new[] { stable });
        Require(noGeometryLedger.Observe(new[] { drift }).Inserted.Count == 1,
            "no_visual_evidence_does_not_guess_player_identity");
        visualLedger.Reset();
        Require(visualLedger.Observe(new[] { drift }, Geometry()).Inserted.Count == 1, "session_reset_clears_visual_association");
        var symbols = new RuntimeChatEventLedger().Observe(new[] { Row(1, "12:47 [Team] Alice (Ashe): :(") }).Inserted;
        Require(symbols.Count == 1, "recognized_emoticon_is_a_player_message_not_ocr_noise");
        var symbolOutput = translator.ProcessEvents(symbols, context, "zh-CN");
        Require(symbolOutput.Lines.Single().FinalOutput == ":(" && symbolOutput.ModelCallCount == 0 &&
            symbolOutput.Lines.Single().SafeDisplay is not null, "emoticon_preserves_symbol_with_safe_identity_without_model");
        Require(new RuntimeChatEventLedger().Observe(new[] { Row(1, "12:47 [Team] Alice (Ashe): . - :") }).Inserted.Count == 0,
            "arbitrary_punctuation_is_not_promoted_as_emoticon");
        foreach ((string text, string expected) in new[]
        {
            ("11:45 Alice (Vayne): Ahri used Flash", "九尾妖狐 使用了 闪现"),
            ("12:15 Alice (Vayne): Kalista Exhaust", "复仇之矛 使用了 虚弱"),
            ("13:45 Alice (Syndra): Hecarim Ghost", "战争之影 使用了 幽灵疾步"),
            ("13:51 Alice (Nocturne): Yunara R - Ready", "不破之誓 的终极技能已就绪"),
            ("15:35 Alice (Nocturne) signals to all in", "发起全力进攻信号"),
            ("11:47 Alice (Nocturne) has stolen the Infernal Drake!", "抢到了 the Infernal Drake"),
            ("13:45 Alice (Taric): Cloud Drake - Spawning in 0:54", "Cloud Drake 将在 0:54 后刷新"),
            ("11:59 Alice (Vayne) signals enemy has vision here", "标记：敌方在此处有视野")
        })
        {
            var eventRows = new RuntimeChatEventLedger().Observe(new[] { Row(1, text) }).Inserted;
            Require(eventRows.Count == 1 && eventRows[0].EventKind is RuntimeChatEventKind.Ping or RuntimeChatEventKind.System,
                "screenshot_structured_event_classified_" + text);
            IncrementalGeneralChatShadowResult translated = translator.ProcessEvents(eventRows, context, "zh-CN");
            Require(translated.ModelCallCount == 0 && translated.Lines.Single().FinalOutput == expected,
                "screenshot_structured_event_deterministic_" + text + "_actual_" + translated.Lines.Single().FinalOutput +
                "_failure_" + translated.Lines.Single().FailureReason);
        }
        var surrenderRow = Row(1, "15:18 A surrender vote has been started! Type /surrender or") with
        {
            SourceStyle = new(RuntimeTextColor.Gold, RuntimeColorEvidence.PixelRegion, true,
                Array.Empty<RuntimeIdentityColorHint>())
        };
        var surrender = new RuntimeChatEventLedger().Observe(new[] { surrenderRow }).Inserted;
        Require(surrender.Count == 1 && surrender[0].Line.RuntimeEvent!.SystemEnvelopeKind == RuntimeSystemEnvelopeKind.KnownNeutralAnnouncement,
            "known_neutral_announcement_has_typed_envelope");
        Require(translator.ProcessEvents(surrender, context, "zh-CN").Lines.Single().FinalOutput == "已发起投降投票",
            "known_neutral_announcement_deterministic_without_fragment_guess");
        var bareDrake = new RuntimeChatEventLedger().Observe(new[] { Row(1, "11:48 Alice (Nocturne) has stolen drake") }).Inserted;
        Require(translator.ProcessEvents(bareDrake, context, "zh-CN").Lines.Single().FailureReason ==
            "blocked_official_term_ambiguity_no_model_guess", "bare_drake_stays_fail_closed_outside_complete_objective_slot");
        var damagedStructuredActor = new RuntimeChatEventLedger().Observe(new[]
        {
            Row(1, "13:38 Alice (Xqzzv) is on the way")
        }).Inserted;
        var damagedStructuredOutput = translator.ProcessEvents(damagedStructuredActor, context, "zh-CN").Lines.Single();
        Require(damagedStructuredOutput.FinalOutput == "正在路上" && damagedStructuredOutput.ModelCalled == false &&
            damagedStructuredOutput.SafeDisplay!.Display.Segments.Any(segment => segment.Text == "未知英雄" &&
                segment.Role == TranslationSidecarTextRole.AlliedIdentity),
            "known_structured_body_survives_unresolved_actor_as_colored_unknown_without_guess");
        var damagedUnknownActor = new RuntimeChatEventLedger().Observe(new[]
        {
            Row(1, "13:39 Alice (Xqzzv): did something unfamiliar")
        }).Inserted;
        Require(translator.ProcessEvents(damagedUnknownActor, context, "zh-CN").Lines.Single().FinalOutput is null,
            "unknown_structured_body_never_uses_unresolved_actor_degradation");
        return new(total, failures);
    }
}
