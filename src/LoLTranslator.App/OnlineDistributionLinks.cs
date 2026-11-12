namespace LoLTranslator.App;

/// <summary>
/// Public-facing endpoints intentionally contain no secret or write credential.
/// The application opens the user's browser and never uploads OCR text, chat,
/// screenshots, runtime traces, or player identifiers in the background.
/// </summary>
public static class OnlineDistributionLinks
{
    public const string RepositoryUrl = "https://github.com/junjiea2/LoLTranslator";
    public const string LatestDownloadUrl = RepositoryUrl + "/releases/latest";
}
