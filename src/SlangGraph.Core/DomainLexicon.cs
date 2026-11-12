using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlangGraph.Core;

/// <summary>
/// A domain-neutral, separately governed entity/concept lexicon.  It is not a
/// SlangGraph Pack and does not change Pack verification or release gating.
/// Callers must opt in to a loaded lexicon explicitly.
/// </summary>
public sealed record DomainLexiconHeader(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("lexicon_id")] string LexiconId,
    [property: JsonPropertyName("lexicon_version")] string LexiconVersion,
    [property: JsonPropertyName("domain_id")] string DomainId,
    [property: JsonPropertyName("status")] string Status);

public sealed record DomainLexiconContextScope(
    [property: JsonPropertyName("domain")] string? Domain = null,
    [property: JsonPropertyName("community")] string? Community = null,
    [property: JsonPropertyName("channel")] string? Channel = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null);

public sealed record DomainLexiconPatchValidity(
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("to")] string? To = null);

public sealed record DomainLexiconAmbiguity(
    [property: JsonPropertyName("group")] string? Group = null,
    [property: JsonPropertyName("requires_context")] bool RequiresContext = false,
    [property: JsonPropertyName("allowed_types")] IReadOnlyList<string>? AllowedTypes = null,
    [property: JsonPropertyName("requires_review")] bool RequiresReview = false);

public sealed record DomainLexiconAlias(
    [property: JsonPropertyName("alias_id")] string AliasId,
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("normalized")] string Normalized,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("register")] string Register,
    [property: JsonPropertyName("context_scopes")] IReadOnlyList<DomainLexiconContextScope> ContextScopes,
    [property: JsonPropertyName("ambiguity")] DomainLexiconAmbiguity? Ambiguity,
    [property: JsonPropertyName("patch_validity")] DomainLexiconPatchValidity? PatchValidity,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("review_status")] string ReviewStatus,
    [property: JsonPropertyName("formal_eligibility")] string FormalEligibility);

public sealed record DomainLexiconRendering(
    [property: JsonPropertyName("target_locale")] string TargetLocale,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("review_status")] string ReviewStatus,
    [property: JsonPropertyName("formal_eligibility")] string FormalEligibility);

public sealed record DomainLexiconEntry(
    [property: JsonPropertyName("canonical_id")] string CanonicalId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("canonical_names")] IReadOnlyDictionary<string, string> CanonicalNames,
    [property: JsonPropertyName("aliases")] IReadOnlyList<DomainLexiconAlias> Aliases,
    [property: JsonPropertyName("context_scopes")] IReadOnlyList<DomainLexiconContextScope> ContextScopes,
    [property: JsonPropertyName("ambiguity")] DomainLexiconAmbiguity? Ambiguity,
    [property: JsonPropertyName("patch_validity")] DomainLexiconPatchValidity? PatchValidity,
    [property: JsonPropertyName("localized_renderings")] IReadOnlyList<DomainLexiconRendering> LocalizedRenderings,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("review_status")] string ReviewStatus,
    [property: JsonPropertyName("formal_eligibility")] string FormalEligibility);

public sealed record DomainLexicon(
    DomainLexiconHeader Header,
    IReadOnlyList<DomainLexiconEntry> Entries);

public sealed record DomainLexiconMatchContext(
    string? Domain,
    string? Community,
    string? Channel,
    string? Patch,
    IReadOnlyList<string>? Tags = null);

public sealed record DomainLexiconMatchOptions(bool IncludeCandidateShadow = false);

public sealed record DomainLexiconEntityMatch(
    string CanonicalId,
    string Kind,
    string Type,
    string AliasId,
    string AliasSurface,
    string MatchedText,
    int Start,
    int Length,
    string Locale,
    string Register,
    bool IsFormal,
    bool IsAmbiguous,
    string? AmbiguityReason)
{
    public SlangGraphSlotValue ToSlotValue() => new(
        CanonicalId,
        "entity_ref",
        MatchedText,
        "domain_lexicon",
        CanonicalId);
}

public sealed record DomainLexiconMatchResult(
    IReadOnlyList<DomainLexiconEntityMatch> FormalMatches,
    IReadOnlyList<DomainLexiconEntityMatch> CandidateShadowMatches,
    IReadOnlyList<string> AmbiguityReasons);

public sealed record DomainLexiconRenderedEntry(
    string CanonicalId,
    string RequestedLocale,
    string ResolvedLocale,
    string Text,
    bool UsedFallback);

/// <summary>
/// Read-only diagnostics from a candidate sidecar. Invalid source rows are
/// retained as issues rather than repaired, so a frozen candidate remains
/// reproducible and cannot silently become runtime data.
/// </summary>
public sealed record DomainLexiconLoadIssue(
    int LineNumber,
    string? RecordType,
    string Code,
    string Detail);

public sealed record DomainLexiconDeclaredAmbiguity(
    string CanonicalId,
    string? Surface,
    string? Requirement);

public sealed record DomainLexiconCandidateLoadResult(
    DomainLexicon? Lexicon,
    int TotalLines,
    int LoadedEntries,
    IReadOnlyList<DomainLexiconLoadIssue> Issues,
    IReadOnlyDictionary<string, int> UnknownFields,
    IReadOnlyDictionary<string, int> MissingFields,
    IReadOnlyList<DomainLexiconDeclaredAmbiguity> DeclaredAmbiguities);

public static class DomainLexiconLoader
{
    public static DomainLexicon LoadJsonl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        DomainLexiconHeader? header = null;
        var entries = new List<DomainLexiconEntry>();

        foreach (string line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            string recordType = document.RootElement.GetProperty("record_type").GetString()
                ?? throw new InvalidDataException("Domain Lexicon record_type is required.");
            switch (recordType)
            {
                case "domain_lexicon":
                    header = JsonSerializer.Deserialize<DomainLexiconHeader>(line, options)
                        ?? throw new InvalidDataException("Invalid Domain Lexicon header.");
                    break;
                case "lexicon_entry":
                    entries.Add(HasDirectScopeContract(document.RootElement)
                        ? ParseDirectContractEntry(document.RootElement)
                        : JsonSerializer.Deserialize<DomainLexiconEntry>(line, options)
                          ?? throw new InvalidDataException("Invalid Domain Lexicon entry."));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Domain Lexicon record_type '{recordType}'.");
            }
        }

        if (header is null || entries.Count == 0)
        {
            throw new InvalidDataException("A Domain Lexicon requires one header and at least one entry.");
        }

        if (entries.GroupBy(entry => entry.CanonicalId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Domain Lexicon canonical IDs must be unique.");
        }

        return new DomainLexicon(header, entries);
    }

    private static bool HasDirectScopeContract(JsonElement root) =>
        root.TryGetProperty("scope", out _) || root.TryGetProperty("scope_tags", out _);

    /// <summary>
    /// Reads the published domain-neutral JSONL contract directly. This is
    /// intentionally separate from the legacy candidate-sidecar adapter.
    /// </summary>
    private static DomainLexiconEntry ParseDirectContractEntry(JsonElement root)
    {
        string canonicalId = RequiredString(root, "canonical_id");
        string kind = RequiredString(root, "kind");
        string type = RequiredString(root, "type");
        IReadOnlyDictionary<string, string> names = StringMap(root, "canonical_names");
        if (names.Count == 0) throw new InvalidDataException("canonical_names is required.");
        IReadOnlyList<DomainLexiconContextScope> scopes = DirectScopes(root);
        DomainLexiconPatchValidity? patch = DirectPatch(root);
        string status = RequiredString(root, "status");
        string reviewStatus = RequiredString(root, "review_status");
        string eligibility = RequiredString(root, "formal_eligibility");
        if (!root.TryGetProperty("aliases", out JsonElement aliasesElement) || aliasesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("aliases is required.");
        }

        var aliases = new List<DomainLexiconAlias>();
        foreach (JsonElement alias in aliasesElement.EnumerateArray())
        {
            if (alias.ValueKind != JsonValueKind.Object) throw new InvalidDataException("alias must be an object.");
            string aliasStatus = RequiredString(alias, "status");
            string aliasReview = RequiredString(alias, "review_status");
            string aliasEligibility = RequiredString(alias, "formal_eligibility");
            aliases.Add(new DomainLexiconAlias(
                RequiredString(alias, "alias_id"),
                RequiredString(alias, "surface"),
                GetString(alias, "normalized") ?? RequiredString(alias, "surface"),
                RequiredString(alias, "locale"),
                RequiredString(alias, "register"),
                DirectScopes(alias, scopes),
                DirectAliasAmbiguity(alias),
                DirectPatch(alias) ?? patch,
                aliasStatus,
                aliasReview,
                aliasEligibility));
        }

        var renderings = new List<DomainLexiconRendering>();
        if (!root.TryGetProperty("localized_renderings", out JsonElement renderingsElement) || renderingsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("localized_renderings is required.");
        }

        foreach (JsonElement rendering in renderingsElement.EnumerateArray())
        {
            if (rendering.ValueKind != JsonValueKind.Object) throw new InvalidDataException("localized rendering must be an object.");
            renderings.Add(new DomainLexiconRendering(
                RequiredString(rendering, "target_locale"),
                RequiredString(rendering, "text"),
                RequiredString(rendering, "status"),
                RequiredString(rendering, "review_status"),
                RequiredString(rendering, "formal_eligibility")));
        }

        return new DomainLexiconEntry(
            canonicalId, kind, type, names, aliases, scopes, null, patch, renderings, status, reviewStatus, eligibility);
    }

    private static IReadOnlyList<DomainLexiconContextScope> DirectScopes(JsonElement root, IReadOnlyList<DomainLexiconContextScope>? fallback = null)
    {
        JsonElement tagsElement;
        if (!root.TryGetProperty("scope_tags", out tagsElement) && !root.TryGetProperty("scope", out tagsElement))
        {
            return fallback ?? Array.Empty<DomainLexiconContextScope>();
        }

        if (tagsElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("scope/scope_tags must be an array.");
        string[] tags = tagsElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
        return tags.Length == 0 ? Array.Empty<DomainLexiconContextScope>() : new[] { new DomainLexiconContextScope(Tags: tags) };
    }

    private static DomainLexiconPatchValidity? DirectPatch(JsonElement root)
    {
        if (!root.TryGetProperty("patch_validity", out JsonElement patch) || patch.ValueKind != JsonValueKind.Object) return null;
        string? from = GetString(patch, "from");
        string? to = GetString(patch, "to");
        return from is null && to is null ? null : new DomainLexiconPatchValidity(from, to);
    }

    private static DomainLexiconAmbiguity? DirectAliasAmbiguity(JsonElement alias)
    {
        bool requiresReview = (alias.TryGetProperty("conflict_ids", out JsonElement conflicts) && conflicts.ValueKind == JsonValueKind.Array && conflicts.GetArrayLength() > 0) ||
                              !string.Equals(GetString(alias, "conflict_status") ?? "none", "none", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(GetString(alias, "match_policy"), "fail_closed", StringComparison.OrdinalIgnoreCase);
        return requiresReview ? new DomainLexiconAmbiguity("declared_conflict", RequiresReview: true) : null;
    }

    /// <summary>
    /// Adapts a candidate sidecar to the domain-neutral contract for
    /// diagnostics only. It accepts no formal promotion and deliberately does
    /// not write, normalize, or otherwise repair its input document.
    /// </summary>
    public static DomainLexiconCandidateLoadResult LoadCandidateSidecarJsonl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var issues = new List<DomainLexiconLoadIssue>();
        var unknownFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missingFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var declaredAmbiguities = new List<DomainLexiconDeclaredAmbiguity>();
        var entries = new List<DomainLexiconEntry>();
        DomainLexiconHeader? header = null;
        int lineNumber = 0;

        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                issues.Add(new DomainLexiconLoadIssue(lineNumber, null, "invalid_json", exception.Message));
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                string? recordType = GetString(root, "record_type");
                if (recordType is null)
                {
                    issues.Add(new DomainLexiconLoadIssue(lineNumber, null, "missing_record_type", "record_type is required."));
                    continue;
                }

                if (recordType == "domain_lexicon_header")
                {
                    TrackUnknownFields(root, HeaderFields, unknownFields);
                    TrackMissingFields(root, RequiredHeaderFields, missingFields);
                    string? packId = GetString(root, "pack_id");
                    string? version = GetString(root, "pack_version");
                    string? schema = GetString(root, "schema_version");
                    if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(schema))
                    {
                        issues.Add(new DomainLexiconLoadIssue(lineNumber, recordType, "missing_header_field", "schema_version, pack_id, and pack_version are required."));
                        continue;
                    }

                    if (header is not null)
                    {
                        issues.Add(new DomainLexiconLoadIssue(lineNumber, recordType, "duplicate_header", "Only one candidate sidecar header is allowed."));
                        continue;
                    }

                    header = new DomainLexiconHeader(schema, packId, version, "candidate_sidecar", "candidate");
                    continue;
                }

                if (recordType != "lexicon_entry")
                {
                    issues.Add(new DomainLexiconLoadIssue(lineNumber, recordType, "unsupported_record_type", "Candidate sidecar supports domain_lexicon_header and lexicon_entry."));
                    continue;
                }

                TrackUnknownFields(root, EntryFields, unknownFields);
                TrackMissingFields(root, RequiredEntryFields, missingFields);
                try
                {
                    DomainLexiconEntry entry = AdaptCandidateEntry(root, lineNumber, issues);
                    entries.Add(entry);
                    declaredAmbiguities.AddRange(CandidateDeclaredAmbiguities(root, entry.CanonicalId));
                }
                catch (InvalidDataException exception)
                {
                    issues.Add(new DomainLexiconLoadIssue(lineNumber, recordType, "invalid_entry", exception.Message));
                }
            }
        }

        if (header is null)
        {
            issues.Add(new DomainLexiconLoadIssue(0, "domain_lexicon_header", "missing_header", "No valid candidate sidecar header was loaded."));
        }

        foreach (IGrouping<string, DomainLexiconEntry> duplicate in entries.GroupBy(entry => entry.CanonicalId, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            issues.Add(new DomainLexiconLoadIssue(0, "lexicon_entry", "duplicate_canonical_id", duplicate.Key));
        }

        DomainLexicon? lexicon = header is not null && entries.Count > 0 &&
            !entries.GroupBy(entry => entry.CanonicalId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            ? new DomainLexicon(header, entries)
            : null;
        return new DomainLexiconCandidateLoadResult(lexicon, lineNumber, entries.Count, issues, unknownFields, missingFields, declaredAmbiguities);
    }

    private static DomainLexiconEntry AdaptCandidateEntry(
        JsonElement root,
        int lineNumber,
        ICollection<DomainLexiconLoadIssue> issues)
    {
        string canonicalId = RequiredString(root, "canonical_id");
        string kind = RequiredString(root, "entry_kind");
        string type = RequiredString(root, "entity_type");
        IReadOnlyDictionary<string, string> canonicalNames = StringMap(root, "canonical_names");
        if (canonicalNames.Count == 0) throw new InvalidDataException("canonical_names is required.");
        string formalEligibility = GetString(root, "formal_eligibility") ?? "unknown";
        string reviewStatus = GetString(root, "review_status") ?? "unknown";
        string status = GetString(root, "status") ??
            (string.Equals(formalEligibility, "formal_eligible", StringComparison.OrdinalIgnoreCase) ? "unknown" : "candidate");
        IReadOnlyList<DomainLexiconContextScope> scopes = CandidateScopes(root);
        DomainLexiconPatchValidity? patch = CandidatePatchValidity(root, lineNumber, issues);
        DomainLexiconAmbiguity? ambiguity = CandidateAmbiguity(root);
        var aliases = new List<DomainLexiconAlias>();
        if (!root.TryGetProperty("aliases", out JsonElement aliasArray) || aliasArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("aliases is required.");
        }

        int aliasIndex = 0;
        foreach (JsonElement alias in aliasArray.EnumerateArray())
        {
            aliasIndex++;
            if (alias.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new DomainLexiconLoadIssue(lineNumber, "lexicon_entry", "invalid_alias", $"{canonicalId} alias {aliasIndex} is not an object."));
                continue;
            }

            string surface = RequiredString(alias, "surface");
            string normalized = GetString(alias, "normalized") ?? surface;
            string aliasStatus = GetString(alias, "status") ?? status;
            aliases.Add(new DomainLexiconAlias(
                $"{canonicalId}#alias-{aliasIndex}",
                surface,
                normalized,
                GetString(alias, "locale") ?? "und",
                GetString(alias, "register") ?? GetString(root, "register") ?? "unknown",
                scopes,
                ambiguity,
                patch,
                aliasStatus,
                reviewStatus,
                formalEligibility));
        }

        if (aliases.Count == 0) throw new InvalidDataException("No valid aliases were available.");
        var renderings = new List<DomainLexiconRendering>();
        if (root.TryGetProperty("localized_renderings", out JsonElement renderingArray) && renderingArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement rendering in renderingArray.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                string? targetLocale = GetString(rendering, "target_locale");
                string? text = GetString(rendering, "text") ?? GetString(rendering, "short_text");
                if (string.IsNullOrWhiteSpace(targetLocale) || string.IsNullOrWhiteSpace(text))
                {
                    issues.Add(new DomainLexiconLoadIssue(lineNumber, "lexicon_entry", "invalid_rendering", $"{canonicalId} rendering requires target_locale and text/short_text."));
                    continue;
                }

                renderings.Add(new DomainLexiconRendering(targetLocale, text, GetString(rendering, "status") ?? status, reviewStatus, formalEligibility));
            }
        }

        return new DomainLexiconEntry(canonicalId, kind, type, canonicalNames, aliases, scopes, ambiguity, patch, renderings, status, reviewStatus, formalEligibility);
    }

    private static DomainLexiconPatchValidity? CandidatePatchValidity(JsonElement root, int lineNumber, ICollection<DomainLexiconLoadIssue> issues)
    {
        if (!root.TryGetProperty("patch_validity", out JsonElement patch) || patch.ValueKind != JsonValueKind.Object) return null;
        string? from = GetString(patch, "from");
        string? to = GetString(patch, "to");
        string? sourcePatch = GetString(patch, "source_patch");
        if (from is null && to is null && sourcePatch is not null)
        {
            issues.Add(new DomainLexiconLoadIssue(lineNumber, "lexicon_entry", "patch_source_exact_shadow", "source_patch was adapted as an exact candidate-shadow patch only."));
            from = sourcePatch;
            to = sourcePatch;
        }

        return from is null && to is null ? null : new DomainLexiconPatchValidity(from, to);
    }

    private static IReadOnlyList<DomainLexiconContextScope> CandidateScopes(JsonElement root)
    {
        if (!root.TryGetProperty("context_scope", out JsonElement scopes) || scopes.ValueKind != JsonValueKind.Array) return Array.Empty<DomainLexiconContextScope>();
        string[] tags = scopes.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
        return tags.Length == 0 ? Array.Empty<DomainLexiconContextScope>() : new[] { new DomainLexiconContextScope(Tags: tags) };
    }

    private static DomainLexiconAmbiguity? CandidateAmbiguity(JsonElement root) =>
        root.TryGetProperty("ambiguity", out JsonElement ambiguity) && ambiguity.ValueKind == JsonValueKind.Array && ambiguity.GetArrayLength() > 0
            ? new DomainLexiconAmbiguity("candidate_declared", true)
            : null;

    private static IEnumerable<DomainLexiconDeclaredAmbiguity> CandidateDeclaredAmbiguities(JsonElement root, string canonicalId)
    {
        if (!root.TryGetProperty("ambiguity", out JsonElement ambiguity) || ambiguity.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement item in ambiguity.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            yield return new DomainLexiconDeclaredAmbiguity(
                canonicalId,
                GetString(item, "surface"),
                GetString(item, "context_requirement"));
        }
    }

    private static IReadOnlyDictionary<string, string> StringMap(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement map) || map.ValueKind != JsonValueKind.Object) return new Dictionary<string, string>();
        return map.EnumerateObject()
            .Where(item => item.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static string RequiredString(JsonElement root, string property) =>
        GetString(root, property) ?? throw new InvalidDataException($"{property} is required.");

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void TrackUnknownFields(JsonElement root, ISet<string> knownFields, IDictionary<string, int> unknownFields)
    {
        foreach (JsonProperty property in root.EnumerateObject().Where(property => !knownFields.Contains(property.Name)))
        {
            unknownFields[property.Name] = unknownFields.TryGetValue(property.Name, out int count) ? count + 1 : 1;
        }
    }

    private static void TrackMissingFields(JsonElement root, IEnumerable<string> requiredFields, IDictionary<string, int> missingFields)
    {
        foreach (string field in requiredFields.Where(field => !root.TryGetProperty(field, out _)))
        {
            missingFields[field] = missingFields.TryGetValue(field, out int count) ? count + 1 : 1;
        }
    }

    private static readonly HashSet<string> HeaderFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "record_type", "schema_version", "pack_id", "pack_version", "runtime_loadable", "formal_eligibility", "note"
    };

    private static readonly HashSet<string> EntryFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "record_type", "schema_version", "canonical_id", "entry_kind", "entity_type", "canonical_names", "source_version", "source_ids", "aliases", "locale", "register", "context_scope", "ambiguity", "ocr_variants", "localized_renderings", "patch_validity", "provenance", "review_status", "formal_eligibility", "notes", "status"
    };

    private static readonly string[] RequiredHeaderFields = { "schema_version", "pack_id", "pack_version", "formal_eligibility" };
    private static readonly string[] RequiredEntryFields = { "canonical_id", "entry_kind", "entity_type", "canonical_names", "aliases", "context_scope", "localized_renderings", "patch_validity", "review_status", "formal_eligibility" };
}

/// <summary>
/// Read-only longest-safe-alias index.  Candidate records can be reported in
/// explicit shadow mode but are never returned as formal matches or renderings.
/// </summary>
public sealed class DomainLexiconIndex
{
    private readonly DomainLexicon _lexicon;
    private readonly IReadOnlyDictionary<string, DomainLexiconEntry> _entries;

    public DomainLexiconIndex(DomainLexicon lexicon)
    {
        _lexicon = lexicon ?? throw new ArgumentNullException(nameof(lexicon));
        _entries = lexicon.Entries.ToDictionary(entry => entry.CanonicalId, StringComparer.OrdinalIgnoreCase);
    }

    public DomainLexiconMatchResult Match(
        string text,
        string locale,
        DomainLexiconMatchContext context,
        DomainLexiconMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        options ??= new DomainLexiconMatchOptions();
        string normalized = DomainLexiconTextNormalizer.Normalize(text);
        var candidates = new List<(DomainLexiconEntry Entry, DomainLexiconAlias Alias, int Start, int Length, string Text, bool Formal)>();

        foreach (DomainLexiconEntry entry in _lexicon.Entries)
        {
            if (!ScopeAllows(entry.ContextScopes, context) || !PatchAllows(entry.PatchValidity, context.Patch))
            {
                continue;
            }

            foreach (DomainLexiconAlias alias in entry.Aliases)
            {
                if (!LocaleAllows(alias.Locale, locale) ||
                    !ScopeAllows(alias.ContextScopes, context) ||
                    !PatchAllows(alias.PatchValidity, context.Patch))
                {
                    continue;
                }

                bool formal = IsFormal(entry.Status, entry.ReviewStatus, entry.FormalEligibility) &&
                              IsFormal(alias.Status, alias.ReviewStatus, alias.FormalEligibility);
                if (!formal && !options.IncludeCandidateShadow)
                {
                    continue;
                }

                string phrase = DomainLexiconTextNormalizer.Normalize(string.IsNullOrWhiteSpace(alias.Normalized) ? alias.Surface : alias.Normalized);
                foreach (int start in FindSafePhraseOffsets(normalized, phrase))
                {
                    candidates.Add((entry, alias, start, phrase.Length, normalized.Substring(start, phrase.Length), formal));
                }
            }
        }

        var formalMatches = new List<DomainLexiconEntityMatch>();
        var shadowMatches = new List<DomainLexiconEntityMatch>();
        var reasons = new List<string>();
        int coveredUntil = -1;
        foreach (IGrouping<int, (DomainLexiconEntry Entry, DomainLexiconAlias Alias, int Start, int Length, string Text, bool Formal)> group in candidates
                     .OrderByDescending(candidate => candidate.Length)
                     .ThenBy(candidate => candidate.Start)
                     .GroupBy(candidate => candidate.Start)
                     .OrderBy(group => group.Key))
        {
            if (group.Key < coveredUntil)
            {
                continue;
            }

            int longest = group.Max(candidate => candidate.Length);
            var longestCandidates = group.Where(candidate => candidate.Length == longest)
                .GroupBy(candidate => candidate.Entry.CanonicalId, StringComparer.OrdinalIgnoreCase)
                .Select(candidatesById => candidatesById.OrderByDescending(candidate => candidate.Formal).First())
                .ToArray();
            bool ambiguous = IsAmbiguous(longestCandidates, context, out string? ambiguityReason);
            if (ambiguous)
            {
                reasons.Add(ambiguityReason!);
                coveredUntil = group.Key + longest;
                continue;
            }

            foreach (var candidate in longestCandidates)
            {
                var match = new DomainLexiconEntityMatch(
                    candidate.Entry.CanonicalId,
                    candidate.Entry.Kind,
                    candidate.Entry.Type,
                    candidate.Alias.AliasId,
                    candidate.Alias.Surface,
                    candidate.Text,
                    candidate.Start,
                    candidate.Length,
                    locale,
                    candidate.Alias.Register,
                    candidate.Formal,
                    false,
                    null);
                (candidate.Formal ? formalMatches : shadowMatches).Add(match);
            }

            coveredUntil = group.Key + longest;
        }

        return new DomainLexiconMatchResult(formalMatches, shadowMatches, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    public DomainLexiconRenderedEntry Render(string canonicalId, string targetLocale)
    {
        if (!_entries.TryGetValue(canonicalId, out DomainLexiconEntry? entry))
        {
            throw new KeyNotFoundException($"Unknown Domain Lexicon entry '{canonicalId}'.");
        }

        DomainLexiconRendering[] eligible = entry.LocalizedRenderings
            .Where(rendering => IsFormal(rendering.Status, rendering.ReviewStatus, rendering.FormalEligibility))
            .ToArray();
        DomainLexiconRendering? selected = eligible.FirstOrDefault(rendering =>
            string.Equals(rendering.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase));
        selected ??= eligible.FirstOrDefault(rendering => SameLanguage(rendering.TargetLocale, targetLocale));
        selected ??= eligible.FirstOrDefault(rendering =>
            string.Equals(rendering.TargetLocale, "en-US", StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            return new DomainLexiconRenderedEntry(
                canonicalId,
                targetLocale,
                selected.TargetLocale,
                selected.Text,
                !string.Equals(selected.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase));
        }

        string fallback = entry.CanonicalNames.TryGetValue(targetLocale, out string? exact)
            ? exact
            : entry.CanonicalNames.FirstOrDefault(pair => SameLanguage(pair.Key, targetLocale)).Value
              ?? entry.CanonicalNames.GetValueOrDefault("en-US")
              ?? canonicalId;
        return new DomainLexiconRenderedEntry(canonicalId, targetLocale, "und", fallback, true);
    }

    /// <summary>
    /// Candidate-only rendering for a diagnostic log. It never participates in
    /// formal output or protected spans.
    /// </summary>
    public DomainLexiconRenderedEntry RenderShadow(string canonicalId, string targetLocale)
    {
        if (!_entries.TryGetValue(canonicalId, out DomainLexiconEntry? entry))
        {
            throw new KeyNotFoundException($"Unknown Domain Lexicon entry '{canonicalId}'.");
        }

        DomainLexiconRendering? selected = entry.LocalizedRenderings.FirstOrDefault(rendering =>
            string.Equals(rendering.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase));
        selected ??= entry.LocalizedRenderings.FirstOrDefault(rendering => SameLanguage(rendering.TargetLocale, targetLocale));
        selected ??= entry.LocalizedRenderings.FirstOrDefault(rendering =>
            string.Equals(rendering.TargetLocale, "en-US", StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            return new DomainLexiconRenderedEntry(canonicalId, targetLocale, selected.TargetLocale, selected.Text,
                !string.Equals(selected.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase));
        }

        string fallback = entry.CanonicalNames.TryGetValue(targetLocale, out string? exact)
            ? exact
            : entry.CanonicalNames.FirstOrDefault(pair => SameLanguage(pair.Key, targetLocale)).Value
              ?? entry.CanonicalNames.GetValueOrDefault("en-US")
              ?? canonicalId;
        return new DomainLexiconRenderedEntry(canonicalId, targetLocale, "und", fallback, true);
    }

    private static bool IsAmbiguous(
        IReadOnlyList<(DomainLexiconEntry Entry, DomainLexiconAlias Alias, int Start, int Length, string Text, bool Formal)> candidates,
        DomainLexiconMatchContext context,
        out string? reason)
    {
        reason = null;
        if (candidates.Count <= 1)
        {
            DomainLexiconAmbiguity? ambiguity = candidates.SingleOrDefault().Alias.Ambiguity ?? candidates.SingleOrDefault().Entry.Ambiguity;
            if (ambiguity?.RequiresReview == true)
            {
                reason = "alias_requires_review";
                return true;
            }
            if (ambiguity?.RequiresContext == true && string.IsNullOrWhiteSpace(context.Domain) && string.IsNullOrWhiteSpace(context.Community) && string.IsNullOrWhiteSpace(context.Channel))
            {
                reason = "alias_requires_context";
                return true;
            }

            return false;
        }

        string group = candidates.Select(candidate => candidate.Alias.Ambiguity?.Group ?? candidate.Entry.Ambiguity?.Group)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "same_surface_multiple_canonical_ids";
        reason = "ambiguous_alias:" + group;
        return true;
    }

    private static bool IsFormal(string status, string reviewStatus, string eligibility) =>
        string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(reviewStatus, "approved", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(eligibility, "formal_eligible", StringComparison.OrdinalIgnoreCase);

    private static bool LocaleAllows(string recordLocale, string requestedLocale) =>
        string.Equals(recordLocale, "*", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(recordLocale, requestedLocale, StringComparison.OrdinalIgnoreCase);

    private static bool ScopeAllows(IReadOnlyList<DomainLexiconContextScope> scopes, DomainLexiconMatchContext context) =>
        scopes.Count == 0 || scopes.Any(scope =>
            (string.IsNullOrWhiteSpace(scope.Domain) || string.Equals(scope.Domain, context.Domain, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(scope.Community) || string.Equals(scope.Community, context.Community, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(scope.Channel) || string.Equals(scope.Channel, context.Channel, StringComparison.OrdinalIgnoreCase)) &&
            (scope.Tags is null || scope.Tags.Count == 0 || (context.Tags is not null && scope.Tags.All(tag => context.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))));

    private static bool PatchAllows(DomainLexiconPatchValidity? validity, string? patch)
    {
        if (validity is null || (string.IsNullOrWhiteSpace(validity.From) && string.IsNullOrWhiteSpace(validity.To)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(patch))
        {
            return false;
        }

        return (string.IsNullOrWhiteSpace(validity.From) || ComparePatch(patch, validity.From) >= 0) &&
               (string.IsNullOrWhiteSpace(validity.To) || ComparePatch(patch, validity.To) <= 0);
    }

    private static int ComparePatch(string left, string right)
    {
        string[] leftParts = left.Split('.', '-', '_');
        string[] rightParts = right.Split('.', '-', '_');
        if (leftParts.All(part => int.TryParse(part, out _)) && rightParts.All(part => int.TryParse(part, out _)))
        {
            int length = Math.Max(leftParts.Length, rightParts.Length);
            for (int index = 0; index < length; index++)
            {
                int leftPart = index < leftParts.Length ? int.Parse(leftParts[index]) : 0;
                int rightPart = index < rightParts.Length ? int.Parse(rightParts[index]) : 0;
                if (leftPart != rightPart) return leftPart.CompareTo(rightPart);
            }

            return 0;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<int> FindSafePhraseOffsets(string text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) yield break;
        int start = 0;
        while ((start = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + phrase.Length;
            if (IsSafeBoundary(text, start - 1) && IsSafeBoundary(text, end)) yield return start;
            start = end;
        }
    }

    private static bool IsSafeBoundary(string text, int index) =>
        index < 0 || index >= text.Length || !IsWordCharacter(text[index]) || ContainsCjkOrHangul(text);

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static bool ContainsCjkOrHangul(string text) => text.Any(character =>
        character is >= '\u3400' and <= '\u4DBF' or
        >= '\u4E00' and <= '\u9FFF' or
        >= '\u3040' and <= '\u30FF' or
        >= '\uAC00' and <= '\uD7AF');

    private static bool SameLanguage(string left, string right) =>
        string.Equals(Language(left), Language(right), StringComparison.OrdinalIgnoreCase);

    private static string Language(string locale) => locale.Split('-')[0];
}

public static class DomainLexiconTextNormalizer
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        bool previousWhitespace = false;
        foreach (char character in text.Trim())
        {
            if (char.IsWhiteSpace(character) || character == '\u00A0' || character == '\u3000')
            {
                if (!previousWhitespace) builder.Append(' ');
                previousWhitespace = true;
            }
            else
            {
                builder.Append(character);
                previousWhitespace = false;
            }
        }

        return builder.ToString();
    }
}
