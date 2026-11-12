using SlangGraph.Core;

namespace LoLTranslator.App;

/// <summary>Lane-local services: mutable legacy caches are never shared between workers or UI diagnostics.</summary>
public sealed class RuntimeTranslationWorkers
{
    private readonly Lazy<IncrementalGeneralChatShadowService> _deterministic;
    private readonly Lazy<IncrementalGeneralChatShadowService> _model;

    public RuntimeTranslationWorkers(OcrShadowSemanticService semantics)
    {
        _deterministic = new(() => IncrementalGeneralChatShadowService.CreateExplicit(semantics,
            provider: new ForbiddenModelProvider()));
        _model = new(() => IncrementalGeneralChatShadowService.CreateExplicit(semantics));
    }

    public Task<IncrementalGeneralChatShadowResult> Deterministic(RuntimeTranslationWork work, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (work.Messages.Any(message => message.EventKind is not (RuntimeChatEventKind.Ping or RuntimeChatEventKind.System)))
            throw new ArgumentException("Non-structured event in deterministic lane.");
        return Task.FromResult(_deterministic.Value.ProcessEvents(work.Messages, work.Context, work.TargetLocale));
    }

    public Task<IncrementalGeneralChatShadowResult> Model(RuntimeTranslationWork work, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (work.Messages.Any(message => message.EventKind != RuntimeChatEventKind.PlayerText))
            throw new ArgumentException("Non-player event in model lane.");
        // Existing provider calls are synchronous and have their own transport
        // timeout. Dispatcher cancellation suppresses publication, retaining the
        // slot until this call returns; it does not claim to stop GPU inference.
        return Task.FromResult(_model.Value.ProcessEvents(work.Messages, work.Context, work.TargetLocale));
    }

    private sealed class ForbiddenModelProvider : ILocalTranslationAttemptProvider
    {
        public GeneralTranslationResult Translate(GeneralTranslationRequest request) =>
            throw new InvalidOperationException("Model access is disabled in the deterministic lane.");
        public LocalTranslationAttempt TranslateWithDiagnostics(GeneralTranslationRequest request) =>
            throw new InvalidOperationException("Model access is disabled in the deterministic lane.");
    }
}
