using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SlangGraph.Core;

namespace LoLTranslator.App;

public sealed record OfficialItemPingResolution(
    string CanonicalId,
    string SourceItemName,
    string TargetItemName,
    string State,
    int? MissingGold,
    string Rendering);

public sealed record RuntimeItemDisplayAlias(string CanonicalId, string Locale, string Surface);

public static class RuntimeItemDisplayAliasLoader
{
    private sealed record AliasRecord(
        [property: JsonPropertyName("record_type")] string RecordType,
        [property: JsonPropertyName("canonical_id")] string CanonicalId,
        [property: JsonPropertyName("locale")] string Locale,
        [property: JsonPropertyName("surface")] string Surface,
        [property: JsonPropertyName("review_status")] string ReviewStatus,
        [property: JsonPropertyName("formal_eligibility")] string FormalEligibility);

    public static IReadOnlyList<RuntimeItemDisplayAlias> LoadApproved(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Array.Empty<RuntimeItemDisplayAlias>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<AliasRecord>(line, options)
                ?? throw new InvalidDataException("A runtime item display alias is invalid."))
            .Where(record => string.Equals(record.RecordType, "client_item_display_alias", StringComparison.Ordinal) &&
                             string.Equals(record.ReviewStatus, "approved", StringComparison.Ordinal) &&
                             string.Equals(record.FormalEligibility, "runtime_only", StringComparison.Ordinal))
            .Select(record => new RuntimeItemDisplayAlias(record.CanonicalId, record.Locale, record.Surface))
            .ToArray();
    }
}

/// <summary>
/// Resolves only a strict in-game item-ping template around an exact official
/// item display name. Candidate aliases are never loaded or fuzzy-matched.
/// This runtime-only structure preserves the item and numeric gold slot without
/// asking a general translation model to reproduce them.
/// </summary>
public sealed class OfficialItemPingResolver
{
    private static readonly Regex ChineseTemplate = new(
        @"^\s*(?<item>.+?)\s*[-—–－]\s*(?:(?<ready>可以购买|可以購買|可购买|可購買)|(?:还?需(?:要)?|還?需(?:要)?)\s*(?<gold>\d{1,5})\s*(?:金币|金幣|金钱|金錢))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EnglishTemplate = new(
        @"^\s*(?<item>.+?)\s*[-—–]\s*(?:(?<ready>ready\s+to\s+(?:buy|purchase)|available\s+to\s+purchase)|(?:(?<gold>\d{1,5})\s+gold\s+(?:needed|required)|need\s+(?<gold2>\d{1,5})\s+gold))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed record ItemNames(
        string CanonicalId,
        IReadOnlyDictionary<string, string> Names,
        IReadOnlyList<RuntimeItemDisplayAlias> RuntimeAliases);
    private readonly IReadOnlyList<ItemNames> _items;

    private OfficialItemPingResolver(IReadOnlyList<ItemNames> items) => _items = items;

    public static OfficialItemPingResolver Load(string path, string? runtimeAliasPath = null)
    {
        DomainLexicon lexicon = DomainLexiconLoader.LoadJsonl(path);
        IReadOnlyList<RuntimeItemDisplayAlias> aliases = RuntimeItemDisplayAliasLoader.LoadApproved(runtimeAliasPath);
        ItemNames[] items = lexicon.Entries
            .Where(entry => string.Equals(entry.Kind, "entity", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Type, "item", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new ItemNames(
                entry.CanonicalId,
                entry.CanonicalNames,
                aliases.Where(alias => string.Equals(alias.CanonicalId, entry.CanonicalId, StringComparison.OrdinalIgnoreCase)).ToArray()))
            .ToArray();
        if (items.Length == 0) throw new InvalidDataException("No official item display names were loaded.");
        return new OfficialItemPingResolver(items);
    }

    public bool TryResolve(
        string text,
        IReadOnlyList<string> sourceLocales,
        string targetLocale,
        out OfficialItemPingResolution? resolution)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceLocales);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLocale);

        Match template = ChineseTemplate.Match(text);
        if (!template.Success) template = EnglishTemplate.Match(text);
        if (!template.Success)
        {
            resolution = null;
            return false;
        }

        string sourceName = template.Groups["item"].Value.Trim();
        ItemNames[] candidates = _items.Where(item => sourceLocales.Any(locale =>
            ResolveLocale(item.Names, locale) is string name && string.Equals(name, sourceName, StringComparison.OrdinalIgnoreCase) ||
            item.RuntimeAliases.Any(alias => LocaleCompatible(alias.Locale, locale) &&
                string.Equals(alias.Surface, sourceName, StringComparison.OrdinalIgnoreCase)))).ToArray();
        if (candidates.Length == 0)
        {
            resolution = null;
            return false;
        }

        var rendered = candidates.Select(item => (Item: item, Name: ResolveRuntimeName(item, targetLocale)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray();
        string[] distinctTargets = rendered.Select(item => item.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (rendered.Length == 0 || distinctTargets.Length != 1)
        {
            resolution = null;
            return false;
        }

        string targetName = distinctTargets[0];
        string? goldText = template.Groups["gold"].Success
            ? template.Groups["gold"].Value
            : template.Groups["gold2"].Value;
        int? gold = int.TryParse(goldText, out int parsedGold) ? parsedGold : null;
        string state = gold is null ? "purchasable_now" : "missing_gold";
        string rendering = Render(targetName, state, gold, targetLocale);
        resolution = new OfficialItemPingResolution(
            rendered[0].Item.CanonicalId, sourceName, targetName, state, gold, rendering);
        return true;
    }

    private static string Render(string itemName, string state, int? gold, string targetLocale)
    {
        if (targetLocale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return state == "purchasable_now" ? $"{itemName} — 可以购买" : $"{itemName} — 还需要 {gold} 金币";
        if (targetLocale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return state == "purchasable_now" ? $"{itemName} — Ready to purchase" : $"{itemName} — {gold} gold needed";
        return state == "purchasable_now" ? $"{itemName} — Ready to purchase" : $"{itemName} — {gold} gold needed";
    }

    private static string? ResolveLocale(IReadOnlyDictionary<string, string> names, string locale)
    {
        if (names.TryGetValue(locale, out string? exact)) return exact;
        string language = locale.Split('-', '_')[0];
        return names.FirstOrDefault(pair =>
            string.Equals(pair.Key.Split('-', '_')[0], language, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string? ResolveRuntimeName(ItemNames item, string locale) =>
        item.RuntimeAliases.FirstOrDefault(alias => LocaleCompatible(alias.Locale, locale))?.Surface
        ?? ResolveLocale(item.Names, locale);

    private static bool LocaleCompatible(string left, string right) =>
        string.Equals(left.Split('-', '_')[0], right.Split('-', '_')[0], StringComparison.OrdinalIgnoreCase);
}
