using System.Text.RegularExpressions;

namespace LoLTranslator.App;

/// <summary>
/// Conservative repair for short player-chat tokens after the sender envelope
/// has already been removed. Player identifiers never enter this layer. A
/// damaged token is changed only when it is present in a reviewed, unambiguous
/// OCR-variant table. Short free-text tokens are not fuzzy-matched because that
/// would turn ordinary words such as "go" or "me" into unrelated abbreviations.
/// </summary>
public static class RuntimeChatOcrTextRepair
{
    private static readonly Regex Token = new(
        @"(?<![\p{L}\p{N}_])(?<token>[A-Za-z]{2,5})(?![\p{L}\p{N}_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> ReviewedVariants =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nm"] = "nvm"
        };

    public static ChatMessageEnvelope Repair(ChatMessageEnvelope envelope) =>
        envelope with { Body = RepairBody(envelope.Body) };

    public static string RepairBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;
        return Token.Replace(body, match =>
        {
            string observed = match.Groups["token"].Value;
            return ReviewedVariants.TryGetValue(observed, out string? repaired)
                ? PreserveCase(observed, repaired)
                : observed;
        });
    }

    private static string PreserveCase(string observed, string repaired) =>
        observed.All(char.IsUpper) ? repaired.ToUpperInvariant() : repaired;

}
