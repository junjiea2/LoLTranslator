using System.Text.RegularExpressions;
using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>
/// Separates the processing destination from the semantic state. A semantic
/// no-match is information for routing, never a completed user-facing result.
/// </summary>
public static class ChatProcessingRoutes
{
    public const string GeneralTranslation = "general_translation";
    public const string SemanticRendering = "semantic_rendering";
    public const string Hybrid = "hybrid";
    public const string HumanReview = "human_review";

    public const string SemanticMatchFound = "semantic_match_found";
    public const string SemanticNoMatch = "semantic_no_match";
}

public sealed record ChatProcessingRequest(
    string RawText,
    string SourceLocale,
    string TargetLocale,
    ShadowMatchContextOption Context,
    ChatProcessingPolicy? Policy = null,
    IReadOnlyList<HybridLockedSpan>? AdditionalLockedSpans = null);

/// <summary>
/// A caller-supplied policy contract, for example from a reviewed evaluation
/// case or a future local review classifier. It is not inferred from Pack data.
/// </summary>
public sealed record ChatProcessingPolicy(
    string ProcessingRoute,
    ChatSemanticPolicy SemanticPolicy);

public sealed record ChatSemanticPolicy(
    string Mode,
    IReadOnlyList<string> ConceptIds,
    IReadOnlyList<string> ForbiddenConceptIds);

public sealed record ChatSemanticPolicyDecision(
    string Mode,
    bool IsSatisfied,
    bool SuppressedFormalMatches,
    IReadOnlyList<string> Violations);

public sealed record GeneralTranslationRequest(
    string Text,
    string SourceLocale,
    string TargetLocale);

public sealed record GeneralTranslationResult(
    string Status,
    string? Text,
    string? ProviderId,
    string? FailureReason)
{
    public bool IsAvailable => string.Equals(Status, "succeeded", StringComparison.OrdinalIgnoreCase);
}

public interface IGeneralTranslationProvider
{
    GeneralTranslationResult Translate(GeneralTranslationRequest request);
}

/// <summary>
/// Explicit local default until a user-approved translation provider is wired in.
/// It intentionally returns no text rather than copying an evaluation oracle.
/// </summary>
public sealed class UnavailableGeneralTranslationProvider : IGeneralTranslationProvider
{
    public GeneralTranslationResult Translate(GeneralTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new GeneralTranslationResult(
            "unavailable",
            null,
            null,
            "general_translation_provider_unavailable");
    }
}

public sealed record HybridAggregationRequest(
    string RawText,
    string SourceLocale,
    string TargetLocale,
    IReadOnlyList<SlangGraphSemanticMatch> SemanticMatches,
    IReadOnlyList<SlangGraphRenderedConcept> Renderings,
    GeneralTranslationResult GeneralTranslation,
    IReadOnlyList<HybridLockedSpan>? AdditionalLockedSpans = null);

public sealed record HybridAggregationResult(
    string Status,
    string? Text,
    string? FailureReason);

public interface IHybridTranslationAggregator
{
    HybridAggregationResult Aggregate(HybridAggregationRequest request);
}

/// <summary>
/// A concrete failure state for hybrid routing before an implementation is ready.
/// The route remains visible to callers instead of silently collapsing to a
/// semantic fragment or a no-match terminal state.
/// </summary>
public sealed class UnavailableHybridTranslationAggregator : IHybridTranslationAggregator
{
    public HybridAggregationResult Aggregate(HybridAggregationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new HybridAggregationResult(
            "unavailable",
            null,
            request.GeneralTranslation.IsAvailable
                ? "hybrid_aggregation_unavailable"
                : "hybrid_requires_general_translation_provider");
    }
}

public sealed record HumanReviewGateDecision(
    bool RequiresReview,
    string? Reason);

public sealed record ChatProcessingResult(
    string ProcessingRoute,
    string SemanticState,
    IReadOnlyList<SlangGraphSemanticMatch> SemanticMatches,
    IReadOnlyList<SlangGraphSemanticMatch> ObservedSemanticMatches,
    ChatSemanticPolicyDecision? SemanticPolicy,
    IReadOnlyList<SlangGraphRenderedConcept> SemanticRenderings,
    GeneralTranslationResult? GeneralTranslation,
    HybridAggregationResult? HybridAggregation,
    HumanReviewGateDecision HumanReview,
    string Status,
    string? OutputText);

/// <summary>
/// Runtime-only routing over the frozen semantic and rendering releases.
/// It does not create data records, enable drafts, or infer new expressions.
/// </summary>
public sealed class ChatProcessingRouteOrchestrator
{
    private static readonly Regex UncertaintyRegex = new(
        @"\b(?:may|might|maybe|perhaps|unsure|uncertain|not\s+sure|didn't\s+see|did\s+not\s+see)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ContrastRegex = new(
        @"\b(?:but|however|although)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CompositeMessageRegex = new(
        @"[,;]|\b(?:then|and|but|while|after|before|if)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly OcrShadowSemanticService _semanticService;
    private readonly IGeneralTranslationProvider _generalTranslationProvider;
    private readonly IHybridTranslationAggregator _hybridAggregator;

    public ChatProcessingRouteOrchestrator(
        OcrShadowSemanticService semanticService,
        IGeneralTranslationProvider? generalTranslationProvider = null,
        IHybridTranslationAggregator? hybridAggregator = null)
    {
        _semanticService = semanticService ?? throw new ArgumentNullException(nameof(semanticService));
        _generalTranslationProvider = generalTranslationProvider ?? new UnavailableGeneralTranslationProvider();
        _hybridAggregator = hybridAggregator ?? new UnavailableHybridTranslationAggregator();
    }

    public ChatProcessingResult Process(ChatProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<OcrShadowLineResult> lines = _semanticService.Match(
            request.RawText,
            request.SourceLocale,
            request.Context);
        SlangGraphSemanticMatch[] observedMatches = lines.SelectMany(line => line.Matches).ToArray();
        (SlangGraphSemanticMatch[] matches, ChatSemanticPolicyDecision? policyDecision) =
            ApplySemanticPolicy(observedMatches, request.Policy?.SemanticPolicy);
        SlangGraphRenderedConcept[] renderings = matches
            .Select(match => _semanticService.Render(match, request.TargetLocale))
            .ToArray();
        string semanticState = matches.Length == 0
            ? ChatProcessingRoutes.SemanticNoMatch
            : ChatProcessingRoutes.SemanticMatchFound;
        HumanReviewGateDecision review = EvaluateHumanReviewGate(
            request.RawText,
            matches,
            request.Policy?.ProcessingRoute);

        if (review.RequiresReview)
        {
            return new ChatProcessingResult(
                ChatProcessingRoutes.HumanReview,
                semanticState,
                matches,
                observedMatches,
                policyDecision,
                renderings,
                null,
                null,
                review,
                "human_review_required",
                null);
        }

        string? requestedRoute = request.Policy?.ProcessingRoute;
        if (requestedRoute is not null &&
            requestedRoute is not ChatProcessingRoutes.GeneralTranslation and
                not ChatProcessingRoutes.SemanticRendering and
                not ChatProcessingRoutes.Hybrid and
                not ChatProcessingRoutes.HumanReview)
        {
            throw new ArgumentException($"Unsupported processing route '{requestedRoute}'.", nameof(request));
        }

        if (requestedRoute == ChatProcessingRoutes.GeneralTranslation ||
            (requestedRoute is null && matches.Length == 0))
        {
            GeneralTranslationResult translation = _generalTranslationProvider.Translate(
                new GeneralTranslationRequest(request.RawText, request.SourceLocale, request.TargetLocale));
            return new ChatProcessingResult(
                ChatProcessingRoutes.GeneralTranslation,
                semanticState,
                matches,
                observedMatches,
                policyDecision,
                renderings,
                translation,
                null,
                review,
                translation.IsAvailable ? "general_translation_completed" : "general_translation_unavailable",
                translation.Text);
        }

        if (requestedRoute == ChatProcessingRoutes.Hybrid ||
            (requestedRoute is null && RequiresHybridAggregation(request.RawText)))
        {
            // A protected hybrid aggregator is responsible for sending only its
            // marker-protected text to a general model.  Sending RawText here
            // first would expose a verified semantic fragment to the model and
            // allow it to replace the locked rendering.  The placeholder result
            // keeps the existing aggregation contract explicit for providers
            // that are unavailable or do not support protected hybrid work.
            GeneralTranslationResult translation = new(
                "unavailable",
                null,
                null,
                "hybrid_requires_locked_span_processing");
            HybridAggregationResult hybrid = _hybridAggregator.Aggregate(
                new HybridAggregationRequest(
                    request.RawText,
                    request.SourceLocale,
                    request.TargetLocale,
                    matches,
                    renderings,
                    translation,
                    request.AdditionalLockedSpans));
            return new ChatProcessingResult(
                ChatProcessingRoutes.Hybrid,
                semanticState,
                matches,
                observedMatches,
                policyDecision,
                renderings,
                translation,
                hybrid,
                review,
                hybrid.Status == "succeeded" ? "hybrid_completed" : "hybrid_unavailable",
                hybrid.Text);
        }

        string? output = renderings.Length == 1 ? renderings[0].Text : null;
        return new ChatProcessingResult(
            ChatProcessingRoutes.SemanticRendering,
            semanticState,
            matches,
            observedMatches,
            policyDecision,
            renderings,
            null,
            null,
            review,
            output is null ? "semantic_rendering_multiple_candidates" : "semantic_rendering_completed",
            output);
    }

    private static HumanReviewGateDecision EvaluateHumanReviewGate(
        string rawText,
        IReadOnlyList<SlangGraphSemanticMatch> matches,
        string? requestedRoute)
    {
        if (requestedRoute == ChatProcessingRoutes.HumanReview)
        {
            return new HumanReviewGateDecision(true, "policy_required_human_review");
        }

        // A reviewed caller policy already selected a non-human route. Keep the
        // runtime heuristic for unclassified live input, but do not override a
        // frozen evaluation/route contract merely because its text has "may".
        if (requestedRoute is not null)
        {
            return new HumanReviewGateDecision(false, null);
        }

        if (matches.Count == 0)
        {
            return new HumanReviewGateDecision(false, null);
        }

        bool uncertainty = UncertaintyRegex.IsMatch(rawText);
        bool contrast = ContrastRegex.IsMatch(rawText);
        if (uncertainty && contrast)
        {
            return new HumanReviewGateDecision(
                true,
                "semantic_fragment_with_contrast_and_uncertainty");
        }

        if (uncertainty)
        {
            return new HumanReviewGateDecision(
                true,
                "semantic_fragment_with_uncertainty");
        }

        return new HumanReviewGateDecision(false, null);
    }

    private static bool RequiresHybridAggregation(string rawText) =>
        CompositeMessageRegex.IsMatch(rawText);

    private static (SlangGraphSemanticMatch[] Matches, ChatSemanticPolicyDecision? Decision) ApplySemanticPolicy(
        IReadOnlyList<SlangGraphSemanticMatch> observedMatches,
        ChatSemanticPolicy? policy)
    {
        if (policy is null)
        {
            return (observedMatches.ToArray(), null);
        }

        string[] observedIds = observedMatches
            .Select(match => match.ConceptId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] expectedIds = policy.ConceptIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] forbiddenIds = policy.ForbiddenConceptIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var violations = new List<string>();
        bool suppress = false;

        switch (policy.Mode)
        {
            case "required":
                foreach (string conceptId in expectedIds)
                {
                    if (!observedIds.Contains(conceptId, StringComparer.OrdinalIgnoreCase))
                    {
                        violations.Add($"required_concept_missing:{conceptId}");
                    }
                }

                break;
            case "optional":
                foreach (string observedId in observedIds)
                {
                    if (!expectedIds.Contains(observedId, StringComparer.OrdinalIgnoreCase))
                    {
                        violations.Add($"optional_policy_unexpected_concept:{observedId}");
                    }
                }

                break;
            case "candidate_only":
                suppress = true;
                foreach (string observedId in observedIds)
                {
                    violations.Add($"candidate_only_formal_match_observed:{observedId}");
                }

                break;
            case "forbidden":
                suppress = true;
                foreach (string observedId in observedIds)
                {
                    if (forbiddenIds.Length == 0 || forbiddenIds.Contains(observedId, StringComparer.OrdinalIgnoreCase))
                    {
                        violations.Add($"forbidden_concept_observed:{observedId}");
                    }
                }

                break;
            default:
                violations.Add($"unsupported_semantic_policy_mode:{policy.Mode}");
                break;
        }

        return (
            suppress ? Array.Empty<SlangGraphSemanticMatch>() : observedMatches.ToArray(),
            new ChatSemanticPolicyDecision(
                policy.Mode,
                violations.Count == 0,
                suppress,
                violations));
    }
}
