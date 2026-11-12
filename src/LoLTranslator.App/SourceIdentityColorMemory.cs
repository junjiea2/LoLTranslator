using System.Text;

namespace LoLTranslator.App;

/// <summary>
/// Stabilizes a sender's source-pixel relation across OCR frames. Only a
/// chromatic blue/red observation (or an explicit Team channel) is learned;
/// neutral/unknown samples can reuse that evidence but can never erase it.
/// </summary>
public sealed class SourceIdentityColorMemory
{
    private readonly Dictionary<string, string> _relations = new(StringComparer.OrdinalIgnoreCase);

    public string? Resolve(string? identity, string? detectedColor, bool explicitAlliedChannel)
    {
        string key = NormalizeIdentity(identity);
        if (explicitAlliedChannel)
        {
            if (key.Length > 0) _relations[key] = "ally";
            return "ally";
        }

        string? observed = detectedColor?.ToLowerInvariant() switch
        {
            "blue" => "ally",
            "red" => "enemy",
            "gold" => "gold",
            "white" => "white",
            _ => null
        };
        if (observed is "ally" or "enemy")
        {
            if (key.Length > 0) _relations[key] = observed;
            return observed;
        }
        if (key.Length > 0 && _relations.TryGetValue(key, out string? remembered)) return remembered;
        return observed;
    }

    public void Reset() => _relations.Clear();

    private string NormalizeIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return string.Empty;
        int open = identity.LastIndexOfAny(['(', '（']);
        int close = identity.LastIndexOfAny([')', '）']);
        ReadOnlySpan<char> stableIdentity = open >= 0 && close > open
            ? identity.AsSpan(open + 1, close - open - 1)
            : identity.AsSpan();
        var normalized = new StringBuilder(stableIdentity.Length);
        foreach (char value in stableIdentity)
        {
            if (char.IsLetterOrDigit(value)) normalized.Append(char.ToLowerInvariant(value));
        }
        string exact = normalized.ToString();
        if (exact.Length < 4 || _relations.ContainsKey(exact)) return exact;
        string[] approximate = _relations.Keys.Where(candidate =>
            Math.Abs(candidate.Length - exact.Length) <= 1 && EditDistance(candidate, exact, 1) <= 1).ToArray();
        return approximate.Length == 1 ? approximate[0] : exact;
    }

    private static int EditDistance(string left, string right, int maximum)
    {
        if (Math.Abs(left.Length - right.Length) > maximum) return maximum + 1;
        int[,] values = new int[left.Length + 1, right.Length + 1];
        for (int row = 0; row <= left.Length; row++) values[row, 0] = row;
        for (int column = 0; column <= right.Length; column++) values[0, column] = column;
        for (int row = 1; row <= left.Length; row++)
        for (int column = 1; column <= right.Length; column++)
        {
            int cost = left[row - 1] == right[column - 1] ? 0 : 1;
            values[row, column] = Math.Min(
                Math.Min(values[row - 1, column] + 1, values[row, column - 1] + 1),
                values[row - 1, column - 1] + cost);
        }
        return values[left.Length, right.Length];
    }
}
