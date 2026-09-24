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

/// <summary>One project: its headline, daily spend and tables, its models, and every session.</summary>
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
            page.Children.Add(Parts.Skeleton(359));
            page.Children.Add(Parts.Skeleton(413));
            page.Children.Add(Parts.Skeleton(560));
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
            merged.Children.Add(Ui.InfoTip("About merged projects", $"{meta.Label} keys projects by working directory, so renaming or moving a folder starts a new project and splits its history. These directories are stitched back together by {Config.ProjectsHint(meta)}."));
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

        // ---- Daily total: chart, then table ------------------------------------
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
        page.Children.Add(Ui.Panel("Daily total spend", "All models combined for this project. Days marked in red sit well above trend.", peakBox,
            dated.Count > 0 ? new SpendAreaChart(dated, 280) : Parts.NoDaysInRange(daily, DayRange.All)));
        page.Children.Add(Ui.Panel("Daily totals, combined", "Newest first. The bar shows each day against the peak.", null,
            CombinedDailyTable(meta, daily, peak?.Combined.CostUsd ?? 0)));

        // ---- Per model ------------------------------------------------------------
        page.Children.Add(Ui.SectionTitle("Breakdown by model"));
        page.Children.Add(Parts.ModelCards(project.PerModel));
        var today = DayRanges.Today(report);
        page.Children.Add(Parts.StackedPanel("Daily tokens by model",
            "Stacked by model, most expensive at the bottom — so the darker the base of a column, the more of that day went on premium tokens.",
            daily, today, spend: false));
        page.Children.Add(Parts.StackedPanel("Daily spend by model",
            "Stacked, so the height of each column is that day's combined total.",
            daily, today, spend: true));
        page.Children.Add(Ui.Panel("Totals by model", null, null, Parts.ModelBreakdownTable(meta, project.PerModel, combined)));
        page.Children.Add(Ui.Panel("Daily breakdown by model", "One row per day and model, newest first.", null, ModelDailyTable(meta, daily)));

        // ---- Sessions ---------------------------------------------------------------
        var sessions = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        sessions.Content = SessionsTable(meta, project.Sessions, expanded: false, sessions);
        page.Children.Add(Ui.Panel("Sessions", "Every transcript file in this project, ranked by cost.", null, sessions));
        return page;
    }

    /// <summary>One row per day, all models summed - "what did this cost me each day".</summary>
    private static UIElement CombinedDailyTable(ProviderMeta meta, List<DailyEntry> daily, double peakCost)
    {
        var table = new DataTable(
        [
            new Column("Date"),
            new Column("Models"),
            new Column(meta.MessageNoun == "requests" ? "Requests" : "Messages"),
            new Column("Tokens"),
            new Column("Runtime", Parts.Runtime),
            new Column("Cost"),
            new Column("Share of peak day", Width: 170),
        ]);
        var rows = daily.OrderByDescending(d => d.Date, Comparer<string>.Create(Dates.CompareKeys)).ToList();
        foreach (var row in rows)
        {
            var share = peakCost > 0 ? row.Combined.CostUsd / peakCost : 0;
            // Radius is half the height: WinUI does not clamp an oversized
            // radius the way CSS does, and 999 drew pointed ends.
            var track = new Grid { Height = 7, CornerRadius = new CornerRadius(3.5), Background = Palette.TrackBrush, Width = 142 };
            track.Children.Add(new Border { Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 142 * share, CornerRadius = new CornerRadius(3.5, 0, 0, 3.5) });
            table.AddRow(
            [
                DataTable.Cell(Format.DateLong(row.Date), weight: 550, numeric: false),
                DataTable.Swatches(row.PerModel.Where(kv => kv.Value.TotalTokens > 0).Select(kv => kv.Key)),
                DataTable.Cell(Format.Count(row.Combined.Messages)),
                DataTable.Cell(Format.Tokens(row.Combined.TotalTokens)),
                DataTable.Cell(Format.Duration(row.Combined.RuntimeSeconds)),
                DataTable.Cost(Format.Usd(row.Combined.CostUsd)),
                track,
            ]);
        }
        table.AddFooter(
        [
            DataTable.Cell($"{rows.Count} days", numeric: false),
            null,
            DataTable.Cell(Format.Count(rows.Sum(r => r.Combined.Messages))),
            DataTable.Cell(Format.Tokens(rows.Sum(r => r.Combined.TotalTokens))),
            DataTable.Cell(Format.Duration(rows.Sum(r => r.Combined.RuntimeSeconds))),
            DataTable.Cost(Format.Usd(rows.Sum(r => r.Combined.CostUsd))),
            null,
        ]);
        return table.Build();
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

    private const int InitialSessions = 12;

    private static UIElement SessionsTable(ProviderMeta meta, List<SessionSummary> sessions, bool expanded, ContentControl host)
    {
        if (sessions.Count == 0) return Ui.Text("No sessions recorded.", 15, 400, Palette.TextFaintBrush);
        var table = new DataTable(
        [
            new Column("Session"),
            new Column("Models"),
            new Column(meta.MessageNoun == "requests" ? "Requests" : "Messages"),
            new Column("Tokens"),
            new Column("Runtime", Parts.Runtime),
            new Column("Open span", ("About open span", "First-to-last timestamp of the session file. Includes idle time, so it is always at least the runtime figure - shown for comparison only.")),
            new Column("Cost"),
        ]);
        foreach (var session in expanded ? sessions : sessions.Take(InitialSessions))
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            // Codex keeps a thread name; where there is one it is the better
            // label, with the id alongside so the row can be traced to its file.
            if (session.Title is { } threadName)
            {
                var name = Ui.Text(threadName, 15.5, 550);
                name.MaxWidth = 260;
                Ui.SetTip(name, threadName);
                label.Children.Add(name);
            }
            var id = Ui.Mono(session.SessionId.Length > 8 ? session.SessionId[..8] : session.SessionId, 13.5);
            id.VerticalAlignment = VerticalAlignment.Center;
            label.Children.Add(id);
            if (session.IsSubagent)
            {
                var pill = Ui.Pill(meta.SubagentNoun, Palette.TextFaintBrush, Palette.BorderBrightBrush);
                if (session.SubagentKind is { } kind) Ui.SetTip(pill, $"Spawned by {meta.Label} as a \"{kind}\" subagent. Billed separately, and counted in these totals.");
                label.Children.Add(pill);
            }
            table.AddRow(
            [
                label,
                DataTable.Swatches(session.Models),
                DataTable.Cell(Format.Count(session.Messages)),
                DataTable.Cell(Format.Tokens(session.TotalTokens)),
                DataTable.Cell(Format.Duration(session.RuntimeSeconds)),
                DataTable.Cell(Format.Duration(session.SpanSeconds), Palette.TextFaintBrush),
                DataTable.Cost(Format.Usd(session.CostUsd)),
            ]);
        }
        var stack = new StackPanel();
        stack.Children.Add(table.Build());
        if (sessions.Count > InitialSessions)
        {
            var toggle = Ui.Button(expanded ? "Show fewer" : $"Show all {Format.Count(sessions.Count)} sessions");
            toggle.Margin = new Thickness(0, 16, 0, 0);
            toggle.Click += (_, _) => host.Content = SessionsTable(meta, sessions, !expanded, host);
            stack.Children.Add(toggle);
        }
        return stack;
    }
}
