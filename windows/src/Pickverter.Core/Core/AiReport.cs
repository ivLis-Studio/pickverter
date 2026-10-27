namespace Pickverter;

/// Remove BG's cut-outs come from an AI model (the IS-Net model Pickverter downloads and runs on this PC). The Microsoft
/// Store asks apps whose AI makes content to say so where the result appears and to let people report a bad result
/// (Store policy 11.16): the result card and Settings › Integrations link to a short GitHub issue form. Nothing is sent
/// from the app. The form opens in the browser, filled in with the version and the tool, never a file name or a picture.
public static class AiReport
{
    public const string Repository = "ivLis-Studio/pickverter";

    /// .github/ISSUE_TEMPLATE/ai-result.yml; its "app" and "tool" fields are filled in from here.
    public const string Template = "ai-result.yml";

    public const string Label = "ai-report";

    private const string Tool = "tool.removebg";

    /// Options whose results the model makes: Remove BG and its variants (Transparent, Crop to Subject, White Background).
    public static bool Covers(string? optionId) =>
        optionId == Tool || (optionId?.StartsWith(Tool + ".", StringComparison.Ordinal) ?? false);

    /// The report form, filled in with what helps (version, Windows, edition, which Remove BG) and nothing personal.
    public static string Url(string? optionId)
    {
        var style = optionId != null && optionId.StartsWith(Tool + ".", StringComparison.Ordinal) ? optionId[(Tool.Length + 1)..] : null;
        var edition = Distribution.IsStore ? "Microsoft Store" : "GitHub";
        var app = $"Pickverter {Updater.Shared.CurrentVersion} · Windows {Environment.OSVersion.Version.ToString(3)} · {Updater.Architecture} · {edition}";
        var tool = style == null ? "Remove BG" : $"Remove BG ({style})";
        return $"https://github.com/{Repository}/issues/new?template={Template}&labels={Label}" +
               $"&app={Uri.EscapeDataString(app)}&tool={Uri.EscapeDataString(tool)}";
    }

    public static void Open(string? optionId) => Platform.Shell.OpenUrl(Url(optionId));
}
