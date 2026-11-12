using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LoLTranslator.App;

public sealed record ReviewedRuntimeTermResolution(string TermId, string Concept, string Rendering);

/// <summary>
/// Loads small, explicitly human-reviewed in-game terminology overrides. These
/// records improve the local runtime but remain separate from the formal Pack
/// until normal evidence/provenance release gates are satisfied.
/// </summary>
public sealed class ReviewedRuntimeTermResolver
{
    private sealed record RuntimeTermRecord(
        [property: JsonPropertyName("record_type")] string RecordType,
        [property: JsonPropertyName("term_id")] string TermId,
        [property: JsonPropertyName("source_locale")] string SourceLocale,
        [property: JsonPropertyName("surface_forms")] IReadOnlyList<string> SurfaceForms,
        [property: JsonPropertyName("concept")] string Concept,
        [property: JsonPropertyName("target_renderings")] IReadOnlyDictionary<string, string> TargetRenderings,
        [property: JsonPropertyName("review_status")] string ReviewStatus,
        [property: JsonPropertyName("formal_eligibility")] string FormalEligibility);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IReadOnlyList<RuntimeTermRecord> _records;

    private ReviewedRuntimeTermResolver(IReadOnlyList<RuntimeTermRecord> records) => _records = records;

    public static ReviewedRuntimeTermResolver Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Reviewed runtime term data was not found.", path);
        RuntimeTermRecord[] records = File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<RuntimeTermRecord>(line, JsonOptions)
                ?? throw new InvalidDataException("A reviewed runtime term record is invalid."))
            .Where(record => string.Equals(record.RecordType, "reviewed_runtime_term", StringComparison.Ordinal) &&
                             string.Equals(record.ReviewStatus, "approved", StringComparison.Ordinal) &&
                             string.Equals(record.FormalEligibility, "runtime_only", StringComparison.Ordinal))
            .ToArray();
        if (records.Length == 0) throw new InvalidDataException("No approved runtime terminology records were loaded.");
        return new ReviewedRuntimeTermResolver(records);
    }

    public bool TryResolveExact(string text, string sourceLocale, string targetLocale, out ReviewedRuntimeTermResolution? resolution)
    {
        string normalized = Normalize(text);
        foreach (RuntimeTermRecord record in _records)
        {
            if (!LocaleCompatible(sourceLocale, record.SourceLocale) ||
                !record.SurfaceForms.Any(surface => string.Equals(Normalize(surface), normalized, StringComparison.Ordinal))) continue;
            string? rendering = ResolveRendering(record.TargetRenderings, targetLocale);
            if (string.IsNullOrWhiteSpace(rendering)) continue;
            resolution = new ReviewedRuntimeTermResolution(record.TermId, record.Concept, rendering);
            return true;
        }
        resolution = null;
        return false;
    }

    private static string Normalize(string value) => Regex.Replace(
        value.Trim().Trim('.', '!', '?', '。', '！', '？', '(', ')', '（', '）'), @"\s+", " ", RegexOptions.CultureInvariant).ToLowerInvariant();

    private static bool LocaleCompatible(string actual, string expected) =>
        actual.StartsWith(expected.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private static string? ResolveRendering(IReadOnlyDictionary<string, string> renderings, string targetLocale)
    {
        if (renderings.TryGetValue(targetLocale, out string? exact)) return exact;
        string language = targetLocale.Split('-')[0];
        return renderings.FirstOrDefault(item => item.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
