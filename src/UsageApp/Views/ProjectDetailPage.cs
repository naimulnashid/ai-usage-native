using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageApp.Charts;
using UsageApp.Controls;
using UsageApp.Theme;
using UsageCore;
using UsageCore.Model;
using UsageCore.Parsing;
using UsageCore.View;

namespace UsageApp.Views;

/// <summary>
/// One project: the overview's sections in the overview's order, at project
/// scope - without the Activity cards' "when" row and the Model prices table -
/// then what only a project has, its day-by-model table.
/// </summary>
public sealed class ProjectDetailPage(PageContext ctx, string projectId) : IPage
{
    public UIElement Build()
    {
        var meta = ctx.State.Meta;
        var data = ctx.State.Current;
        var page = new StackPanel { Padding = new Thickness(0, 26, 0, 0) };
        page.Children.Add(Ui.Link("← All projects", () => ctx.Navigate(new Route(PageKind.Projects))));

        if (data.Report is null)
        {
            if (data.Error is not null && !data.Loading) return OverviewPage.ErrorView(meta, data.Error);
            // The page's own sections, in order: headline, daily spend, cost by
            // model, the Activity title and its two rows of cards, the heat map.
            page.Children.Add(Parts.Skeleton(359));
            page.Children.Add(Parts.Skeleton(433));
            page.Children.Add(Parts.Skeleton(393));
            page.Children.Add(Parts.Skeleton(13, 110, 18));
            var grid = new FitGrid(width => width > 900 ? 4 : width > 450 ? 2 : 1) { Gap = 14, Margin = new Thickness(0, 0, 0, 26) };
            for (var i = 0; i < ScoreCards.WithoutWhen; i++) grid.Children.Add(Parts.Skeleton(128, bottom: 0));
            page.Children.Add(grid);
            page.Children.Add(Parts.Skeleton(457));
            return page;
        }

        var report = data.Report;
        var project = report.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project is null)
        {
            var notice = Parts.Notice(Ui.Paragraph($"Project not found. No project with id `{projectId}` in the current scan.", 14.5, Palette.TextMutedBrush));
            notice.Margin = new Thickness(0, 16, 0, 22);
            page.Children.Add(notice);
            return page;
        }

        var combined = project.Combined;
        var daily = project.Daily;
        var dated = daily.Where(d => Dates.IsDayKey(d.Date)).ToList();
        var peak = daily.Count == 0 ? null : daily.Aggregate((best, d) => d.Combined.CostUsd > best.Combined.CostUsd ? d : best);
        var activeDays = daily.Count(d => d.Combined.CostUsd > 0);
        var avgPerDay = activeDays > 0 ? combined.CostUsd / activeDays : 0;
        var share = report.Global.Combined.CostUsd > 0 ? combined.CostUsd / report.Global.Combined.CostUsd * 100 : 0;

        // ---- Headline -----------------------------------------------------------
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 26) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        nameRow.Children.Add(ProjectLogoView.Create(project.Name, ctx.Logos.PathFor(meta.Id, project.Name), 52));
        var title = Ui.Text(project.Name, 32, 640, spacing: -0.025);
        title.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        nameRow.Children.Add(title);
        head.Children.Add(nameRow);
        var path = Ui.Text(project.Cwd ?? project.Id, 14, 400, Palette.TextFaintBrush, wrap: true);
        path.Margin = new Thickness(0, 6, 0, 0);
        head.Children.Add(path);
        if (project.MergedFrom.Count > 0)
        {
            var merged = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            merged.Children.Add(Ui.Paragraph("Includes usage merged from " + string.Join(", ", project.MergedFrom.Select(m => $"`{m}`")), 13.5, Palette.TextMutedBrush));
            merged.Children.Add(Ui.InfoTip("About merged projects", $"{meta.Label} keys projects by working directory, so renaming or moving a folder starts a new project and splits its history. These directories are stitched back together by a merge - set from a project's ⋯ menu on the Projects page, or in {Config.ProjectsHint(meta)}."));
            head.Children.Add(merged);
        }

        var headline = new StackPanel();
        headline.Children.Add(head);
        // The agent's own label: on Codex this figure is API-equivalent, not a bill.
        headline.Children.Add(Parts.HeadlineGrid(meta, combined.CostUsd,
            [Parts.HeadlineMeta($"{share:0.0}% of all spend · {Format.Usd(avgPerDay)} avg / active day")],
            (Format.Tokens(combined.TotalTokens), $"{Format.Count(combined.Messages)} {meta.MessageNoun} · {Format.Count(project.Sessions.Count)} sessions"),
            (Format.Duration(combined.RuntimeSeconds), $"across {Format.Count(activeDays)} active days"),
            combined));
        var headlineCard = Ui.Card(headline, new Thickness(44, 40, 44, 40));
        headlineCard.Margin = new Thickness(0, 16, 0, 22);
        Ui.Rise(headlineCard);
        page.Children.Add(headlineCard);

        var unpricedHere = project.PerModel.Where(kv => kv.Value.Unpriced).Select(kv => kv.Key).ToList();
        if (Parts.UnpricedNotice(meta, unpricedHere) is { } unpriced) page.Children.Add(unpriced);

        // ---- The overview's sections, in the overview's order --------------------
        // Same panels, same names, this project's data - so the two pages read
        // as one page at two scopes. What only a project has comes after.
        UIElement? peakBox = null;
        if (peak is not null)
        {
            var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var caption = Ui.Text("Peak day", 13, 400, Palette.TextFaintBrush);
            caption.HorizontalAlignment = HorizontalAlignment.Right;
            box.Children.Add(caption);
            var line = new TextBlock { FontFamily = Fonts.Sans, FontSize = 16, FontWeight = Fonts.Weight(600), Foreground = Palette.TextBrush, HorizontalAlignment = HorizontalAlignment.Right };
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Format.DateLong(peak.Date) + " " });
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Format.Usd(peak.Combined.CostUsd), Foreground = Palette.Accent });
            box.Children.Add(line);
            peakBox = box;
        }
        var spend = Ui.Panel("Daily combined spend", "All models, this project. Days marked in red sit well above trend.", peakBox,
            dated.Count > 0 ? new SpendAreaChart(dated) : Parts.NoDaysInRange(daily, DayRange.All));
        Ui.Rise(spend, 100);
        page.Children.Add(spend);

        page.Children.Add(OverviewPage.CostPanel(report, project.PerModel, "Estimated spend per model in this project."));

        if (project.Activity is { } activity)
        {
            page.Children.Add(Ui.SectionTitle("Activity"));
            page.Children.Add(ScoreCards.Build(meta, activity, combined, showWhen: false));
        }
        page.Children.Add(OverviewPage.ActivityPanel(ctx, report, daily, project.Id));

        var today = DayRanges.Today(report);
        page.Children.Add(Parts.StackedPanel("Daily tokens by model",
            "Stacked by model, most expensive at the bottom — so the darker the base of a column, the more of that day went on premium tokens.",
            daily, today, spend: false, initial: DayRange.All));
        page.Children.Add(Parts.StackedPanel("Daily spend by model",
            "The same columns priced instead of counted — so a day that looks modest above and tall here went on the expensive models.",
            daily, today, spend: true, initial: DayRange.All));
        page.Children.Add(OverviewPage.TokenPanel(meta, project.PerModel, combined));

        // ---- This project's own table ------------------------------------------------
        page.Children.Add(Ui.Panel("Daily breakdown by model", "One row per day and model, newest first.", null, ModelDailyTable(meta, daily)));
        return page;
    }

    /// <summary>One row per (day, model), newest first; each new day ruled a shade brighter.</summary>
    private static UIElement ModelDailyTable(ProviderMeta meta, List<DailyEntry> daily)
    {
        var rows = daily.OrderByDescending(d => d.Date, Comparer<string>.Create(Dates.CompareKeys))
            .SelectMany(d => d.PerModel.Where(kv => kv.Value.TotalTokens > 0 || kv.Value.RuntimeSeconds > 0)
                .OrderByDescending(kv => kv.Value.CostUsd)
                .Select(kv => (d.Date, Model: kv.Key, Cell: kv.Value)))
            .ToList();
        if (rows.Count == 0) return Ui.Text("No per-model activity to break down.", 15, 400, Palette.TextFaintBrush);

        var columns = new List<Column>
        {
            new("Date"), new("Model"), new(meta.MessageNoun == "requests" ? "Requests" : "Messages"), new("Input"), new("Output"),
        };
        if (meta.HasCacheWrites) columns.Add(new Column("Cache write"));
        columns.Add(new Column(meta.CacheReadLabel));
        columns.Add(new Column("Runtime", Parts.Runtime));
        columns.Add(new Column("Cost"));
        var table = new DataTable(columns);
        for (var i = 0; i < rows.Count; i++)
        {
            var (date, model, cell) = rows[i];
            var newDay = i == 0 || rows[i - 1].Date != date;
            var cells = new List<UIElement?>
            {
                DataTable.Cell(newDay ? Format.DateLong(date) : "", newDay ? Palette.TextBrush : Palette.TextFaintBrush, numeric: false),
                DataTable.ModelCell(model, cell.Unpriced, alignRight: true),
                DataTable.Cell(Format.Count(cell.Messages)),
                DataTable.Cell(Format.Tokens(cell.Input)),
                DataTable.Cell(Format.Tokens(cell.Output)),
            };
            if (meta.HasCacheWrites) cells.Add(DataTable.Cell(Format.Tokens(cell.CacheWrite5m + cell.CacheWrite1h)));
            cells.Add(DataTable.Cell(Format.Tokens(cell.CacheRead)));
            cells.Add(DataTable.Cell(Format.Duration(cell.RuntimeSeconds)));
            cells.Add(DataTable.Cost(cell.Unpriced ? "—" : Format.Usd(cell.CostUsd)));
            table.AddRow(cells, separatorAbove: newDay && i > 0);
        }
        return table.Build();
    }
}
