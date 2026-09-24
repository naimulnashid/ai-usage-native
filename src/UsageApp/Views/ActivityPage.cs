using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageApp.Charts;
using UsageApp.Theme;
using UsageCore.View;

namespace UsageApp.Views;

/// <summary>
/// The heat map over every recorded day: the overview's six-month strip
/// repeated downwards, oldest first, so more history makes the page taller
/// rather than the cells smaller.
/// </summary>
public sealed class ActivityPage(PageContext ctx) : IPage
{
    public UIElement Build()
    {
        var meta = ctx.State.Meta;
        var data = ctx.State.Current;
        var page = new StackPanel { Padding = new Thickness(0, 26, 0, 0) };
        page.Children.Add(Ui.Link("← Overview", () => ctx.Navigate(new Route(PageKind.Overview))));

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

        var from = Heatmap.FullHistoryStart(report.Daily);
        UIElement body = from is null
            ? Ui.Text("No dated activity found.", 15, 400, Palette.TextFaintBrush)
            : HeatmapFigure.Build(
                Heatmap.Full(report.Daily, from, Heatmap.Today(report), Heatmap.FirstDay(report.Settings.WeekStartsOn)),
                report.Settings.WeekStartsOn,
                $"since {Format.DateStamp(from)}",
                labelStrips: true,
                action: null);
        var panel = Ui.Panel("Daily activity",
            from is null ? "Spend per day. Brighter means a more expensive day." : $"Spend per day since {Format.DateStamp(from)}, six months to a row. Brighter means a more expensive day.",
            null, body, titleIsPage: true);
        panel.Margin = new Thickness(0, 16, 0, 22);
        page.Children.Add(panel);
        return page;
    }
}
