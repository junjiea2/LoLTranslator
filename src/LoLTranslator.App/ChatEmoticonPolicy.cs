namespace LoLTranslator.App;

/// <summary>Closed, whole-message symbols. Not arbitrary punctuation/noise and not invented translations.</summary>
public static class ChatEmoticonPolicy
{
    public static bool IsRecognized(string body) => body.Trim() is
        ":(" or ":)" or ":-(" or ":-)" or ";)" or ":D" or "D:" or ":/" or "<3";
}
