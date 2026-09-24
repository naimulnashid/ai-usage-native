using UsageCore.Config;
using UsageCore.Model;
using UsageCore.Parsing;
using UsageCore.View;

namespace UsageCore.Tests;

public class ViewTests
{
    [Fact]
    public void AWindowIsCalendarDaysEndingToday_WithIdleDaysAsEmptyColumns()
    {
        var daily = new List<DailyEntry>
        {
            new() { Date = "2026-06-01", Combined = new UsageCell { Messages = 1, CostUsd = 1 } },
            new() { Date = "2026-08-09", Combined = new UsageCell { Messages = 1, CostUsd = 2 } },
            new() { Date = Dates.UnknownDate, Combined = new UsageCell { Messages = 1 } },
        };
        var days = DayRanges.DaysIn(daily, DayRange.Last30, "2026-08-10");
        Assert.Equal(30, days.Count);
        Assert.Equal("2026-07-12", days[0].Date);
        Assert.Equal("2026-08-10", days[^1].Date);
        Assert.Equal(2, days.Single(d => d.Date == "2026-08-09").Combined.CostUsd);
        Assert.DoesNotContain(days, d => d.Date == Dates.UnknownDate);
        Assert.Equal(3, DayRanges.DaysIn(daily, DayRange.All, "2026-08-10").Count);
    }

    [Fact]
    public void StreaksRunToTodayOrYesterday()
    {
        Assert.Equal((3, 3), Dates.Streaks(["2026-08-07", "2026-08-08", "2026-08-09"], "2026-08-10"));
        Assert.Equal((0, 3), Dates.Streaks(["2026-08-01", "2026-08-02", "2026-08-03"], "2026-08-10"));
        Assert.Equal((0, 0), Dates.Streaks([], "2026-08-10"));
    }

    [Fact]
    public void ADearerModelIsNeverLighterThanACheaperOne()
    {
        static double Luminance(string hex)
        {
            double Channel(int i)
            {
                var c = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255d;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
        }

        foreach (var family in new[] { "claude-", "gpt-|codex-" })
        {
            var ramp = ModelColors.Shades.Where(s => family.Split('|').Any(p => s.Model.StartsWith(p, StringComparison.Ordinal))).ToList();
            for (var i = 1; i < ramp.Count; i++)
            {
                Assert.True(ramp[i - 1].Shade.OutputPrice >= ramp[i].Shade.OutputPrice, $"{ramp[i].Model} is out of price order");
                Assert.True(Luminance(ramp[i - 1].Shade.Hex) <= Luminance(ramp[i].Shade.Hex), $"{ramp[i - 1].Model} is lighter than {ramp[i].Model}");
            }
        }
    }

    [Fact]
    public void EveryModelShadeClears3To1AgainstThePanel()
    {
        static double Lum(string hex)
        {
            double Ch(int i)
            {
                var c = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255d;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(0) + 0.7152 * Ch(1) + 0.0722 * Ch(2);
        }
        var panel = Lum("#0A0A0C");
        foreach (var (model, shade) in ModelColors.Shades)
        {
            var ratio = (Lum(shade.Hex) + 0.05) / (panel + 0.05);
            Assert.True(ratio >= 3, $"{model} is {ratio:0.00}:1");
        }
    }

    [Fact]
    public void LogoKeysIgnoreCaseAndSeparators()
    {
        Assert.Equal("my app", ProjectLogos.Key("My_App"));
        Assert.Equal("some app web", ProjectLogos.Key("Some App - Web"));
        Assert.Equal("MA", ProjectLogos.Initials("My App"));
        Assert.Equal("RE", ProjectLogos.Initials("recipes"));
    }

    [Fact]
    public void AliasesPriceAJobTitleAsItsModel()
    {
        var pricing = Fixtures.Pricing(new Dictionary<string, string> { ["codex-auto-review"] = "cheap-model" });
        Assert.Equal(pricing.Models["cheap-model"], AppConfig.GetRate(pricing, "codex-auto-review"));
        Assert.Null(AppConfig.GetRate(pricing, "nothing"));
    }

    [Fact]
    public void TheBuiltInRateCardsLoad()
    {
        var claude = AppConfig.LoadPricing(ProviderId.Claude, configDir: Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var codex = AppConfig.LoadPricing(ProviderId.Codex, configDir: Path.Combine(Path.GetTempPath(), "no-such-dir"));
        Assert.Equal("built-in", claude.Source);
        Assert.Equal(new ModelRate(2, 2.5, 4, 0.2, 10), claude.Models["claude-sonnet-5"]);
        Assert.Equal("gpt-5.3-codex", codex.Aliases["codex-auto-review"]);
        Assert.Equal(0, claude.Models["<synthetic>"].Output);
    }

    [Fact]
    public void TheHeatmapStripIs26WeeksEndingToday()
    {
        var today = new DateOnly(2026, 8, 10); // a Monday
        var layout = Heatmap.Recent([], today, DayOfWeek.Monday);
        var strip = Assert.Single(layout.Strips);
        Assert.Equal(26 * 7, strip.Cells.Count);
        Assert.Equal("2026-08-10", strip.LastDate);
        Assert.All(strip.Cells.Where(c => c.Outside), c => Assert.True(string.CompareOrdinal(c.Date, "2026-08-10") > 0));
    }
}
