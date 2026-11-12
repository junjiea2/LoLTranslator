using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace LoLTranslator.App;

/// <summary>
/// Read-only contract for a Data-governed, candidate-only typed normalization.
/// It is unavailable until an explicit shadow service loads this exact artifact.
/// </summary>
public sealed class RuntimeTypedNormalizationCandidate
{
    public const string ExpectedArtifactSha256 = "B4F760609768C1C5E1D53F8FD2A425AAFD2AD234EE90B247881F558B34301992";
    public const string ExpectedReviewSha256 = "42451E27262D8EBB38541774DDA3721261305CBD939D56558F62D62E4BC612ED";
    public const string ExpectedCandidateId = "normalization.real-screenshot-shadow-001.t0p-lane-top.001";
    public const string ExpectedSchema = "typed-entity-normalization-candidate/0.1";

    private RuntimeTypedNormalizationCandidate(
        string normalizationId,
        string exactSourceSurface,
        string targetEntity,
        string targetSlot)
    {
        NormalizationId = normalizationId;
        ExactSourceSurface = exactSourceSurface;
        TargetEntity = targetEntity;
        TargetSlot = targetSlot;
    }

    public string NormalizationId { get; }
    public string ExactSourceSurface { get; }
    public string TargetEntity { get; }
    public string TargetSlot { get; }

    public static RuntimeTypedNormalizationCandidate LoadExplicit(string artifactPath, string reviewPath)
    {
        VerifySha256(artifactPath, ExpectedArtifactSha256, "normalization artifact");
        VerifySha256(reviewPath, ExpectedReviewSha256, "normalization review source");
        JsonElement[] records = File.ReadLines(artifactPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        if (records.Length != 4) throw new InvalidDataException("Normalization artifact record count is invalid.");

        JsonElement header = Single(records, "normalization_candidate_header");
        if (Text(header, "schema_version") != ExpectedSchema ||
            Text(header, "candidate_id") != ExpectedCandidateId ||
            Text(header, "status") != "candidate_only" ||
            Text(header, "formal_eligibility") != "not_eligible" ||
            Bool(header, "runtime_enabled") || !Bool(header, "explicit_opt_in_required") ||
            !IsFalse(header, "lol_specific_regex") ||
            Text(header, "scope") != "diagnostic_shadow_only")
        {
            throw new InvalidDataException("Normalization header violates the explicit candidate-only contract.");
        }

        JsonElement normalization = Single(records, "typed_entity_normalization");
        if (Text(normalization, "normalization_id") != ExpectedCandidateId ||
            Text(normalization, "exact_source_surface") != "t0P" ||
            Text(normalization, "target_entity") != "game.lol:lane.top" ||
            Text(normalization, "target_slot") != "destination_area_lane" ||
            Text(normalization, "formal_eligibility") != "not_eligible" ||
            Bool(normalization, "runtime_enabled") ||
            Text(normalization, "locale_policy") != "use_runtime_context; do_not_infer_locale_from_surface_alone" ||
            !ArrayEquals(normalization, "context_scope", "game.lol", "in_game_chat"))
        {
            throw new InvalidDataException("Normalization target, scope, or eligibility is invalid.");
        }

        JsonElement fixture = Single(records, "expected_fixture");
        JsonElement loader = Single(records, "loader_contract");
        if (Text(fixture, "normalization_id") is { } fixtureId && fixtureId != ExpectedCandidateId ||
            Text(fixture, "formal_eligibility") != "not_eligible" ||
            Text(loader, "candidate_id") != ExpectedCandidateId ||
            Text(loader, "load_mode") != "explicit_opt_in_shadow_only" ||
            Text(loader, "default_behavior") != "disabled" ||
            Text(loader, "formal_pack_co_load") != "prohibited")
        {
            throw new InvalidDataException("Normalization fixture or loader contract is invalid.");
        }

        ValidateReview(reviewPath);
        return new RuntimeTypedNormalizationCandidate(
            Text(normalization, "normalization_id")!,
            Text(normalization, "exact_source_surface")!,
            Text(normalization, "target_entity")!,
            Text(normalization, "target_slot")!);
    }

    public RuntimeTypedNormalizationResult NormalizeExact(
        string messageBody,
        ShadowMatchContextOption context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(messageBody))
        {
            return RuntimeTypedNormalizationResult.NoMatch(messageBody, "normalization_empty_message_body");
        }
        if (!IsCompatibleContext(context))
        {
            return RuntimeTypedNormalizationResult.NoMatch(messageBody, "normalization_context_incompatible");
        }

        int start = FindStandaloneExactSurface(messageBody, ExactSourceSurface);
        if (start < 0)
        {
            if (ContainsNearStandaloneSurface(messageBody, ExactSourceSurface))
            {
                return RuntimeTypedNormalizationResult.NoMatch(messageBody, "normalization_approximate_surface_requires_review");
            }
            return RuntimeTypedNormalizationResult.NoMatch(messageBody, "normalization_exact_surface_not_found");
        }

        // Canonical English is an internal model input, not a rendering and not
        // a Pack alias. The generic model still translates it for the target OS locale.
        const string canonicalModelText = "top lane";
        string normalized = messageBody.Remove(start, ExactSourceSurface.Length).Insert(start, canonicalModelText);
        return new RuntimeTypedNormalizationResult(
            true,
            normalized,
            new RuntimeTypedEntityNormalization(NormalizationId, ExactSourceSurface, TargetEntity, TargetSlot, start, ExactSourceSurface.Length),
            null);
    }

    private static bool IsCompatibleContext(ShadowMatchContextOption context) =>
        string.Equals(context.Channel, "in_game_chat", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(context.Community) &&
        context.Community.StartsWith("lol.", StringComparison.OrdinalIgnoreCase);

    private static int FindStandaloneExactSurface(string text, string surface)
    {
        int start = 0;
        while ((start = text.IndexOf(surface, start, StringComparison.Ordinal)) >= 0)
        {
            int end = start + surface.Length;
            bool leftBoundary = start == 0 || !IsIdentifierCharacter(text[start - 1]);
            bool rightBoundary = end == text.Length || !IsIdentifierCharacter(text[end]);
            if (leftBoundary && rightBoundary) return start;
            start = end;
        }
        return -1;
    }

    private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character is '_' or '-';

    // A candidate may only normalize its exact surface.  This deliberately does
    // not choose a near spelling; it merely stops an opt-in shadow translation
    // from treating a one-character OCR-like variant as ordinary safe input.
    private static bool ContainsNearStandaloneSurface(string text, string surface)
    {
        int tokenStart = 0;
        while (tokenStart < text.Length)
        {
            while (tokenStart < text.Length && !IsIdentifierCharacter(text[tokenStart])) tokenStart++;
            if (tokenStart == text.Length) break;
            int tokenEnd = tokenStart;
            while (tokenEnd < text.Length && IsIdentifierCharacter(text[tokenEnd])) tokenEnd++;
            string token = text[tokenStart..tokenEnd];
            tokenStart = tokenEnd;
            if (token.Length != surface.Length || string.Equals(token, surface, StringComparison.Ordinal)) continue;
            int differences = 0;
            for (int index = 0; index < token.Length && differences < 2; index++)
            {
                if (token[index] != surface[index]) differences++;
            }
            if (differences == 1) return true;
        }
        return false;
    }

    private static void VerifySha256(string path, string expected, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Missing {name}.", path);
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{name} SHA-256 mismatch.");
    }

    private static void ValidateReview(string reviewPath)
    {
        JsonElement[] records = File.ReadLines(reviewPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        if (records.Length < 2 || Text(records[0], "schema_version") != "shadow-human-review-normalization/0.1" ||
            records.Skip(1).All(record => Text(record, "review_id") != "rss-005-review-001.normalized.001") ||
            records.Skip(1).Any(record => Text(record, "formal_eligibility") != "not_eligible"))
        {
            throw new InvalidDataException("Normalization review source violates the not-eligible contract.");
        }
    }

    private static JsonElement Single(IEnumerable<JsonElement> records, string recordType) =>
        records.Single(record => Text(record, "record_type") == recordType);
    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    private static bool IsFalse(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.False;
    private static bool ArrayEquals(JsonElement element, string property, params string[] expected) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array &&
        value.EnumerateArray().Select(item => item.GetString()).SequenceEqual(expected, StringComparer.Ordinal);
}

public sealed record RuntimeTypedEntityNormalization(
    string NormalizationId,
    string SourceSurface,
    string TargetEntity,
    string TargetSlot,
    int Start,
    int Length);

public sealed record RuntimeTypedNormalizationResult(
    bool Applied,
    string NormalizedText,
    RuntimeTypedEntityNormalization? Entity,
    string? FailureReason)
{
    public static RuntimeTypedNormalizationResult NoMatch(string original, string reason) => new(false, original, null, reason);
}
