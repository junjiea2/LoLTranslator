using System.Net.Http;

namespace LoLTranslator.App;

/// <summary>
/// Explicit local-only activation for controlled shadow tests.  It is disabled
/// by default and deliberately contains no alternate-model field: Qwen3 is a
/// candidate, while TranslateGemma remains an offline benchmark only.
/// </summary>
public sealed record ControlledShadowQwen3Profile(
    bool Enabled,
    string Activation,
    OllamaTranslationProfile Profile)
{
    public const string CandidateProfileId = "ollama.qwen3.8b.q4_k_m.controlled_shadow";
    public const string ExpectedDigest = "500a1f067a9f782620b40bee6f7b0c89e17ae61f686b92c24933e4ca4b2b8b41";

    public static ControlledShadowQwen3Profile Disabled { get; } = new(
        Enabled: false,
        Activation: "disabled_by_default",
        Profile: CreateQwenProfile());

    public static ControlledShadowQwen3Profile ExplicitTestOnly() => new(
        Enabled: true,
        Activation: "explicit_controlled_shadow_test_only",
        Profile: CreateQwenProfile());

    private static OllamaTranslationProfile CreateQwenProfile() => new(
        CandidateProfileId,
        "qwen3:8b",
        "qwen3-non-thinking-strict-zh-hans-v2",
        ExpectedDigest,
        DisableThinking: true,
        TemplateKind: "qwen3_non_thinking");
}

public sealed record ControlledShadowRuntimeDependencies(
    IGeneralTranslationProvider GeneralTranslationProvider,
    IHybridTranslationAggregator HybridTranslationAggregator,
    bool IsEnabled,
    string Activation);

/// <summary>
/// Creates runtime dependencies without changing the app's default.  Disabled
/// controlled shadow has exactly the same unavailable behavior as production's
/// current no-provider state.  An enabled candidate has no model fallback.
/// </summary>
public static class ControlledShadowQwen3RuntimeFactory
{
    public static ControlledShadowRuntimeDependencies Create(
        ControlledShadowQwen3Profile? profile = null,
        HttpClient? client = null)
    {
        ControlledShadowQwen3Profile resolved = profile ?? ControlledShadowQwen3Profile.Disabled;
        if (!resolved.Enabled)
        {
            return new ControlledShadowRuntimeDependencies(
                new UnavailableGeneralTranslationProvider(),
                new UnavailableHybridTranslationAggregator(),
                IsEnabled: false,
                resolved.Activation);
        }

        var provider = new OllamaLocalTranslationProvider(resolved.Profile, client);
        return new ControlledShadowRuntimeDependencies(
            provider,
            new ProtectedHybridTranslationAggregator(provider),
            IsEnabled: true,
            resolved.Activation);
    }
}
