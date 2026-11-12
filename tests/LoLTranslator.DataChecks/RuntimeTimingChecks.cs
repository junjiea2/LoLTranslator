using System.Text.Json;
using LoLTranslator.App;

internal static class RuntimeTimingChecks
{
    public static bool Run(OcrShadowLineResult prototype, OcrShadowSemanticService semantics)
    {
        int count = 0; var failures = new List<string>();
        void Check(bool ok, string label) { count++; if (!ok) failures.Add(label); }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var timing = new RuntimeFrameTiming(72, now.AddSeconds(-3), now.AddSeconds(-2), now.AddSeconds(-1));
        var row = prototype with
        {
            Text = "04:42 PrivateFixture (Ashe) is on the way", FrameTiming = timing,
            SourceStyle = new(RuntimeTextColor.Blue, RuntimeColorEvidence.PixelRegion, false, Array.Empty<RuntimeIdentityColorHint>())
        };
        var message = new RuntimeChatEventLedger(() => now).Observe(new[] { row }).Inserted.Single();
        Check(message.Line.RuntimeEvent?.FrameTiming == timing && message.Line.RuntimeEvent.ObservedAtUtc == now,
            "capture_and_admission_are_distinct");
        var weakLedger = new RuntimeChatEventLedger(() => now);
        var first = weakLedger.Observe(new[] { row }, allowImmediateTimestampedRelease: false);
        var laterTiming = timing with { FrameId = 73, CapturedAtUtc = now.AddSeconds(-1) };
        var confirmed = weakLedger.Observe(new[] { row with { FrameTiming = laterTiming } }, allowImmediateTimestampedRelease: false).Inserted.Single();
        Check(first.Inserted.Count == 0 && confirmed.Line.RuntimeEvent!.FirstObservedCaptureAtUtc == timing.CapturedAtUtc &&
            confirmed.Line.RuntimeEvent.FrameTiming == laterTiming, "first_exact_observation_survives_confirmation_frame");
        var mismatchLedger = new RuntimeChatEventLedger(() => now);
        mismatchLedger.Observe(new[] { row }, allowImmediateTimestampedRelease: false);
        var changed = row with { Text = "04:42 PrivateFixture (Ashe) signals that enemies are missing", FrameTiming = laterTiming };
        mismatchLedger.Observe(new[] { changed }, allowImmediateTimestampedRelease: false);
        var different = mismatchLedger.Observe(new[] { changed }, allowImmediateTimestampedRelease: false).Inserted.Single();
        Check(different.Line.RuntimeEvent!.FirstObservedCaptureAtUtc == laterTiming.CapturedAtUtc,
            "same_timestamp_different_action_never_inherits_first_observation");
        var workers = new RuntimeTranslationWorkers(semantics);
        var output = workers.Deterministic(new(new[] { message }, new("test", "lol.global", "in_game_chat"), "zh-CN"), default).GetAwaiter().GetResult();
        SafeRuntimeDisplayMessage safe = output.Lines.Single().SafeDisplay!;
        Check(safe?.FrameTiming == timing, "timing_survives_real_template_and_projection");
        if (safe is null) return false;
        var tracking = new RuntimeViewTracking(() => now);
        var receipts = new List<RuntimeViewReceipt>(); tracking.Receipt += receipts.Add;
        tracking.Submitted(safe);
        Check(receipts.Count == 0, "submission_is_not_a_render_ack");
        now = now.AddMilliseconds(120);
        tracking.RenderOpportunity(Array.Empty<string>());
        Check(receipts.Count == 0, "hidden_or_clipped_rows_are_not_acknowledged");
        tracking.RenderOpportunity(new[] { safe.EventId });
        tracking.RenderOpportunity(new[] { safe.EventId });
        Check(receipts.Count == 1 && receipts[0].Status == "render_opportunity", "render_callback_counts_once");
        Check(receipts[0].CaptureAgeMilliseconds == 3120 && receipts[0].SubmittedAgeMilliseconds == 120,
            "capture_to_render_includes_ocr_and_buffer_wait");
        Check(receipts[0].FirstObservedAgeMilliseconds == 3120, "first_observation_age_survives_projection_and_render_receipt");
        tracking.Removed(safe.EventId, "expired");
        Check(receipts.Last().Status == "expired_after_render_opportunity" && tracking.ActiveCount == 0, "removal_after_render");
        // Replay one 16-message submission burst before any render callback.
        var queue = new Queue<string>(); receipts.Clear();
        foreach (int n in Enumerable.Range(1, 16))
        {
            string id = "burst:" + n; tracking.Submitted(safe with { EventId = id, Sequence = n }); queue.Enqueue(id);
            if (queue.Count > 8) tracking.Removed(queue.Dequeue(), "capacity_evicted");
        }
        Check(receipts.Count == 8 && receipts.All(r => r.Status == "capacity_evicted_before_render_opportunity"), "burst_loss_is_not_counted_as_seen");
        tracking.RenderOpportunity(queue);
        Check(receipts.Count(r => r.Status == "render_opportunity") == 8 && tracking.ActiveCount == 8, "only_retained_burst_rows_get_opportunity");
        tracking.Reset();
        Check(tracking.ActiveCount == 0 && receipts.Count(r => r.Status == "session_reset_after_render_opportunity") == 8, "reset_releases_tracking");
        tracking.Submitted(safe with { EventId = "no-timing", FrameTiming = null });
        tracking.RenderOpportunity(new[] { "no-timing" });
        Check(receipts.Last().CaptureAgeMilliseconds is null, "missing_capture_time_is_not_zero_latency");

        string path = Path.Combine(Path.GetTempPath(), "lol-timing-check-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var trace = new ContinuousRuntimeTrace(path, "timing-test");
            trace.AppendFrameDisposition(timing.FrameId, timing.CapturedAtUtc, "captured");
            trace.AppendFrameTiming(timing, 1);
            trace.AppendDispatch(new(message, RuntimeTranslationLane.Deterministic, RuntimeDispatchState.Released, "released_for_presentation", 10), 0, 0);
            trace.AppendPresentation(new(message, output), new(1, 1, 0, 0, 0));
            trace.AppendViewReceipt(new(safe.EventId, safe.Sequence, "render_opportunity", now, 120, 3120));
            trace.AppendObservationDecisions(72, new RuntimeChatEventLedger(() => now).Observe(new[] { row, row }));
            string[] lines = File.ReadAllLines(path);
            var records = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Check(records[2].RootElement.GetProperty("event_key").GetString() == records[4].RootElement.GetProperty("event_key").GetString() &&
                    records[2].RootElement.GetProperty("frame_id").GetInt64() == 72,
                    "capture_dispatch_and_render_join_without_text");
                Check(records[1].RootElement.GetProperty("buffer_wait_ms").GetInt64() == 1000 &&
                    records[1].RootElement.GetProperty("preprocessing_and_ocr_ms").GetInt64() == 1000, "buffer_and_ocr_breakdown");
                Check(!string.Join("", lines).Contains("PrivateFixture") && !string.Join("", lines).Contains("Ashe") &&
                    records[1].RootElement.GetProperty("build_id").GetString() == RuntimeBuildInfo.Id, "trace_is_content_free_and_versioned");
                Check(records[5].RootElement.GetProperty("decisions").GetProperty("inserted_typed_event").GetInt32() == 1 &&
                    records[5].RootElement.GetProperty("decisions").GetProperty("suppressed_event_ledger_duplicate").GetInt32() == 1,
                    "prequeue_decision_counts_explain_admission_without_logging_text");
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally { File.Delete(path); }
        foreach (string failure in failures) Console.WriteLine("RUNTIME TIMING FAIL: " + failure);
        Console.WriteLine($"Runtime capture/view timing checks: {count - failures.Count}/{count}");
        return failures.Count == 0;
    }
}
