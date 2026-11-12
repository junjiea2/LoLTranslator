using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>
/// A named, locally configurable Ollama profile.  The tag and digest are kept
/// separate so evaluation artifacts can identify the exact local model without
/// hard-coding a runner into the processing-route contract.
/// </summary>
public sealed record OllamaTranslationProfile(
    string ProfileId,
    string ModelTag,
    string PromptTemplateVersion,
    string ModelDigest,
    bool DisableThinking,
    string TemplateKind,
    string KeepAlive = "10m");

public sealed record LocalTranslationAttempt(
    string ProfileId,
    string ModelTag,
    string ModelDigest,
    string PromptTemplateVersion,
    string Input,
    string Prompt,
    string? RawOutput,
    string? FinalOutput,
    long ElapsedMilliseconds,
    string? Error,
    bool UsedFallback);

/// <summary>
/// Optional diagnostics for local providers.  IGeneralTranslationProvider stays
/// intentionally small so the existing unavailable/fake test providers remain
/// valid and a future runner is not locked to Ollama.
/// </summary>
public interface ILocalTranslationAttemptProvider : IGeneralTranslationProvider
{
    LocalTranslationAttempt TranslateWithDiagnostics(GeneralTranslationRequest request);
}

public sealed record BatchTranslationItem(string ItemId, string Text, string SourceLocale, string TargetLocale);
public sealed record BatchTranslationItemResult(string ItemId, string? RawOutput, string? FinalOutput, string? Error);
public sealed record BatchTranslationAttempt(IReadOnlyList<BatchTranslationItemResult> Items, long ElapsedMilliseconds, string? Error);

/// <summary>Optional extension: callers can batch independent, already-protected
/// inputs without changing the single-item provider contract used by tests.</summary>
public interface IBatchLocalTranslationAttemptProvider : ILocalTranslationAttemptProvider
{
    BatchTranslationAttempt TranslateBatchWithDiagnostics(IReadOnlyList<BatchTranslationItem> items);
}

/// <summary>
/// Local-only Ollama adapter.  It calls the loopback API, asks for a single
/// non-streaming result and records the unmodified response separately from the
/// trimmed final result.  It never contacts an external translation service.
/// </summary>
public sealed class OllamaLocalTranslationProvider : IBatchLocalTranslationAttemptProvider
{
    private static readonly System.Text.RegularExpressions.Regex ThinkingControlLeak = new(
        @"(?ix)(?:/no[_\s-]*think\b|\bno\s+think(?:ing)?\b|\bthink(?:ing)?\s+(?:off|disabled)\b)",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly HttpClient SharedClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/"),
        Timeout = TimeSpan.FromMinutes(3)
    };

    public OllamaLocalTranslationProvider(OllamaTranslationProfile profile, HttpClient? client = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _client = client ?? SharedClient;
    }

    private readonly HttpClient _client;

    public OllamaTranslationProfile Profile { get; }

    public LocalTranslationAttempt? LastAttempt { get; private set; }

    public GeneralTranslationResult Translate(GeneralTranslationRequest request)
    {
        LocalTranslationAttempt attempt = TranslateWithDiagnostics(request);
        LastAttempt = attempt;
        return attempt.Error is null && !string.IsNullOrWhiteSpace(attempt.FinalOutput)
            ? new GeneralTranslationResult("succeeded", attempt.FinalOutput, Profile.ProfileId, null)
            : new GeneralTranslationResult(
                "unavailable",
                null,
                Profile.ProfileId,
                attempt.Error ?? "local_translation_empty_output");
    }

    public LocalTranslationAttempt TranslateWithDiagnostics(GeneralTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string prompt = string.Empty;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            prompt = BuildPrompt(request);
            var payload = new OllamaGenerateRequest(
                Profile.ModelTag,
                prompt,
                Stream: false,
                Think: Profile.DisableThinking ? false : null,
                KeepAlive: Profile.KeepAlive,
                Options: new OllamaGenerationOptions(Temperature: 0, NumCtx: 2048));
            using HttpResponseMessage response = _client
                .PostAsJsonAsync("api/generate", payload)
                .GetAwaiter()
                .GetResult();
            response.EnsureSuccessStatusCode();
            OllamaGenerateResponse? generated = response.Content
                .ReadFromJsonAsync<OllamaGenerateResponse>()
                .GetAwaiter()
                .GetResult();
            string? raw = generated?.Response;
            (string? final, string? validationError) = ValidateSingleTranslation(raw);
            if (final is not null && HasUnexpectedThinkingControlLeak(request.Text, final))
            {
                final = null;
                validationError = "local_translation_control_token_leak";
            }
            stopwatch.Stop();
            return new LocalTranslationAttempt(
                Profile.ProfileId,
                Profile.ModelTag,
                Profile.ModelDigest,
                Profile.PromptTemplateVersion,
                request.Text,
                prompt,
                raw,
                final,
                stopwatch.ElapsedMilliseconds,
                validationError,
                UsedFallback: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new LocalTranslationAttempt(
                Profile.ProfileId,
                Profile.ModelTag,
                Profile.ModelDigest,
                Profile.PromptTemplateVersion,
                request.Text,
                prompt,
                null,
                null,
                stopwatch.ElapsedMilliseconds,
                "ollama_local_provider_error:" + exception.GetType().Name,
                UsedFallback: false);
        }
    }

    public BatchTranslationAttempt TranslateBatchWithDiagnostics(IReadOnlyList<BatchTranslationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return new BatchTranslationAttempt(Array.Empty<BatchTranslationItemResult>(), 0, null);
        Stopwatch stopwatch = Stopwatch.StartNew();
        string prompt = "Translate each item to its requested target language. Return ONLY strict JSON: {\"items\":[{\"id\":\"...\",\"translation\":\"...\"}]}. " +
            TranslationOutputQuality.GameInstruction +
            "Keep every id exactly once. Do not add explanation, prefixes, newlines, or fields. " +
            "Inputs may contain minor OCR or typing errors; infer the intended wording only when unambiguous. " +
            TranslationOutputQuality.ConversationInstruction +
            "Preserve player and user names verbatim and never translate or rewrite them.\n" +
            JsonSerializer.Serialize(items.Select(item => new { id = item.ItemId, text = item.Text, source_locale = item.SourceLocale, target_locale = item.TargetLocale }));
        try
        {
            var payload = new OllamaGenerateRequest(Profile.ModelTag, prompt, false, Profile.DisableThinking ? false : null,
                Profile.KeepAlive, new OllamaGenerationOptions(Temperature: 0, NumCtx: 4096));
            using HttpResponseMessage response = _client.PostAsJsonAsync("api/generate", payload).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            string? raw = response.Content.ReadFromJsonAsync<OllamaGenerateResponse>().GetAwaiter().GetResult()?.Response;
            var translated = ParseBatch(raw, items);
            stopwatch.Stop();
            return new BatchTranslationAttempt(translated, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new BatchTranslationAttempt(items.Select(item => new BatchTranslationItemResult(item.ItemId, null, null,
                "ollama_local_provider_error:" + exception.GetType().Name)).ToArray(), stopwatch.ElapsedMilliseconds,
                "ollama_local_provider_error:" + exception.GetType().Name);
        }
    }

    private static IReadOnlyList<BatchTranslationItemResult> ParseBatch(string? raw, IReadOnlyList<BatchTranslationItem> items)
    {
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw ?? string.Empty);
            if (document.RootElement.TryGetProperty("items", out JsonElement values) && values.ValueKind == JsonValueKind.Array)
                foreach (JsonElement value in values.EnumerateArray())
                    if (value.TryGetProperty("id", out JsonElement id) && value.TryGetProperty("translation", out JsonElement translation) &&
                        id.ValueKind == JsonValueKind.String && translation.ValueKind == JsonValueKind.String)
                        mapped.TryAdd(id.GetString()!, translation.GetString()!);
        }
        catch (JsonException) { }
        return items.Select(item =>
        {
            if (!mapped.TryGetValue(item.ItemId, out string? value))
                return new BatchTranslationItemResult(item.ItemId, null, null, "batch_item_missing_or_invalid_json");
            (string? final, string? error) = ValidateSingleTranslation(value);
            if (final is not null && HasUnexpectedThinkingControlLeak(item.Text, final))
                return new BatchTranslationItemResult(item.ItemId, value, null, "local_translation_control_token_leak");
            return new BatchTranslationItemResult(item.ItemId, value, final, error);
        }).ToArray();
    }

    private static bool HasUnexpectedThinkingControlLeak(string source, string output) =>
        ThinkingControlLeak.IsMatch(output) && !ThinkingControlLeak.IsMatch(source);

    public string BuildPrompt(GeneralTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Profile.TemplateKind switch
        {
            // This is the published TranslateGemma/Ollama direct-translation
            // prompt.  Ollama applies the model's installed Gemma chat envelope.
            "translategemma_direct" =>
                "Please translate the following English text into Simplified Chinese:\n" + request.Text,
            "qwen3_non_thinking" =>
                TranslationOutputQuality.GameInstruction +
                $"Translate this League of Legends in-game chat from {request.SourceLocale} to {request.TargetLocale} in natural, short language like a player would say. " +
                "Output only the translation. Do not explain or add information. Preserve negation, numbers, times, conditions, objects, and uncertainty. " +
                "The input may contain minor OCR or typing errors; infer the intended wording only when unambiguous. " +
                TranslationOutputQuality.ConversationInstruction +
                "Preserve player and user names verbatim and never translate or rewrite them.\n" +
                "Text: " + request.Text,
            _ => throw new InvalidOperationException($"Unsupported Ollama translation template '{Profile.TemplateKind}'.")
        };
    }

    /// <summary>
    /// A translation provider is not allowed to turn explanations, reasoning or
    /// alternate answers into user-visible translation text.  This intentionally
    /// fails closed; callers receive an unavailable result and may offer an
    /// explicit retry, never a silently degraded final translation.
    /// </summary>
    private static (string? FinalOutput, string? Error) ValidateSingleTranslation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, "local_translation_empty_output");
        }

        string normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return (null, "local_translation_empty_output");
        }

        if (normalized.Contains('\n'))
        {
            return (null, "local_translation_format_violation_multiline");
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
                normalized,
                @"(?is)<think>|</think>|^\s*(?:analysis|reasoning|translation|译文|翻译)\s*[:：]"))
        {
            return (null, "local_translation_format_violation_prefix_or_thinking");
        }

        return (normalized, null);
    }

    private sealed record OllamaGenerateRequest(
        string Model,
        string Prompt,
        bool Stream,
        bool? Think,
        string KeepAlive,
        OllamaGenerationOptions Options);

    private sealed record OllamaGenerationOptions(int Temperature, int NumCtx);

    private sealed record OllamaGenerateResponse(string? Response);
}

/// <summary>
/// Protects verified rendering text from a general model during hybrid output.
/// The model receives opaque markers for only the locked semantic spans.  If it
/// drops or duplicates a marker the result is rejected, never silently replaced
/// with a model-controlled rendering.
/// </summary>
public sealed class ProtectedHybridTranslationAggregator : IHybridTranslationAggregator
{
    private readonly ILocalTranslationAttemptProvider _provider;

    public ProtectedHybridTranslationAggregator(ILocalTranslationAttemptProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Most recent sequential local attempt for runtime diagnostics.  The UI
    /// should consume this only on the same processing call; batch runners are
    /// deliberately single-threaded to keep provenance unambiguous.
    /// </summary>
    public LocalTranslationAttempt? LastAttempt { get; private set; }

    public string? LastProtectedInput { get; private set; }

    public HybridAggregationResult Aggregate(HybridAggregationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        HybridLockedSpan[] spans = HybridLockedSpanPlanner.Plan(request)
            .Concat(request.AdditionalLockedSpans ?? Array.Empty<HybridLockedSpan>())
            .OrderBy(span => span.Start)
            .ToArray();
        if (!HasSafeNonOverlappingSpans(spans, request.RawText.Length))
        {
            return new HybridAggregationResult("unavailable", null, "hybrid_locked_span_invalid");
        }
        string protectedInput = request.RawText;
        foreach (HybridLockedSpan span in spans.OrderByDescending(item => item.Start))
        {
            protectedInput = protectedInput.Remove(span.Start, span.Length).Insert(span.Start, span.Marker);
        }

        LastProtectedInput = protectedInput;

        LocalTranslationAttempt attempt = _provider.TranslateWithDiagnostics(
            new GeneralTranslationRequest(protectedInput, request.SourceLocale, request.TargetLocale));
        LastAttempt = attempt;
        if (attempt.Error is not null || string.IsNullOrWhiteSpace(attempt.FinalOutput))
        {
            return new HybridAggregationResult("unavailable", null, attempt.Error ?? "hybrid_empty_provider_output");
        }

        string output = attempt.FinalOutput;
        foreach (HybridLockedSpan span in spans)
        {
            int markerCount = CountOccurrences(output, span.Marker);
            if (markerCount != 1)
            {
                return new HybridAggregationResult("unavailable", null, "hybrid_locked_marker_not_preserved");
            }

            output = output.Replace(span.Marker, span.VerifiedRendering, StringComparison.Ordinal);
        }

        return new HybridAggregationResult("succeeded", output, null);
    }

    private static bool HasSafeNonOverlappingSpans(
        IReadOnlyList<HybridLockedSpan> spans,
        int textLength)
    {
        int previousEnd = 0;
        foreach (HybridLockedSpan span in spans)
        {
            if (span.Start < previousEnd || span.Start < 0 || span.Length <= 0 || span.Start + span.Length > textLength ||
                string.IsNullOrWhiteSpace(span.Marker) || string.IsNullOrWhiteSpace(span.VerifiedRendering))
            {
                return false;
            }

            previousEnd = span.Start + span.Length;
        }

        return true;
    }

    private static int CountOccurrences(string value, string marker)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }
}

public sealed record HybridLockedSpan(int Start, int Length, string Marker, string VerifiedRendering);

/// <summary>
/// Runtime span planning is deliberately conservative.  It does not add an
/// expression to a Pack: it only locates the observed, already verified English
/// surface so the corresponding published rendering cannot be overwritten.
/// </summary>
public static class HybridLockedSpanPlanner
{
    private static readonly System.Text.RegularExpressions.Regex FlashSurface = new(
        @"\b(?:(?:top|mid|bot|adc|support|jungle)\s+)?no\s+f\b",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex CareSurface = new(
        @"\b(?:(?:top|mid|bot)\s+)?care\b",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static IReadOnlyList<HybridLockedSpan> Plan(HybridAggregationRequest request)
    {
        var spans = new List<HybridLockedSpan>();
        foreach ((SlangGraphSemanticMatch match, SlangGraphRenderedConcept rendering) in request.SemanticMatches.Zip(request.Renderings))
        {
            System.Text.RegularExpressions.Match surface = match.ConceptId switch
            {
                "game.lol:summoner.flash.unavailable" => FlashSurface.Match(request.RawText),
                "game.lol:lane.caution" => CareSurface.Match(request.RawText),
                _ => System.Text.RegularExpressions.Match.Empty
            };

            if (surface.Success && spans.All(existing => existing.Start != surface.Index))
            {
                spans.Add(new HybridLockedSpan(
                    surface.Index,
                    surface.Length,
                    $"[[SG_LOCKED_{spans.Count + 1}]]",
                    rendering.Text));
            }
        }

        return spans;
    }
}
