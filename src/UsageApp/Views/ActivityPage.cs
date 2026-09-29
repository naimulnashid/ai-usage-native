using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageApp.Charts;
using UsageApp.Theme;
using UsageCore.View;

namespace UsageApp.Views;

/// <summary>
/// The heat map over every recorded day: the six-month strip repeated
/// downwards, oldest first, so more history makes the page taller rather than
/// the cells smaller. For the agent (from the overview) or, with a project id,
/// for one project (from its page) - the same page at two scopes.
/// </summary>
public sealed class ActivityPage(PageContext ctx, string? projectId = null) : IPage
{
    public UIElement Build()
    {
        var meta = ctx.State.Meta;
        var data = ctx.State.Current;
        var page = new StackPanel { Padding = new Thickness(0, 26, 0, 0) };
        var project = projectId is null ? null : data.Report?.Projects.FirstOrDefault(p => p.Id == projectId);
        page.Children.Add(projectId is null
            ? Ui.Link("← Overview", () => ctx.Navigate(new Route(PageKind.Overview)))
            : Ui.Link($"← {project?.Name ?? "Project"}", () => ctx.Navigate(new Route(PageKind.Project, projectId))));

        if (data.Report is null)
        {
            if (data.Error is not null && !data.Loading) return OverviewPage.ErrorView(meta, data.Error);
            page.Children.Add(Parts.Skeleton(457));
            return page;
        }
        var report = data.Report;
        if (ReportState.IsEmpty(report))
        {
            page.Children.Add(Parts.EmptyState(meta, report.Diagnostics.Warnings));
            return page;
        }

        if (projectId is not null && project is null)
        {
            var notice = Parts.Notice(Ui.Paragraph($"Project not found. No project with id `{projectId}` in the current scan.", 14.5, Palette.TextMutedBrush));
            notice.Margin = new Thickness(0, 16, 0, 22);
            page.Children.Add(notice);
            return page;
        }

        var daily = project?.Daily ?? report.Daily;
        var from = Heatmap.FullHistoryStart(daily);
        UIElement body = from is null
            ? Ui.Text("No dated activity found.", 15, 400, Palette.TextFaintBrush)
            : HeatmapFigure.Build(
                Heatmap.Full(daily, from, Heatmap.Today(report), Heatmap.FirstDay(report.Settings.WeekStartsOn)),
                report.Settings.WeekStartsOn,
                $"since {Format.DateStamp(from)}",
                labelStrips: true,
                action: null);
        var panel = Ui.Panel(project is null ? "Daily activity" : $"Daily activity — {project.Name}",
            from is null ? "Spend per day. Brighter means a more expensive day." : $"Spend per day since {Format.DateStamp(from)}, six months to a row. Brighter means a more expensive day.",
            null, body, titleIsPage: true);
        panel.Margin = new Thickness(0, 16, 0, 22);
        page.Children.Add(panel);
        return page;
    }
}
