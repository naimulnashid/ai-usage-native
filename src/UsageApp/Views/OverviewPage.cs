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
/// One agent's overview: the headline, daily spend, cost by model, the twelve
/// activity cards and the heat map, the stacked daily charts, then the token
/// and price tables. A project page is the same page at project scope.
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

        // ---- Cost by model -------------------------------------------------------
        page.Children.Add(CostPanel(report, global.PerModel, "Estimated spend per model across every project."));

        // ---- Activity: the cards, the heat map, then the daily charts ----------
        page.Children.Add(Ui.SectionTitle("Activity"));
        page.Children.Add(ScoreCards.Build(meta, report.Activity, global.Combined));
        page.Children.Add(ActivityPanel(ctx, report, daily, projectId: null));

        var today = DayRanges.Today(report);
        page.Children.Add(ProjectPanel(report, today));
        page.Children.Add(Parts.StackedPanel("Daily tokens by model",
            "Stacked by model, most expensive at the bottom — so the darker the base of a column, the more of that day went on premium tokens.",
            daily, today, spend: false));
        page.Children.Add(Parts.StackedPanel("Daily spend by model",
            "The same columns priced instead of counted — so a day that looks modest above and tall here went on the expensive models.",
            daily, today, spend: true));

        // ---- The detail tables: tokens, then the rates behind the costs --------
        page.Children.Add(TokenPanel(meta, global.PerModel, global.Combined));
        page.Children.Add(Ui.Panel("Model prices", ModelPrices.Subtitle, null, ModelPrices.Table(ctx, global.PerModel, report.ModelRates)));
        return page;
    }

    /// <summary>How many projects get a band of their own; the rest, and every hidden one, are Other.</summary>
    private const int MaxProjectBands = 8;

    /// <summary>
    /// Daily spend by project: stacked areas, each project in its own colour,
    /// over the last 30 days (the default), the last 90 or every day. Which
    /// projects get a band is decided over the days shown.
    /// </summary>
    private Border ProjectPanel(UsageReport report, string today)
    {
        var body = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = ProjectChart(report, DayRange.Last30, today),
        };
        var picker = RangePicker.Create("Days shown in Daily spend by project", range => body.Content = ProjectChart(report, range, today), DayRange.Last30, RangePicker.ProjectRanges);
        var panel = Ui.Panel("Daily spend by project",
            "Each project in its own colour, largest at the bottom. The top eight over the days shown get a band; the rest are summed into Other.",
            picker, body);
        Ui.Rise(panel, 65);
        return panel;
    }

    private UIElement ProjectChart(UsageReport report, DayRange range, string today)
    {
        var days = DayRanges.DaysIn(report.Daily, range, today);
        var index = days.Select((d, i) => (d.Date, i)).ToDictionary(x => x.Date, x => x.i, StringComparer.Ordinal);
        var hidden = ctx.State.Hidden.For(ctx.State.Provider);
        var colors = ctx.Colors.For(ctx.State.Provider, report.Projects);

        var perProject = report.Projects.Select(p =>
        {
            var values = new double[days.Count];
            foreach (var day in p.Daily)
            {
                if (index.TryGetValue(day.Date, out var i) && day.Combined.CostUsd > 0) values[i] = day.Combined.CostUsd;
            }
            return (Project: p, Values: values, Cost: values.Sum());
        }).Where(x => x.Cost > 0).ToList();
        var ranked = perProject.Where(x => !hidden.Contains(x.Project.Id)).OrderByDescending(x => x.Cost).ToList();
        var hiddenOnes = perProject.Where(x => hidden.Contains(x.Project.Id)).ToList();
        // As the donut does: at exactly one over the cap, Other would stand for one project.
        var collapse = hiddenOnes.Count > 0 || ranked.Count > MaxProjectBands + 1;
        var banded = collapse ? ranked.Take(MaxProjectBands).ToList() : ranked;
        var rest = collapse ? ranked.Skip(MaxProjectBands).Concat(hiddenOnes).ToList() : [];

        var series = banded.Select(x => new ProjectSeries(x.Project.Id, x.Project.Name, Palette.Hex(colors[x.Project.Id].Hex), x.Values, 1)).ToList();
        if (rest.Count > 0)
        {
            var sums = new double[days.Count];
            foreach (var x in rest) for (var i = 0; i < sums.Length; i++) sums[i] += x.Values[i];
            series.Add(new ProjectSeries(null, hiddenOnes.Count == rest.Count ? "Hidden" : "Other", Palette.OthersColor, sums, rest.Count));
        }
        if (series.Count == 0) return Parts.NoDaysInRange(report.Daily, range);

        var stack = new StackPanel();
        stack.Children.Add(new ProjectAreaChart(days.Select(d => d.Date).ToList(), series));
        stack.Children.Add(ProjectLegend(series));
        return stack;
    }

    /// <summary>
    /// Chips in stack order: the colour, the logo where there is one, the name -
    /// nothing else, by the owner's choice; the figures are in the tooltip.
    /// Centred, each wrapped line too. A project's chip opens it.
    /// </summary>
    private UIElement ProjectLegend(List<ProjectSeries> series)
    {
        var panel = new CenteredWrapPanel { HorizontalSpacing = 10, VerticalSpacing = 8 };
        foreach (var s in series)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(Ui.Swatch(s.Color));
            content.Children.Add(s.Id is not null
                ? ProjectLogoView.Create(s.Name, ctx.Logos.PathFor(ctx.State.Provider, s.Name), 18)
                : Ui.Text($"+{s.Projects}", 11.5, 620, Palette.TextFaintBrush, numeric: true, selectable: false));
            var name = Ui.Text(s.Name, 14, 560, selectable: false);
            name.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(name);
            var chip = new Border
            {
                Child = content,
                Padding = new Thickness(9, 5, 11, 5),
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.BorderBrush,
                Background = Palette.SurfaceBrush,
            };
            if (s.Id is { } id)
            {
                Ui.HoverLift(chip, 1);
                chip.PointerEntered += (_, _) => chip.Background = Palette.SurfaceHoverBrush;
                chip.PointerExited += (_, _) => chip.Background = Palette.SurfaceBrush;
                chip.Tapped += (_, _) => ctx.Navigate(new Route(PageKind.Project, id));
                chip.IsTabStop = true;
                chip.UseSystemFocusVisuals = true;
                chip.KeyDown += (_, e) =>
                {
                    if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space) ctx.Navigate(new Route(PageKind.Project, id));
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Open {s.Name}");
            }
            else
            {
                Ui.SetTip(chip, $"{s.Projects} {(s.Projects == 1 ? "project" : "projects")}, combined");
            }
            panel.Children.Add(chip);
        }
        return new Border
        {
            Child = panel,
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(0, 16, 0, 0),
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
    }

    /// <summary>The Cost by model panel, with where the rates came from. Shared with a project page.</summary>
    internal static Border CostPanel(UsageReport report, IReadOnlyDictionary<string, UsageCell> perModel, string lead)
    {
        var costChart = new CostByModelChart(perModel);
        var rateNote = report.PricingLastVerified is { } verified
            ? $" Rates from {(report.PricingSource == "built-in" ? "the built-in rate card" : $"`{report.PricingSource}`")}, last verified {Format.DateStamp(verified)}."
            : "";
        var panel = Ui.Panel("Cost by model", lead + rateNote, null,
            costChart.IsEmpty ? Ui.Text("No model usage found.", 15, 400, Palette.TextFaintBrush) : costChart);
        Ui.Rise(panel, 140);
        return panel;
    }

    /// <summary>The Token detail by model panel. Shared with a project page.</summary>
    internal static Border TokenPanel(UsageCore.ProviderMeta meta, IReadOnlyDictionary<string, UsageCell> perModel, UsageCell combined) =>
        Ui.Panel("Token detail by model",
            meta.HasCacheWrites
                ? "Cache writes are split by TTL internally and priced separately; the column below shows their sum."
                : "Input counts only the uncached remainder of each prompt — the cached part is billed at a tenth of the rate and has its own column.",
            null, Parts.ModelBreakdownTable(meta, perModel, combined));

    /// <summary>
    /// The heat map, with the week-on-week trend and, when there is older
    /// history, Expand - to the agent's full history, or with a project id to
    /// that project's. Shared with a project page.
    /// </summary>
    internal static Border ActivityPanel(PageContext ctx, UsageReport report, List<DailyEntry> daily, string? projectId)
    {
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
            expand.Click += (_, _) => ctx.Navigate(new Route(PageKind.Activity, projectId));
        }
        var figure = HeatmapFigure.Build(layout, report.Settings.WeekStartsOn, "in the last 6 months", labelStrips: false, expand);
        // The ramp runs toward "more" against each theme's own ground: brighter
        // on the dark one, darker on the light one.
        return Ui.Panel("Daily activity", $"Spend per day over the last 6 months. {(Palette.IsLight ? "Darker" : "Brighter")} means a more expensive day.", trend, figure);
    }

    public static UIElement Loading()
    {
        var page = new StackPanel { Padding = new Thickness(0, 34, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(page, "Loading the dashboard");
        var ring = new ProgressRing { IsActive = true, Width = 28, Height = 28, Foreground = Palette.Accent, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(ring);
        // The page's own sections, in order: headline, daily spend, cost by
        // model, the Activity title and its cards, the heat map.
        page.Children.Add(Parts.Skeleton(269));
        page.Children.Add(Parts.Skeleton(433));
        page.Children.Add(Parts.Skeleton(393));
        page.Children.Add(Parts.Skeleton(13, 110, 18));
        // The score grid's own column steps, so the placeholder wraps as it does.
        var grid = new FitGrid(width => width > 900 ? 4 : width > 450 ? 2 : 1) { Gap = 14, Margin = new Thickness(0, 0, 0, 26) };
        for (var i = 0; i < 12; i++) grid.Children.Add(Parts.Skeleton(128, bottom: 0));
        page.Children.Add(grid);
        page.Children.Add(Parts.Skeleton(457));
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
