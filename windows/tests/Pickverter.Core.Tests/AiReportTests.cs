using Xunit;

namespace Pickverter.Tests;

/// Remove BG's results come from an AI model: their cards say so and link to the report form (Microsoft Store policy 11.16).
public class AiReportTests
{
    [Theory]
    [InlineData("tool.removebg", true)]
    [InlineData("tool.removebg.transparent", true)]
    [InlineData("tool.removebg.crop", true)]
    [InlineData("tool.removebg.white", true)]
    [InlineData("tool.removebgx", false)]
    [InlineData("tool.redact.blur", false)]
    [InlineData("tool.ocr", false)]
    [InlineData("convert.png", false)]
    [InlineData(null, false)]
    public void OnlyRemoveBgCountsAsTheModelsWork(string? optionId, bool covered) => Assert.Equal(covered, AiReport.Covers(optionId));

    [Fact]
    public void TheFormComesFilledInWithVersionAndToolOnly()
    {
        var url = new Uri(AiReport.Url("tool.removebg.crop"));
        Assert.Equal("https", url.Scheme);
        Assert.Equal("github.com", url.Host);
        Assert.Equal("/ivLis-Studio/pickverter/issues/new", url.AbsolutePath);
        var query = url.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        Assert.Equal(["template", "labels", "app", "tool"], query.Keys);
        Assert.Equal("ai-result.yml", query["template"]);
        Assert.Equal("ai-report", query["labels"]);
        Assert.Equal("Remove BG (crop)", query["tool"]);
        Assert.StartsWith("Pickverter ", query["app"]);
        Assert.Contains(Distribution.IsStore ? "Microsoft Store" : "GitHub", query["app"]);
        Assert.Equal("Remove BG", Uri.UnescapeDataString(new Uri(AiReport.Url(null)).Query.Split("&tool=")[1]));
    }

    [Fact]
    public async Task OnlyAFinishedRemoveBgCardSaysTheModelMadeIt()
    {
        static PickOption Option(string id) => new() { Id = id, Title = id, Perform = _ => Task.FromResult(new PickResult()) };
        var cutout = new Job(Option("tool.removebg.transparent"), WheelMode.Tools, ["photo.jpg"], ResultDestination.File);
        Assert.False(cutout.MadeByAI);
        await cutout.Complete(new PickResult());
        Assert.True(cutout.MadeByAI);
        var png = new Job(Option("convert.png"), WheelMode.Convert, ["photo.jpg"], ResultDestination.File);
        await png.Complete(new PickResult());
        Assert.False(png.MadeByAI);
        Assert.False(new Job("Notice", "detail", "info.circle", JobTone.Info).MadeByAI);
    }

    [Fact]
    public void TheIssueFormHasTheFieldsTheAppFillsIn()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !Directory.Exists(Path.Combine(folder.FullName, ".github"))) folder = folder.Parent;
        Assert.NotNull(folder);
        var form = File.ReadAllText(Path.Combine(folder!.FullName, ".github", "ISSUE_TEMPLATE", AiReport.Template));
        Assert.Contains("id: app", form);
        Assert.Contains("id: tool", form);
        Assert.Contains(AiReport.Label, form);
    }
}
