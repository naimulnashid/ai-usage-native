using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageApp.Charts;
using UsageApp.Controls;
using UsageApp.Theme;
using UsageCore.Model;
using UsageCore.Parsing;
using UsageCore.View;

namespace UsageApp.Views;

/// <summary>
/// One agent's overview: the headline, daily spend, the per-model breakdown,
/// the twelve activity cards, the stacked daily charts and the heat map.
/// </summary>
public sealed class OverviewPage(PageContext ctx) : IPage
{
    public UIElement Build()
    {
        var meta = ctx.State.Meta;
        var data = ctx.State.Current;
        var page = new StackPanel { Padding = new Thickness(0, 34, 0, 0) };

        if (data.Report is null)
        {
            if (data.Error is not null && !data.Loading) return ErrorView(meta, data.Error);
            return Loading();
        }
        var report = data.Report;
        if (ReportState.IsEmpty(report))
        {
            page.Children.Add(Parts.EmptyState(meta, report.Diagnostics.Warnings));
            return page;
        }

        var global = report.Global;
        var daily = report.Daily;
        var modelCount = global.PerModel.Count;
        var activeDays = daily.Count(d => d.Combined.CostUsd > 0);
        var avgPerDay = activeDays > 0 ? global.Combined.CostUsd / activeDays : 0;

        // ---- Headline: the number to check first ------------------------------
        var extras = new List<UIElement>
        {
            Parts.HeadlineMeta($"across {Format.Count(report.Projects.Count)} {(report.Projects.Count == 1 ? "project" : "projects")} · {Format.Count(modelCount)} {(modelCount == 1 ? "model" : "models")} · {Format.Count(activeDays)} active {(activeDays == 1 ? "day" : "days")}"),
        };
        // Say what span these totals cover: agents delete their own transcripts,
        // so "total" is a window, and a shrinking one must not read as less spend.
        if (report.Coverage is { EarliestDate: { } from, LatestDate: { } to } coverage)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
            line.Children.Add(Ui.Text($"{Format.DateShort(from)} – {Format.DateShort(to)}", 13.5, 400, Palette.TextFaintBrush));
            if (coverage.Restored)
            {
                var badge = Ui.Pill($"{Format.Count(coverage.ArchivedOnlyDays)} archived", Palette.TextMutedBrush, Palette.BorderBrightBrush);
                Ui.SetTip(badge, $"{coverage.ArchivedOnlyDays} earlier {(coverage.ArchivedOnlyDays == 1 ? "day is" : "days are")} kept from this app's own archive. {meta.Label} has already deleted those transcripts, so the numbers come from what was recorded before they went.");
                line.Children.Add(badge);
            }
            extras.Add(line);
        }
        var headline = Ui.Card(
            Parts.HeadlineGrid(meta, global.Combined.CostUsd, extras,
                (Format.Tokens(global.Combined.TotalTokens), $"{Format.Count(global.Combined.Messages)} {meta.MessageNoun}"),
                (Format.Duration(global.Combined.RuntimeSeconds), $"{Format.Usd(avgPerDay)} avg / active day"),
                global.Combined),
            new Thickness(44, 40, 44, 40));
        headline.Margin = new Thickness(0, 0, 0, 22);
        Ui.Rise(headline);
        page.Children.Add(headline);

        if (Parts.UnpricedNotice(meta, report.Diagnostics.UnpricedModels) is { } unpriced) page.Children.Add(unpriced);
        if (Parts.ParseWarnings(ReportState.NoteworthyWarnings(report.Diagnostics.Warnings)) is { } warnings) page.Children.Add(warnings);

        // ---- Daily combined spend ---------------------------------------------
        UIElement? largest = null;
        if (report.Projects.FirstOrDefault() is { } top)
        {
            var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var caption = Ui.Text("Largest project", 13, 400, Palette.TextFaintBrush);
            caption.HorizontalAlignment = HorizontalAlignment.Right;
            box.Children.Add(caption);
            var line = new TextBlock { FontFamily = Fonts.Sans, FontSize = 16, FontWeight = Fonts.Weight(600), Foreground = Palette.TextBrush, HorizontalAlignment = HorizontalAlignment.Right };
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = top.Name + " " });
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Format.Usd(top.Combined.CostUsd), Foreground = Palette.Accent });
            box.Children.Add(line);
            largest = box;
        }
        var spend = Ui.Panel("Daily combined spend", "All models, all projects. Days marked in red sit well above trend.", largest,
            daily.Any(d => Dates.IsDayKey(d.Date)) ? new SpendAreaChart(daily.Where(d => Dates.IsDayKey(d.Date)).ToList()) : Parts.NoDaysInRange(daily, DayRange.All));
        Ui.Rise(spend, 100);
        page.Children.Add(spend);

        // ---- Per model ---------------------------------------------------------
        page.Children.Add(Ui.SectionTitle("Breakdown by model"));
        page.Children.Add(Parts.ModelCards(global.PerModel));

        var costChart = new CostByModelChart(global.PerModel);
        var rateNote = report.PricingLastVerified is { } verified
            ? $" Rates from {(report.PricingSource == "built-in" ? "the built-in rate card" : $"`{report.PricingSource}`")}, last verified {Format.DateStamp(verified)}."
            : "";
        var costPanel = Ui.Panel("Cost by model", "Estimated spend per model across every project." + rateNote, null,
            costChart.IsEmpty ? Ui.Text("No model usage found.", 15, 400, Palette.TextFaintBrush) : costChart);
        Ui.Rise(costPanel, 140);
        page.Children.Add(costPanel);

        var tokenPanel = Ui.Panel("Token detail by model",
            meta.HasCacheWrites
                ? "Cache writes are split by TTL internally and priced separately; the column below shows their sum."
                : "Input counts only the uncached remainder of each prompt — the cached part is billed at a tenth of the rate and has its own column.",
            null, Parts.ModelBreakdownTable(meta, global.PerModel, global.Combined));
        Ui.Rise(tokenPanel, 180);
        page.Children.Add(tokenPanel);

        // ---- Activity -----------------------------------------------------------
        page.Children.Add(Ui.SectionTitle("Activity"));
        page.Children.Add(ScoreCards.Build(meta, report.Activity, global.Combined));

        var today = DayRanges.Today(report);
        page.Children.Add(Parts.StackedPanel("Daily tokens by model",
            "Stacked by model, most expensive at the bottom — so the darker the base of a column, the more of that day went on premium tokens.",
            daily, today, spend: false));
        page.Children.Add(Parts.StackedPanel("Daily spend by model",
            "The same columns priced instead of counted — so a day that looks modest above and tall here went on the expensive models.",
            daily, today, spend: true));

        page.Children.Add(ActivityPanel(report));
        return page;
    }

    /// <summary>The heat map, with the week-on-week trend and, when there is older history, Expand.</summary>
    private Border ActivityPanel(UsageReport report)
    {
        var daily = report.Daily;
        // The last seven recorded days against the seven before them.
        var recent = daily.TakeLast(7).Sum(d => d.Combined.CostUsd);
        var previous = daily.SkipLast(7).TakeLast(7).Sum(d => d.Combined.CostUsd);
        UIElement? trend = null;
        if (daily.Count > 7 && previous > 0)
        {
            var pct = (recent - previous) / previous * 100;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(Ui.Text($"{(pct > 0 ? "↑" : "↓")} {Math.Abs(pct):0}%", 19, 640, pct > 0 ? Palette.WarnBrush : Palette.GoodBrush, numeric: true));
            var vs = Ui.Text("vs previous 7 days", 14, 400, Palette.TextFaintBrush);
            vs.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(vs);
            trend = line;
        }

        var today = Heatmap.Today(report);
        var first = Heatmap.FirstDay(report.Settings.WeekStartsOn);
        var layout = Heatmap.Recent(daily, today, first);
        Button? expand = null;
        if (Heatmap.HasHistoryBeforeWindow(daily, today, first))
        {
            expand = Ui.Button("Expand", fontSize: 13.5, padding: new Thickness(14, 5, 14, 5));
            expand.Click += (_, _) => ctx.Navigate(new Route(PageKind.Activity));
        }
        var figure = HeatmapFigure.Build(layout, report.Settings.WeekStartsOn, "in the last 6 months", labelStrips: false, expand);
        return Ui.Panel("Daily activity", "Spend per day over the last 6 months. Brighter means a more expensive day.", trend, figure);
    }

    public static UIElement Loading()
    {
        var page = new StackPanel { Padding = new Thickness(0, 34, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(page, "Loading the dashboard");
        page.Children.Add(Parts.Skeleton(269));
        page.Children.Add(Parts.Skeleton(433));
        page.Children.Add(Parts.Skeleton(13, 170, 18));
        var grid = FitGrid.AutoFit(215, 16);
        grid.Margin = new Thickness(0, 0, 0, 34);
        for (var i = 0; i < 4; i++) grid.Children.Add(Parts.Skeleton(149, bottom: 0));
        page.Children.Add(grid);
        page.Children.Add(Parts.Skeleton(393));
        var ring = new ProgressRing { IsActive = true, Width = 28, Height = 28, Foreground = Palette.Accent, Margin = new Thickness(0, 10, 0, 0) };
        page.Children.Insert(0, ring);
        ring.Margin = new Thickness(0, 0, 0, 16);
        ring.HorizontalAlignment = HorizontalAlignment.Left;
        return page;
    }

    public static UIElement ErrorView(UsageCore.ProviderMeta meta, string error)
    {
        var page = new StackPanel { Padding = new Thickness(0, 34, 0, 0) };
        page.Children.Add(Parts.Notice(Ui.Paragraph(
            $"Could not read your {meta.Label} transcripts. {error}\nExpected them under `{meta.TranscriptHint}`. Set `{meta.TranscriptEnvVar}` if yours live elsewhere, then hit Refresh.",
            14.5, Palette.TextMutedBrush)));
        return page;
    }
}
