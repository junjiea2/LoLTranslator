using LoLTranslator.App;

internal static class RuntimeDispatchChecks
{
    public static async Task<bool> Run(OcrShadowLineResult prototype, OcrShadowSemanticService semantics)
    {
        var failures = new List<string>(); int total = 0;
        void Require(bool condition, string name) { total++; if (!condition) failures.Add(name); }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var context = new ShadowMatchContextOption("dispatch", "lol.global", "in_game_chat");
        VisibleChatMessage Message(long sequence, RuntimeChatEventKind kind, string? id = null)
        {
            string key = id ?? $"test:{sequence}";
            var envelope = new ChatMessageEnvelope("04:42 [Team] Alice (Ashe):", "hello", true, "04:42", "Team", "Alice (Ashe)");
            var contract = new RuntimeChatEvent(key, sequence, kind, "ALICE", "04:42", now, envelope, kind == RuntimeChatEventKind.PlayerText);
            return new(prototype with { RuntimeEvent = contract }, "hello", key, TimestampKey: "04:42", DetectedAtUtc: now);
        }
        var slow = new GateProcessor();
        var log = new List<RuntimeDispatchTransition>(); var output = new List<RuntimeDispatchDelivery>();
        using var dispatcher = new RuntimeTranslationDispatcher(Ready, slow.Process, utcNow: () => now);
        void Poll() { var update = dispatcher.Advance(); log.AddRange(update.Transitions); output.AddRange(update.Deliveries); }
        dispatcher.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        Poll(); await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.Enqueue(Enumerable.Range(2, 20).Select(n => Message(n, RuntimeChatEventKind.Ping)), context, "zh-CN");
        await Until(() => output.Count == 20, Poll);
        Require(output.Count == 20 && !slow.Release.Task.IsCompleted, "pings_complete_without_model_return_or_new_ocr_frame");
        Require(output.Select(item => item.Message.Line.RuntimeEvent!.Sequence).SequenceEqual(Enumerable.Range(2, 20).Select(n => (long)n)), "same_second_burst_preserves_ledger_order");
        slow.Release.TrySetResult(); await Until(() => dispatcher.ActiveWorkerCount == 0, Poll);
        Require(output.Count == 21 && output.Count(item => item.Message.OccurrenceKey == "test:1") == 1 &&
            !log.Any(item => item.State == RuntimeDispatchState.Superseded), "fast_ping_does_not_cancel_valid_older_player_translation");
        var terminals = log.Where(item => item.State is not (RuntimeDispatchState.Queued or RuntimeDispatchState.Processing)).ToArray();
        Require(terminals.Length == 21 && terminals.GroupBy(item => item.Message.OccurrenceKey).All(group => group.Count() == 1), "exactly_one_terminal_dispatch_state_per_event");

        var fastGate = new GateProcessor(); var modelGate = new GateProcessor();
        using var simultaneous = new RuntimeTranslationDispatcher(fastGate.Process, modelGate.Process, utcNow: () => now);
        simultaneous.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText), Message(2, RuntimeChatEventKind.Ping) }, context, "zh-CN");
        simultaneous.Advance();
        await Task.WhenAll(fastGate.Started.Task, modelGate.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        fastGate.Release.TrySetResult(); modelGate.Release.TrySetResult();
        await Task.WhenAll(fastGate.Returned.Task, modelGate.Returned.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => simultaneous.CompletedWorkerCount == 2, () => { });
        var both = simultaneous.Advance();
        Require(both.Deliveries.Select(item => item.Message.Line.RuntimeEvent!.Sequence).SequenceEqual(new long[] { 1, 2 }), "simultaneous_completions_merge_before_watermark");

        var stuck = new GateProcessor();
        using var bounded = new RuntimeTranslationDispatcher(Ready, stuck.Process, modelCapacity: 1, utcNow: () => now);
        bounded.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        bounded.Advance(); await stuck.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bounded.Enqueue(new[] { Message(2, RuntimeChatEventKind.PlayerText), Message(3, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        var capacity = bounded.Advance();
        Require(bounded.ModelQueued == 1 && bounded.OverflowRejectedCount == 1 && capacity.Transitions.Any(t => t.Message.OccurrenceKey == "test:3" && t.Reason == "lane_capacity_exceeded"), "model_capacity_rejection_is_explicit");
        bounded.Enqueue(new[] { Message(3, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        Require(bounded.Advance().Transitions.Count == 0 && bounded.OverflowRejectedCount == 1, "rejected_id_stays_idempotent");
        now = now.AddSeconds(5);
        var timeout = bounded.Advance();
        Require(timeout.Transitions.Count(t => t.State == RuntimeDispatchState.Expired) == 2 && timeout.Transitions.Any(t => t.Reason == "queue_deadline_exceeded") && timeout.Transitions.Any(t => t.Reason == "processing_deadline_exceeded"), "queue_and_processing_both_count_toward_deadline");
        Require(stuck.Cancellation.IsCancellationRequested && bounded.ActiveWorkerCount == 1 && stuck.Calls == 1, "timeout_does_not_abandon_physical_worker");
        for (int index = 4; index < 24; index++)
        {
            bounded.Enqueue(new[] { Message(index, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
            now = now.AddSeconds(5);
            bounded.Advance();
        }
        Require(bounded.RetainedMessageCount == 1 && stuck.Calls == 1,
            "expired_queued_payloads_do_not_accumulate_behind_stuck_provider");
        bounded.Reset();
        bounded.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText, "new:1"), Message(2, RuntimeChatEventKind.Ping, "new:2") }, context, "zh-CN");
        var afterReset = new List<RuntimeDispatchDelivery>();
        await Until(() => afterReset.Count == 1, () => afterReset.AddRange(bounded.Advance().Deliveries));
        Require(afterReset[0].Message.OccurrenceKey == "new:2" && stuck.Calls == 1, "new_session_fast_lane_survives_old_model_worker");
        stuck.Release.TrySetResult(); await Until(() => bounded.ActiveWorkerCount == 0, () => afterReset.AddRange(bounded.Advance().Deliveries));
        Require(afterReset.All(item => item.Message.OccurrenceKey.StartsWith("new:", StringComparison.Ordinal)), "cancelled_session_never_publishes_old_output");
        bounded.Enqueue(new[] { Message(3, RuntimeChatEventKind.PlayerText, "new:3") }, context, "zh-CN");
        await Until(() => afterReset.Count == 3, () => afterReset.AddRange(bounded.Advance().Deliveries));
        Require(stuck.Calls == 3 && afterReset.Last().Message.OccurrenceKey == "new:3" &&
            afterReset.Any(item => item.Message.OccurrenceKey == "new:1"), "model_slot_recovers_and_preserves_new_session_player_after_old_provider_returns");

        var pendingGate = new GateProcessor();
        using var cancel = new RuntimeTranslationDispatcher(Ready, pendingGate.Process, utcNow: () => now);
        cancel.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        cancel.Advance(); await pendingGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Enqueue(new[] { Message(2, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        Require(cancel.Reset().Transitions.Count(t => t.State == RuntimeDispatchState.Cancelled) == 2, "reset_reports_queued_and_processing_cancellations");
        pendingGate.Release.TrySetResult(); await Until(() => cancel.ActiveWorkerCount == 0, () => cancel.Advance());

        int unknownCalls = 0;
        using var unknown = new RuntimeTranslationDispatcher((work, token) => { unknownCalls++; return Ready(work, token); }, Ready, utcNow: () => now);
        unknown.Enqueue(new[] { Message(1, RuntimeChatEventKind.Unknown) }, context, "zh-CN");
        Require(unknown.Advance().Transitions.Single().Reason == "unclassified_event" && unknownCalls == 0, "unknown_never_enters_worker");

        using var faulty = new RuntimeTranslationDispatcher((_, _) => throw new InvalidOperationException("private payload"), Ready, utcNow: () => now);
        faulty.Enqueue(new[] { Message(1, RuntimeChatEventKind.Ping) }, context, "zh-CN");
        var faults = new List<RuntimeDispatchTransition>();
        await Until(() => faults.Any(t => t.State == RuntimeDispatchState.Failed), () => faults.AddRange(faulty.Advance().Transitions));
        Require(faults.Last().Reason == "processor_failure", "exception_has_terminal_reason_without_payload");

        using var malformed = new RuntimeTranslationDispatcher((work, _) => Task.FromResult(Result(work) with { Lines = Array.Empty<IncrementalGeneralChatShadowLine>() }), Ready, utcNow: () => now);
        malformed.Enqueue(new[] { Message(1, RuntimeChatEventKind.Ping) }, context, "zh-CN");
        var bad = new List<RuntimeDispatchTransition>(); var badOutputs = new List<RuntimeDispatchDelivery>();
        await Until(() => bad.Any(t => t.State == RuntimeDispatchState.Failed), () => { var update = malformed.Advance(); bad.AddRange(update.Transitions); badOutputs.AddRange(update.Deliveries); });
        Require(badOutputs.Count == 0 && bad.Last().Reason == "invalid_event_result_contract", "invalid_result_contract_never_publishes");

        var olderSafe = new GateProcessor();
        using var safeGate = new RuntimeTranslationDispatcher((work, _) =>
        {
            var value = Result(work);
            return Task.FromResult(value with { Lines = value.Lines.Select(line => line with { SafeDisplay = null }).ToArray() });
        }, olderSafe.Process, utcNow: () => now);
        safeGate.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText), Message(2, RuntimeChatEventKind.Ping) }, context, "zh-CN");
        var safeStates = new List<RuntimeDispatchTransition>(); var safeResults = new List<RuntimeDispatchDelivery>();
        void PollSafe() { var update = safeGate.Advance(); safeStates.AddRange(update.Transitions); safeResults.AddRange(update.Deliveries); }
        await Until(() => safeStates.Any(t => t.Reason == "missing_safe_display_contract"), PollSafe);
        olderSafe.Release.TrySetResult();
        await Until(() => safeGate.ActiveWorkerCount == 0, PollSafe);
        Require(safeResults.Count == 1 && safeResults[0].Message.Line.RuntimeEvent!.Sequence == 1,
            "unsafe_newer_output_does_not_advance_release_watermark_past_safe_older_event");

        using var plain = new RuntimeTranslationDispatcher(Ready, Ready, utcNow: () => now);
        plain.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText), Message(2, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        var plainOutput = new List<RuntimeDispatchDelivery>();
        await Until(() => plainOutput.Count == 2, () => plainOutput.AddRange(plain.Advance().Deliveries));
        Require(plainOutput.Count == 2, "normal_chat_completes_without_new_capture");

        using var staleCapture = new RuntimeTranslationDispatcher(Ready, Ready, utcNow: () => now);
        var staleMessage = Message(1, RuntimeChatEventKind.PlayerText);
        staleMessage = staleMessage with { Line = staleMessage.Line with { RuntimeEvent = staleMessage.Line.RuntimeEvent! with {
            FirstObservedCaptureAtUtc = now.AddSeconds(-5),
            FrameTiming = new(999, now.AddMilliseconds(-100), now, now) } } };
        staleCapture.Enqueue(new[] { staleMessage }, context, "zh-CN");
        var staleUpdate = staleCapture.Advance();
        Require(staleUpdate.Transitions.Any(t => t.State == RuntimeDispatchState.Expired && t.Reason == "queue_deadline_exceeded") &&
            staleCapture.ActiveWorkerCount == 0, "recent_reobservation_cannot_refresh_old_capture_deadline");

        using var echoFailure = new RuntimeTranslationDispatcher(Ready, (work, _) =>
        {
            var result = Result(work);
            return Task.FromResult(result with { Lines = result.Lines.Select(line => line with
                { FinalOutput = null, SafeDisplay = null, FailureReason = "untranslated_source_echo" }).ToArray() });
        }, utcNow: () => now);
        echoFailure.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
        var echoStates = new List<RuntimeDispatchTransition>();
        await Until(() => echoStates.Any(t => t.State == RuntimeDispatchState.Failed), () => echoStates.AddRange(echoFailure.Advance().Transitions));
        Require(echoStates.Last().Reason == "untranslated_source_echo", "echo_quality_failure_has_specific_content_free_trace_reason");

        foreach (string failureCode in new[] { "unknown_ping_template_no_provider_call", "blocked_official_term_ambiguity_no_model_guess", "private unexpected provider text" })
        {
            using var explained = new RuntimeTranslationDispatcher(Ready, (work, _) => {
                var result = Result(work);
                return Task.FromResult(result with { Lines = result.Lines.Select(line => line with
                    { FinalOutput = null, SafeDisplay = null, FailureReason = failureCode }).ToArray() });
            }, utcNow: () => now);
            explained.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText) }, context, "zh-CN");
            var states = new List<RuntimeDispatchTransition>();
            await Until(() => states.Any(t => t.State == RuntimeDispatchState.Failed), () => states.AddRange(explained.Advance().Transitions));
            Require(states.Last().Reason == (failureCode.StartsWith("private") ? "translation_no_output" : failureCode),
                "diagnostic_failure_whitelist_" + (failureCode.StartsWith("private") ? "redacts_unknown" : failureCode));
        }

        var workers = new RuntimeTranslationWorkers(semantics);
        var realPing = new RuntimeChatEventLedger().Observe(new[]
        {
            prototype with { Text = "04:42 Alice (Ashe) is on the way", SourceStyle = new(RuntimeTextColor.Blue, RuntimeColorEvidence.PixelRegion, false, Array.Empty<RuntimeIdentityColorHint>()) },
            prototype with { LineNumber = 2, Text = "04:42 Bob (Braum) signals that enemies are missing", SourceStyle = new(RuntimeTextColor.Blue, RuntimeColorEvidence.PixelRegion, false, Array.Empty<RuntimeIdentityColorHint>()) }
        }).Inserted;
        var actualTemplates = await Task.Run(() => workers.Deterministic(new(realPing, context, "zh-CN"), CancellationToken.None));
        Require(actualTemplates.ModelCallCount == 0 && actualTemplates.Lines.Select(line => line.FinalOutput)
            .SequenceEqual(new[] { "正在路上", "敌人消失" }), "production_fast_worker_uses_templates_without_ollama");
        bool playerRejected = false;
        try { await workers.Deterministic(new(new[] { Message(1, RuntimeChatEventKind.PlayerText) }, context, "zh-CN"), CancellationToken.None); }
        catch (ArgumentException) { playerRejected = true; }
        Require(playerRejected, "fast_worker_rejects_player_type_before_processing");

        // Overfilling one lane must not consume the other lane's capacity.
        using var isolated = new RuntimeTranslationDispatcher(Ready, Ready, modelCapacity: 1, deterministicCapacity: 1, utcNow: () => now);
        isolated.Enqueue(new[] { Message(1, RuntimeChatEventKind.PlayerText), Message(2, RuntimeChatEventKind.PlayerText), Message(3, RuntimeChatEventKind.Ping) }, context, "zh-CN");
        Require(isolated.FastQueued == 1 && isolated.ModelQueued == 1 && isolated.OverflowRejectedCount == 1, "lane_capacities_are_independent");

        string tracePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dispatch-check-{Guid.NewGuid():N}.jsonl");
        var trace = new ContinuousRuntimeTrace(tracePath);
        trace.AppendDispatch(log.First(item => item.State == RuntimeDispatchState.Released), 0, 0);
        trace.AppendPresentation(output[0], TranslationSidecarApplyResult.Empty);
        string traceText = System.IO.File.ReadAllText(tracePath);
        var records = System.IO.File.ReadAllLines(tracePath).Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        Require(!traceText.Contains("Alice") && !traceText.Contains("hello") && !traceText.Contains("译文") &&
            records[0].RootElement.GetProperty("event_key").GetString() == records[1].RootElement.GetProperty("event_key").GetString(),
            "trace_correlates_dispatch_and_overlay_without_player_or_text");
        foreach (var record in records) record.Dispose();
        System.IO.File.Delete(tracePath);
        foreach (string failure in failures) Console.WriteLine($"RUNTIME DISPATCH FAIL: {failure}");
        Console.WriteLine($"Runtime two-lane dispatch checks: {total - failures.Count}/{total}");
        return failures.Count == 0;
    }

    private static async Task Until(Func<bool> condition, Action poll)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { poll(); if (!condition()) await Task.Delay(5, timeout.Token); }
    }
    private static Task<IncrementalGeneralChatShadowResult> Ready(RuntimeTranslationWork work, CancellationToken cancellation) => Task.FromResult(Result(work));
    private static IncrementalGeneralChatShadowResult Result(RuntimeTranslationWork work)
    {
        var lines = work.Messages.Select((message, index) => new IncrementalGeneralChatShadowLine(
            index + 1, message.Line.RuntimeEvent!.Envelope.Prefix, "hello", message.Line.Fusion, new[] { "en-US" }, message.OccurrenceKey,
            message.EventKind == RuntimeChatEventKind.PlayerText ? "general_translation" : "runtime_ping_template", "displayed_new",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, null, "译文", 0, null, "ally")
            { RuntimeEvent = message.Line.RuntimeEvent,
              SafeDisplay = new(message.OccurrenceKey, message.Line.RuntimeEvent!.Sequence,
                  new(new[] { new TranslationSidecarSegment("译文", TranslationSidecarTextRole.Normal) })) }).ToArray();
        return new("test", "test", "test", "test", "test", work.TargetLocale, lines, lines.Any(line => line.ModelCalled) ? 1 : 0, 0, 0, lines.Length);
    }
    private sealed class GateProcessor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Cancellation { get; private set; }
        public int Calls;
        public async Task<IncrementalGeneralChatShadowResult> Process(RuntimeTranslationWork work, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls); Cancellation = cancellation; Started.TrySetResult();
            await Release.Task;
            var result = Result(work); Returned.TrySetResult(); return result;
        }
    }
}
