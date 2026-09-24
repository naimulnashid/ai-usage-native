using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using UsageApp.Charts;
using UsageApp.Controls;
using UsageApp.Theme;
using UsageCore;
using UsageCore.Model;
using UsageCore.Parsing;
using UsageCore.View;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace UsageApp.Views;

/// <summary>
/// The projects ranked by spend, under a share-of-spend donut that answers the
/// one question a list cannot: is this one project or twelve?
/// </summary>
public sealed class ProjectsPage(PageContext ctx) : IPage
{
    /// <summary>Only the top nine get a slice; past that the ring is slivers.</summary>
    private const int MaxSlices = 9;

    private bool _showAll;

    public UIElement Build()
    {
        var meta = ctx.State.Meta;
        var data = ctx.State.Current;
        if (data.Report is null)
        {
            if (data.Error is not null && !data.Loading) return OverviewPage.ErrorView(meta, data.Error);
            var loading = new StackPanel { Padding = new Thickness(0, 34, 0, 0) };
            loading.Children.Add(Parts.Skeleton(458));
            for (var i = 0; i < 5; i++) loading.Children.Add(Parts.Skeleton(177, bottom: 14));
            return loading;
        }

        var report = data.Report;
        var page = new StackPanel { Padding = new Thickness(0, 34, 0, 0) };
        if (ReportState.IsEmpty(report))
        {
            page.Children.Add(Parts.EmptyState(meta, report.Diagnostics.Warnings));
            return page;
        }
        if (report.Projects.Count == 0)
        {
            page.Children.Add(Ui.Panel("No projects to show",
                "This agent recorded usage, but none of it could be attributed to a working directory. The Overview still has the totals.",
                null, new Border(), titleIsPage: true));
            return page;
        }

        var total = report.Global.Combined.CostUsd;
        var hidden = ctx.State.Hidden.For(meta.Id);

        var donutPanel = Ui.Panel("Share of spend",
            "The largest projects as slices of the total, shaded by rank — so the ring reads in the same order as the list below. Anything past the top nine is summed into Others.",
            null, ShareChart(report.Projects, total, hidden));
        Ui.Rise(donutPanel);
        page.Children.Add(donutPanel);

        // Hidden projects leave the LIST only; every total still counts them.
        var hiddenCount = report.Projects.Count(p => hidden.Contains(p.Id));
        var expanded = _showAll && hiddenCount > 0;
        var listed = expanded ? report.Projects : report.Projects.Where(p => !hidden.Contains(p.Id)).ToList();
        for (var i = 0; i < listed.Count; i++)
        {
            var card = ProjectCard(meta, listed[i], total, hidden.Contains(listed[i].Id));
            Ui.Rise(card, i * 40);
            page.Children.Add(card);
        }

        if (listed.Count == 0)
        {
            var note = Ui.Text(report.Projects.Count == 1 ? "Your only project is hidden." : $"All {Format.Count(report.Projects.Count)} projects are hidden.", 14.5, 400, Palette.TextFaintBrush);
            note.Margin = new Thickness(0, 4, 0, 14);
            page.Children.Add(note);
        }
        if (hiddenCount > 0)
        {
            var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 6, 0, 0) };
            var toggle = Ui.Button(expanded ? "Show fewer projects" : "Show all projects");
            toggle.Click += (_, _) =>
            {
                _showAll = !_showAll;
                Rebuild();
            };
            more.Children.Add(toggle);
            var count = Ui.Text($"{Format.Count(hiddenCount)} hidden", 14, 400, Palette.TextFaintBrush);
            count.VerticalAlignment = VerticalAlignment.Center;
            more.Children.Add(count);
            page.Children.Add(more);
        }
        return page;
    }

    private void Rebuild() => ctx.Navigate(new Route(PageKind.Projects));

    /* --------------------------------------------------------------- Donut */

    private UIElement ShareChart(List<ProjectSummary> projects, double total, IReadOnlySet<string> hiddenIds)
    {
        var ranked = projects.Where(p => p.Combined.CostUsd > 0).OrderByDescending(p => p.Combined.CostUsd).ToList();
        if (ranked.Count == 0 || total <= 0) return new Border();

        var listed = ranked.Where(p => !hiddenIds.Contains(p.Id)).ToList();
        var hidden = ranked.Where(p => hiddenIds.Contains(p.Id)).ToList();
        // At exactly ten, "Others" would stand for one project: show them all.
        // A hidden project forces the remainder, and its slot comes out of the
        // named ones, so the legend never passes ten rows.
        var collapse = hidden.Count > 0 || listed.Count > MaxSlices + 1;
        var shown = collapse ? listed.Take(MaxSlices).ToList() : listed;
        var rest = collapse ? listed.Skip(MaxSlices).Concat(hidden).ToList() : [];

        var slices = shown.Select((p, i) => new DonutSlice(p.Id, p.Name, p.Combined.CostUsd, p.Combined.TotalTokens, p.Combined.CostUsd / total * 100, Palette.RankColor(i, shown.Count), false, 1, 0)).ToList();
        if (rest.Count > 0)
        {
            var cost = rest.Sum(p => p.Combined.CostUsd);
            slices.Add(new DonutSlice("__others__", hidden.Count == rest.Count ? "Hidden" : "Others", cost, rest.Sum(p => p.Combined.TotalTokens), cost / total * 100, Palette.OthersColor, true, rest.Count, hidden.Count));
        }

        var donut = new DonutChart(slices, total, ranked.Count);

        // The legend: multi-column, filled top to bottom, so the left column is
        // the top of the ranking in order - the same order as the list below.
        var rows = new List<Grid>();
        foreach (var slice in slices)
        {
            var row = new Grid { ColumnSpacing = 11, Padding = new Thickness(2, 6, 8, 6), Background = Palette.TransparentBrush, Tag = slice.Id };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(170) };
            row.Children.Add(Ui.Swatch(slice.Fill, 10));
            // A monogram here would draw "O" and read as a project called Others.
            UIElement mark = slice.Remainder
                ? Center(Ui.Text($"+{slice.Projects}", 12.5, 620, Palette.TextFaintBrush, numeric: true))
                : ProjectLogoView.Create(slice.Name, ctx.Logos.PathFor(ctx.State.Provider, slice.Name), 22);
            Grid.SetColumn((FrameworkElement)mark, 1);
            row.Children.Add(mark);
            var name = Ui.Text(slice.Name, 14.5, 550);
            name.VerticalAlignment = VerticalAlignment.Center;
            Ui.SetTip(name, slice.Name);
            Grid.SetColumn(name, 2);
            row.Children.Add(name);
            var cost = Ui.Text(Format.Usd(slice.Cost), 14, 400, Palette.TextMutedBrush, numeric: true);
            cost.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cost, 3);
            row.Children.Add(cost);
            var share = Ui.Text($"{slice.Share:0.0}%", 14.5, 620, numeric: true);
            share.HorizontalAlignment = HorizontalAlignment.Right;
            share.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(share, 4);
            row.Children.Add(share);
            var id = slice.Id;
            row.PointerEntered += (_, _) => { row.Background = Palette.SurfaceHoverBrush; donut.SetActive(id); Dim(rows, id); };
            row.PointerExited += (_, _) => { row.Background = Palette.TransparentBrush; donut.SetActive(null); Dim(rows, null); };
            rows.Add(row);
        }
        donut.ActiveChanged += id => Dim(rows, id);

        // Column-major: fill the left column first.
        var columns = rows.Count > 5 ? 2 : 1;
        var perColumn = (int)Math.Ceiling(rows.Count / (double)columns);
        var left = new StackPanel();
        var right = new StackPanel();
        for (var i = 0; i < rows.Count; i++) (i < perColumn ? left : right).Children.Add(rows[i]);
        var legendGrid = new Grid { ColumnSpacing = 26, VerticalAlignment = VerticalAlignment.Center };
        legendGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (right.Children.Count > 0) legendGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        legendGrid.Children.Add(left);
        if (right.Children.Count > 0)
        {
            Grid.SetColumn(right, 1);
            legendGrid.Children.Add(right);
        }

        var layout = new Grid { ColumnSpacing = 30 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(donut);
        Grid.SetColumn(legendGrid, 1);
        layout.Children.Add(legendGrid);
        return layout;
    }

    private static void Dim(List<Grid> rows, string? active)
    {
        foreach (var row in rows)
        {
            row.Opacity = active is null || (string)row.Tag == active ? 1 : 0.34;
            if ((string)row.Tag == active) row.Background = Palette.SurfaceHoverBrush;
            else if (active is null) row.Background = Palette.TransparentBrush;
        }
    }

    private static FrameworkElement Center(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    /* --------------------------------------------------------- Project card */

    private Border ProjectCard(ProviderMeta meta, ProjectSummary project, double total, bool isHidden)
    {
        var share = total > 0 ? project.Combined.CostUsd / total * 100 : 0;
        var content = new StackPanel();

        var top = new Grid { ColumnSpacing = 20 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        identity.Children.Add(ProjectLogoView.Create(project.Name, ctx.Logos.PathFor(meta.Id, project.Name)));
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        nameLine.Children.Add(Ui.Text(project.Name, 20, 620, spacing: -0.015));
        if (isHidden) nameLine.Children.Add(Ui.Pill("Hidden", Palette.TextMutedBrush, Palette.BorderBrightBrush));
        names.Children.Add(nameLine);
        var path = Ui.Text(project.Cwd ?? project.Id, 13.5, 400, Palette.TextFaintBrush);
        path.Margin = new Thickness(0, 4, 0, 0);
        Ui.SetTip(path, project.Cwd ?? project.Id);
        names.Children.Add(path);
        identity.Children.Add(names);
        top.Children.Add(identity);

        var figures = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cost = CountUp.Apply(Ui.Text("", 30, 640, Palette.Accent, numeric: true), project.Combined.CostUsd, v => Format.Usd(v));
        cost.HorizontalAlignment = HorizontalAlignment.Right;
        figures.Children.Add(cost);
        var pct = Ui.Text($"{share:0.0}% of total", 13.5, 400, Palette.TextFaintBrush, numeric: true);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        figures.Children.Add(pct);
        Grid.SetColumn(figures, 1);
        top.Children.Add(figures);
        content.Children.Add(top);

        // A share bar segmented by each model's share of the project's tokens.
        var bar = new Grid { Height = 7, CornerRadius = new CornerRadius(3.5), Background = Palette.TrackBrush, Margin = new Thickness(0, 16, 0, 13) };
        foreach (var (model, cell) in project.PerModel.OrderByDescending(kv => kv.Value.TotalTokens))
        {
            var part = project.Combined.TotalTokens > 0 ? (double)cell.TotalTokens / project.Combined.TotalTokens : 0;
            if (part <= 0) continue;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(part, GridUnitType.Star) });
            var segment = new Border { Background = Palette.ModelBrush(model) };
            Ui.SetTip(segment, $"{Format.Model(model)} · {part * 100:0.0}%");
            Grid.SetColumn(segment, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(segment);
        }
        content.Children.Add(bar);

        var stats = new Grid();
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22 };
        left.Children.Add(Ui.Text($"{Format.Tokens(project.Combined.TotalTokens)} tokens", 14.5, 400, Palette.TextMutedBrush, numeric: true));
        var runtime = new StackPanel { Orientation = Orientation.Horizontal };
        runtime.Children.Add(Ui.Text(Format.Duration(project.Combined.RuntimeSeconds), 14.5, 400, Palette.TextMutedBrush, numeric: true));
        runtime.Children.Add(Ui.InfoTip("About runtime", Parts.RuntimeTip));
        left.Children.Add(runtime);
        left.Children.Add(Ui.Text(Plural(project.Sessions.Count, "session"), 14.5, 400, Palette.TextMutedBrush, numeric: true));
        left.Children.Add(Ui.Text(Plural(project.Daily.Count(d => Dates.IsDayKey(d.Date)), "active day"), 14.5, 400, Palette.TextMutedBrush, numeric: true));
        stats.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        right.Children.Add(Ui.Text("View breakdown →", 14.5, 550, Palette.Accent));
        right.Children.Add(Menu(meta, project, isHidden));
        Grid.SetColumn(right, 1);
        stats.Children.Add(right);
        content.Children.Add(stats);

        var card = Ui.Card(content, new Thickness(26, 22, 26, 22), hover: true);
        card.Margin = new Thickness(0, 0, 0, 14);
        if (isHidden)
        {
            // Marked by a pill and a brighter edge, never by fading: most of a
            // card's text is TextFaint, which fails contrast the moment it dims.
            card.BorderBrush = Palette.BorderBrightBrush;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"{project.Name}, {Format.Usd(project.Combined.CostUsd)}, {share:0.0}% of total");
        // handledEventsToo: selectable text marks a tap as handled, which made
        // clicking the project's NAME do nothing. A tap still opens the project
        // wherever it lands; a drag across the text is not a tap, so selecting
        // and copying still works.
        card.AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInsideButton(source, card)) return;
            ctx.Navigate(new Route(PageKind.Project, project.Id));
        }), handledEventsToo: true);
        card.IsTabStop = true;
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space) ctx.Navigate(new Route(PageKind.Project, project.Id));
        };
        EnableLogoDrop(card, meta, project);
        return card;
    }

    private static string Plural(int count, string noun) => $"{Format.Count(count)} {noun}{(count == 1 ? "" : "s")}";

    private static bool IsInsideButton(DependencyObject source, DependencyObject stop)
    {
        for (var current = source; current is not null && current != stop; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase) return true;
        }
        return false;
    }

    /// <summary>
    /// The card's options: hide it from this list (a view preference - its spend
    /// still counts everywhere), and its logo.
    /// </summary>
    private Button Menu(ProviderMeta meta, ProjectSummary project, bool isHidden)
    {
        var dots = new FontIcon { Glyph = "", FontSize = 14, Foreground = Palette.TextFaintBrush };
        var button = new Button
        {
            Content = dots,
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.TransparentBrush,
            CornerRadius = new CornerRadius(7),
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.BorderBrightBrush;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Options for {project.Name}");

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var hide = new MenuFlyoutItem
        {
            Text = isHidden ? "Show in project list" : "Hide from project list",
            Icon = new FontIcon { Glyph = isHidden ? "" : "" },
        };
        Ui.SetTip(hide, isHidden ? "Lists it again. Nothing else changes." : "Its spend still counts in every total.");
        hide.Click += (_, _) => Guard(() =>
        {
            ctx.State.Hidden.Set(meta.Id, project.Id, !isHidden);
            Rebuild();
        });
        flyout.Items.Add(hide);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var setLogo = new MenuFlyoutItem { Text = "Set logo…", Icon = new FontIcon { Glyph = "" } };
        setLogo.Click += async (_, _) => await PickLogo(meta, project);
        flyout.Items.Add(setLogo);
        if (ctx.Logos.PathFor(meta.Id, project.Name) is not null)
        {
            var remove = new MenuFlyoutItem { Text = "Remove logo", Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => Guard(() => ctx.Logos.RemoveLogo(meta.Id, project.Name));
            flyout.Items.Add(remove);
        }
        var open = new MenuFlyoutItem { Text = "Open logos folder", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => Shell.OpenFolder(AppPaths.LogoDir(meta.Id));
        flyout.Items.Add(open);
        button.Flyout = flyout;
        return button;
    }

    private async Task PickLogo(ProviderMeta meta, ProjectSummary project)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail };
        foreach (var ext in ProjectLogos.Extensions) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(ctx.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        Guard(() => ctx.Logos.SetLogo(meta.Id, project.Name, file.Path));
    }

    /// <summary>Drop an image on a card to make it that project's logo.</summary>
    private void EnableLogoDrop(Border card, ProviderMeta meta, ProjectSummary project)
    {
        card.AllowDrop = true;
        card.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Set as the logo for {project.Name}";
            card.BorderBrush = Palette.Accent;
        };
        card.DragLeave += (_, _) => card.BorderBrush = Palette.BorderBrush;
        card.Drop += async (_, e) =>
        {
            card.BorderBrush = Palette.BorderBrush;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(f => Array.IndexOf(ProjectLogos.Extensions, Path.GetExtension(f.Path).ToLowerInvariant()) >= 0);
            if (file is not null) Guard(() => ctx.Logos.SetLogo(meta.Id, project.Name, file.Path));
        };
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = Shell.ShowMessage(ctx.Window, "Could not save", ex.Message);
        }
    }
}
