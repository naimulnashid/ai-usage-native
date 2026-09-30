using UsageCore.Config;
using UsageCore.Model;
using UsageCore.View;
using static UsageCore.Tests.Fixtures;

namespace UsageCore.Tests;

/// <summary>Project colours, the light theme's shades, and the 90-day window.</summary>
public class ProjectColorTests
{
    private const int Size = 64;

    private static byte[] Image(Func<int, int, byte[]> paint)
    {
        var data = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++) paint(x, y).CopyTo(data, (y * Size + x) * 4);
        }
        return data;
    }

    private static bool In(int x, int y, int from, int to) => x >= from && x < to && y >= from && y < to;

    private static readonly byte[] Orange = [217, 119, 87, 255];

    [Fact]
    public void ReadsAColourTypedByHand()
    {
        Assert.Equal("#3B82F6", ProjectColors.Parse("#3b82f6"));
        Assert.Equal("#3B82F6", ProjectColors.Parse(" 3B82F6 "));
        Assert.Equal("#AABBCC", ProjectColors.Parse("#abc"));
        foreach (var bad in new[] { "", "#12345", "red", "#GGGGGG", "#1234567" }) Assert.Null(ProjectColors.Parse(bad));
    }

    [Fact]
    public void FitsAColourIntoTheBandBothThemesCanSee()
    {
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#0A0A40", "#FFF3B0", "#777777" })
        {
            var fitted = ProjectColors.FitForCharts(hex);
            Assert.True(ProjectColors.Contrast(fitted, "#0A0A0C") >= 3, $"{hex} -> {fitted} on the dark panel");
            Assert.True(ProjectColors.Contrast(fitted, "#FFFFFF") >= 3, $"{hex} -> {fitted} on white");
        }
        Assert.Equal("#3B82F6", ProjectColors.FitForCharts("#3b82f6"));
        foreach (var hex in ProjectColors.Fallbacks) Assert.Equal(hex, ProjectColors.FitForCharts(hex));
    }

    [Fact]
    public void PrefersYourColour_ThenTheLogo_ThenAFallbackInRankOrder()
    {
        var assigned = ProjectColors.Assign(["a", "b", "c", "d"],
            new Dictionary<string, string> { ["b"] = "#123456" },
            id => id == "c" ? "#3B82F6" : null);
        Assert.Equal(("#123456", ProjectColorSource.Custom), assigned["b"]);
        Assert.Equal(("#3B82F6", ProjectColorSource.Logo), assigned["c"]);
        Assert.Equal((ProjectColors.Fallbacks[0], ProjectColorSource.Auto), assigned["a"]);
        Assert.Equal((ProjectColors.Fallbacks[1], ProjectColorSource.Auto), assigned["d"]);
    }

    [Fact]
    public void ALogosDominantColour_SkipsATileAndItsHairlineBorder()
    {
        // The shape that broke: a grey 1px border round a black fill, with the
        // coloured mark in the middle. Reading only the outer ring called the
        // border the background, and the black fill won.
        var data = Image((x, y) =>
            x == 0 || y == 0 || x == Size - 1 || y == Size - 1 ? [30, 30, 36, 255]
            : In(x, y, 22, 42) ? Orange
            : [0, 0, 0, 255]);
        Assert.Equal("#D97757", ProjectColors.DominantColor(data, Size, Size));
    }

    [Fact]
    public void ALogosDominantColour_PrefersColourToLettering_AndATileToAPlainGlyph()
    {
        var mark = Image((x, y) => In(x, y, 10, 20) ? Orange : In(x, y, 10, 54) ? [0, 0, 0, 255] : [0, 0, 0, 0]);
        Assert.Equal("#D97757", ProjectColors.DominantColor(mark, Size, Size));

        var tile = Image((x, y) => In(x, y, 24, 40) ? [255, 255, 255, 255] : [16, 163, 127, 255]);
        Assert.Equal("#10A37F", ProjectColors.DominantColor(tile, Size, Size));

        var grey = Image((x, y) => In(x, y, 16, 48) ? [60, 60, 60, 255] : [0, 0, 0, 0]);
        Assert.Equal("#3C3C3C", ProjectColors.DominantColor(grey, Size, Size));

        Assert.Null(ProjectColors.DominantColor(Image((_, _) => [0, 0, 0, 0]), Size, Size));
    }

    [Fact]
    public void SavesReadsAndClearsAProjectsColour_PerAgent()
    {
        using var tmp = new TempDir();
        const string Id = "C--Users-you-Projects-my-app";
        Assert.Empty(ProjectColorSettings.Load(ProviderId.Claude, tmp.Path));
        ProjectColorSettings.Save(ProviderId.Claude, Id, "#3b82f6", tmp.Path);
        Assert.Equal("#3B82F6", ProjectColorSettings.Load(ProviderId.Claude, tmp.Path)[Id]);
        Assert.Empty(ProjectColorSettings.Load(ProviderId.Codex, tmp.Path));
        ProjectColorSettings.Save(ProviderId.Claude, Id, null, tmp.Path);
        Assert.Empty(ProjectColorSettings.Load(ProviderId.Claude, tmp.Path));
        Assert.Throws<ArgumentException>(() => ProjectColorSettings.Save(ProviderId.Claude, Id, "red", tmp.Path));

        File.WriteAllText(ProjectColorSettings.PathFor(ProviderId.Claude, tmp.Path), "{ not json");
        Assert.Empty(ProjectColorSettings.Load(ProviderId.Claude, tmp.Path));
    }

    [Fact]
    public void TheLightThemesShades_ClearWhite_AndStillDarkenWithPrice()
    {
        foreach (var (agent, palette) in ModelColors.LightPalettes)
        {
            Assert.Equal(ModelColors.Palettes[agent].Count, palette.Count);
            for (var i = 0; i < palette.Count; i++)
            {
                Assert.True(ProjectColors.Contrast(palette[i], "#FFFFFF") >= 3, $"{agent} {palette[i]} on white");
                if (i > 0) Assert.True(ProjectColors.Luminance(palette[i]) > ProjectColors.Luminance(palette[i - 1]));
            }
        }
        foreach (var (model, shade) in ModelColors.Shades)
        {
            var twin = ModelColors.Themed(shade.Hex, light: true);
            Assert.NotEqual(shade.Hex, twin);
            Assert.True(ProjectColors.Contrast(twin, "#FFFFFF") >= 3, $"{model}'s twin {twin}");
        }
        Assert.Equal("#D97757", ModelColors.Themed("#D97757", light: false));
    }

    [Fact]
    public void TheLightThemesAccentsCarryText_AndItsHeatMapDarkensWithSpend()
    {
        foreach (var meta in Providers.All)
        {
            Assert.True(ProjectColors.Contrast(meta.AccentLight, "#FFFFFF") >= 4.5, meta.Label);
            Assert.True(ProjectColors.Contrast(meta.AccentBrightLight, "#F4F5F8") >= 4.5, meta.Label);
            for (var i = 1; i < meta.HeatRampLight.Length; i++)
            {
                Assert.True(ProjectColors.Contrast(meta.HeatRampLight[i], "#FFFFFF") >= 3, $"{meta.Label} step {i}");
                if (i > 1) Assert.True(ProjectColors.Luminance(meta.HeatRampLight[i]) < ProjectColors.Luminance(meta.HeatRampLight[i - 1]));
            }
        }
    }

    [Fact]
    public void ANinetyDayWindowIsNinetyColumns()
    {
        var days = DayRanges.DaysIn([], DayRange.Last90, "2026-08-10");
        Assert.Equal(90, days.Count);
        Assert.Equal("2026-05-13", days[0].Date);
        Assert.Equal("Last 90 days", DayRanges.Label(DayRange.Last90));
    }
}
