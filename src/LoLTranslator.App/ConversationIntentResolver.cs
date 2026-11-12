using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoLTranslator.App;

public sealed record ConversationIntent(string Id, string Text);

/// <summary>Whole-message intent plus optional closed address slot; never substring/fuzzy matching.</summary>
public sealed class ConversationIntentResolver
{
    private sealed record Entry(string Id, string[] Forms, Dictionary<string, string> Renderings);
    private sealed record Catalog(string Version, Entry[] Intents, Entry[] Addresses);
    private static readonly Lazy<ConversationIntentResolver> Default = new(() => Load(Path.Combine(
        AppContext.BaseDirectory, "data/runtime/lol-conversation-intents.v0.1.json")));
    private readonly Catalog _catalog;
    private ConversationIntentResolver(Catalog catalog) => _catalog = catalog;
    public static ConversationIntentResolver Current => Default.Value;

    public static ConversationIntentResolver Load(string path)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing conversation catalog");
        if (catalog.Version != "0.1-runtime-preview" || catalog.Intents is null || catalog.Addresses is null ||
            catalog.Intents.Select(e => e.Id).Distinct().Count() != catalog.Intents.Length ||
            catalog.Intents.Concat(catalog.Addresses).Any(e => e.Forms is null || e.Forms.Length == 0 ||
                e.Forms.Any(string.IsNullOrWhiteSpace) || e.Renderings is null || e.Renderings.Values.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("Invalid conversation catalog");
        return new(catalog);
    }

    public bool TryResolve(string body, string locale, out ConversationIntent? result)
    {
        result = null;
        // Questions, quotes, negations and extended clauses must not lose their
        // meaning through punctuation stripping or a prefix-only match.
        if (body.Contains('?') || body.Contains('？') || body.Contains('\n')) return false;
        string normalized = Regex.Replace(body.Trim().TrimEnd('!', '.', '！', '。'), @"\s+", " ").ToLowerInvariant();
        var matches = new List<ConversationIntent>();
        foreach (var entry in _catalog.Intents)
        {
            if (!entry.Renderings.TryGetValue(locale, out string? text)) continue;
            foreach (string form in entry.Forms)
            {
                if (normalized == form) matches.Add(new(entry.Id, text));
                foreach (var address in _catalog.Addresses)
                    if (address.Renderings.TryGetValue(locale, out string? addressed) && address.Forms.Any(a =>
                        normalized == form + " " + a || normalized == form + ", " + a))
                        matches.Add(new(entry.Id, text + (locale.StartsWith("zh") ? "，" : ", ") + addressed));
            }
        }
        if (matches.Count != 1) return false; // Conflict never chooses by file order.
        result = matches[0]; return true;
    }
}
