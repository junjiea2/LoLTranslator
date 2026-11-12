using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>
/// Separate opt-in for a reviewed Domain Lexicon.  It is disabled by default,
/// and candidate aliases are observational only even when lexicon shadow is
/// explicitly enabled.
/// </summary>
public sealed record DomainLexiconShadowProfile(
    bool Enabled,
    bool IncludeCandidateShadow,
    string Activation)
{
    public static DomainLexiconShadowProfile Disabled { get; } = new(
        Enabled: false,
        IncludeCandidateShadow: false,
        Activation: "disabled_by_default");

    public static DomainLexiconShadowProfile ExplicitTestOnly(bool includeCandidateShadow = false) => new(
        Enabled: true,
        IncludeCandidateShadow: includeCandidateShadow,
        Activation: "explicit_lexicon_shadow_test_only");
}

public sealed record DomainLexiconShadowResult(
    ChatProcessingResult Processing,
    DomainLexiconMatchResult Lexicon,
    IReadOnlyList<HybridLockedSpan> LexiconLockedSpans,
    bool ForcedHybridForFormalLexicon);

/// <summary>
/// Explicitly scoped RC-only shadow activation. It must never be used as an
/// application default: unlike formal entries, its matches remain candidate
/// diagnostics even while a controlled-shadow test protects their spans.
/// </summary>
public sealed record DomainLexiconRcShadowProfile(bool Enabled, string Activation)
{
    public static DomainLexiconRcShadowProfile Disabled { get; } = new(false, "disabled_by_default");
    public static DomainLexiconRcShadowProfile ExplicitTestOnly() => new(true, "explicit_rc_shadow_test_only");
}

public sealed record DomainLexiconRcShadowResult(
    ChatProcessingResult Processing,
    IReadOnlyList<DomainLexiconEntityMatch> RcShadowMatches,
    IReadOnlyList<HybridLockedSpan> RcLockedSpans,
    bool ForcedHybridForRcShadow);

/// <summary>
/// Bridges a generic formal lexicon to the existing protected hybrid pipeline.
/// It never alters SlangGraph matching.  Only formal matches receive localized
/// rendering and a lock; candidate aliases remain shadow-only diagnostics.
/// </summary>
public sealed class DomainLexiconShadowProcessor
{
    private readonly DomainLexiconIndex _index;
    private readonly DomainLexiconShadowProfile _profile;
    private readonly ChatProcessingRouteOrchestrator _orchestrator;

    public DomainLexiconShadowProcessor(
        DomainLexiconIndex index,
        DomainLexiconShadowProfile profile,
        ChatProcessingRouteOrchestrator orchestrator)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    public DomainLexiconShadowResult Process(
        ChatProcessingRequest request,
        DomainLexiconMatchContext lexiconContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lexiconContext);
        if (!_profile.Enabled)
        {
            ChatProcessingResult processing = _orchestrator.Process(request);
            return new DomainLexiconShadowResult(
                processing,
                new DomainLexiconMatchResult(Array.Empty<DomainLexiconEntityMatch>(), Array.Empty<DomainLexiconEntityMatch>(), Array.Empty<string>()),
                Array.Empty<HybridLockedSpan>(),
                false);
        }

        DomainLexiconMatchResult lexicon = _index.Match(
            request.RawText,
            request.SourceLocale,
            lexiconContext,
            new DomainLexiconMatchOptions(_profile.IncludeCandidateShadow));
        HybridLockedSpan[] locks = lexicon.FormalMatches
            .Select((match, index) =>
            {
                DomainLexiconRenderedEntry rendering = _index.Render(match.CanonicalId, request.TargetLocale);
                return new HybridLockedSpan(match.Start, match.Length, $"[[DL_LOCKED_{index + 1}]]", rendering.Text);
            })
            .ToArray();
        bool forceHybrid = locks.Length > 0 &&
            request.Policy?.ProcessingRoute is null or ChatProcessingRoutes.GeneralTranslation;
        ChatProcessingPolicy? policy = forceHybrid
            ? new ChatProcessingPolicy(
                ChatProcessingRoutes.Hybrid,
                request.Policy?.SemanticPolicy ?? new ChatSemanticPolicy("optional", Array.Empty<string>(), Array.Empty<string>()))
            : request.Policy;
        ChatProcessingResult result = _orchestrator.Process(request with
        {
            Policy = policy,
            AdditionalLockedSpans = (request.AdditionalLockedSpans ?? Array.Empty<HybridLockedSpan>())
                .Concat(locks)
                .ToArray()
        });
        return new DomainLexiconShadowResult(result, lexicon, locks, forceHybrid);
    }
}

/// <summary>
/// A separate bridge for a deliberately chosen RC corpus. It starts from
/// candidate-shadow matches, never converts them to formal matches, and is
/// reachable only through an explicit test profile. Its protected spans are
/// diagnostic artifacts, not user-facing verified renderings.
/// </summary>
public sealed class DomainLexiconRcShadowProcessor
{
    private readonly DomainLexiconIndex _index;
    private readonly DomainLexiconRcShadowProfile _profile;
    private readonly ChatProcessingRouteOrchestrator _orchestrator;

    public DomainLexiconRcShadowProcessor(
        DomainLexiconIndex index,
        DomainLexiconRcShadowProfile profile,
        ChatProcessingRouteOrchestrator orchestrator)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    public DomainLexiconRcShadowResult Process(
        ChatProcessingRequest request,
        DomainLexiconMatchContext lexiconContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lexiconContext);
        if (!_profile.Enabled)
        {
            return new DomainLexiconRcShadowResult(
                _orchestrator.Process(request),
                Array.Empty<DomainLexiconEntityMatch>(),
                Array.Empty<HybridLockedSpan>(),
                false);
        }

        DomainLexiconMatchResult match = _index.Match(
            request.RawText,
            request.SourceLocale,
            lexiconContext,
            new DomainLexiconMatchOptions(IncludeCandidateShadow: true));
        DomainLexiconEntityMatch[] rcMatches = match.CandidateShadowMatches.ToArray();
        HybridLockedSpan[] locks = rcMatches.Select((item, index) =>
        {
            DomainLexiconRenderedEntry rendering = _index.RenderShadow(item.CanonicalId, request.TargetLocale);
            return new HybridLockedSpan(item.Start, item.Length, $"[[RC_LOCKED_{index + 1}]]", rendering.Text);
        }).ToArray();
        bool forceHybrid = locks.Length > 0 &&
            request.Policy?.ProcessingRoute is null or ChatProcessingRoutes.GeneralTranslation;
        ChatProcessingPolicy? policy = forceHybrid
            ? new ChatProcessingPolicy(
                ChatProcessingRoutes.Hybrid,
                request.Policy?.SemanticPolicy ?? new ChatSemanticPolicy("optional", Array.Empty<string>(), Array.Empty<string>()))
            : request.Policy;
        ChatProcessingResult result = _orchestrator.Process(request with
        {
            Policy = policy,
            AdditionalLockedSpans = (request.AdditionalLockedSpans ?? Array.Empty<HybridLockedSpan>())
                .Concat(locks)
                .ToArray()
        });
        return new DomainLexiconRcShadowResult(result, rcMatches, locks, forceHybrid);
    }
}
