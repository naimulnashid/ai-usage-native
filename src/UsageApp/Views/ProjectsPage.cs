using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using UsageApp.Charts;
using UsageApp.Controls;
using UsageApp.Theme;
using UsageCore;
using UsageCore.Config;
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

        var colors = ctx.Colors.For(meta.Id, report.Projects);
        var donutPanel = Ui.Panel("Share of spend",
            "The largest projects as slices of the total, each in its own colour — its logo's, or one you pick from its ⋯ menu. Anything past the top nine is summed into Others. Click a slice or a name to open the project.",
            null, ShareChart(report.Projects, total, hidden, colors));
        Ui.Rise(donutPanel);
        page.Children.Add(donutPanel);

        // Hidden projects leave the LIST only; every total still counts them.
        var hiddenCount = report.Projects.Count(p => hidden.Contains(p.Id));
        var expanded = _showAll && hiddenCount > 0;
        var listed = expanded ? report.Projects : report.Projects.Where(p => !hidden.Contains(p.Id)).ToList();
        var own = ProjectSettings.Load(meta.Id);
        for (var i = 0; i < listed.Count; i++)
        {
            var card = ProjectCard(meta, listed[i], total, hidden.Contains(listed[i].Id), colors[listed[i].Id], report.Projects, own);
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

    private UIElement ShareChart(List<ProjectSummary> projects, double total, IReadOnlySet<string> hiddenIds, Dictionary<string, (string Hex, ProjectColorSource Source)> colors)
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

        var slices = shown.Select((p, i) => new DonutSlice(p.Id, p.Name, p.Combined.CostUsd, p.Combined.TotalTokens, p.Combined.CostUsd / total * 100, Palette.Hex(colors[p.Id].Hex), false, 1, 0)).ToList();
        if (rest.Count > 0)
        {
            var cost = rest.Sum(p => p.Combined.CostUsd);
            slices.Add(new DonutSlice("__others__", hidden.Count == rest.Count ? "Hidden" : "Others", cost, rest.Sum(p => p.Combined.TotalTokens), cost / total * 100, Palette.OthersColor, true, rest.Count, hidden.Count));
        }

        var donut = new DonutChart(slices, total, ranked.Count);
        donut.SliceClicked += id => ctx.Navigate(new Route(PageKind.Project, id));

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
            if (!slice.Remainder)
            {
                // A row opens its project, as its slice does. handledEventsToo:
                // the selectable name marks a tap as handled.
                row.AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, _) => ctx.Navigate(new Route(PageKind.Project, id))), handledEventsToo: true);
                row.IsTabStop = true;
                row.UseSystemFocusVisuals = true;
                row.KeyDown += (_, e) =>
                {
                    if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space) ctx.Navigate(new Route(PageKind.Project, id));
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"Open {slice.Name}, {Format.Usd(slice.Cost)}, {slice.Share:0.0}%");
            }
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

    private Border ProjectCard(ProviderMeta meta, ProjectSummary project, double total, bool isHidden, (string Hex, ProjectColorSource Source) color,
        List<ProjectSummary> all, ProjectConfig own)
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
        right.Children.Add(Menu(meta, project, isHidden, color, all, own));
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
    /// The card's options: its colour, its name, merging it into another project
    /// (and separating what was merged into it), hiding it from this list (a
    /// view preference - its spend still counts everywhere), and its logo.
    /// </summary>
    /// <param name="own">The merges and names set from this page, which are the
    /// ones it can undo; <c>projects.json</c>'s are edited there.</param>
    private Button Menu(ProviderMeta meta, ProjectSummary project, bool isHidden, (string Hex, ProjectColorSource Source) color,
        List<ProjectSummary> all, ProjectConfig own)
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
        var recolour = new MenuFlyoutItem { Text = "Change colour…", Icon = new FontIcon { Glyph = "\uE790", Foreground = new SolidColorBrush(Palette.Hex(color.Hex)) } };
        Ui.SetTip(recolour, "How it is drawn in the charts. " + SourceNote(color.Source));
        // After the menu has closed: a flyout cannot open from inside another's Click.
        recolour.Click += (_, _) => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => ColorEditor(meta, project, color).ShowAt(button));
        flyout.Items.Add(recolour);
        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new FontIcon { Glyph = "\uE8AC" } };
        Ui.SetTip(rename, "The name shown in this app. Nothing on disk is renamed.");
        rename.Click += (_, _) => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => RenameEditor(meta, project, own).ShowAt(button));
        flyout.Items.Add(rename);
        var others = all.Where(p => p.Id != project.Id).ToList();
        if (others.Count > 0)
        {
            var mergeInto = new MenuFlyoutItem { Text = "Merge into…", Icon = new FontIcon { Glyph = "\uE71B" } };
            Ui.SetTip(mergeInto, "Counts it as part of another project, under that one's name.");
            mergeInto.Click += (_, _) => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => MergeEditor(meta, project, others).ShowAt(button));
            flyout.Items.Add(mergeInto);
        }
        // Only what this page merged can be separated here.
        var separable = project.MergedFrom.Where(source => own.Merge.ContainsKey(source)).ToList();
        foreach (var source in separable)
        {
            var unmerge = new MenuFlyoutItem
            {
                Text = separable.Count == 1 ? "Unmerge" : $"Unmerge {source}",
                Icon = new FontIcon { Glyph = "\uE8C6" },
            };
            Ui.SetTip(unmerge, $"Lists {source} as a project of its own again. Days whose transcripts are already gone stay with {project.Name}.");
            unmerge.Click += (_, _) => Guard(() =>
            {
                ProjectSettings.SaveMerge(meta.Id, source, null);
                _ = ctx.State.RefreshAsync(meta.Id);
            });
            flyout.Items.Add(unmerge);
        }
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

    private static string SourceNote(ProjectColorSource source) => source switch
    {
        ProjectColorSource.Custom => "Chosen by you.",
        ProjectColorSource.Logo => "Taken from its logo.",
        _ => "Picked automatically - it has no logo.",
    };

    /// <summary>
    /// The colour editor: WinUI's own colour picker, which carries a hex field
    /// for entering one exactly, then Save / Reset / Cancel. A save redraws; it
    /// never re-parses, since a colour is a view preference.
    /// </summary>
    private Flyout ColorEditor(ProviderMeta meta, ProjectSummary project, (string Hex, ProjectColorSource Source) color)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var root = new StackPanel { Spacing = 10, Width = 300 };
        root.Children.Add(Ui.Text($"Colour for {project.Name}", 14, 600));
        var picker = new ColorPicker
        {
            Color = Palette.Hex(color.Hex),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Box,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, $"Colour for {project.Name}");
        root.Children.Add(picker);
        var note = SourceNote(color.Source);
        if (color.Source == ProjectColorSource.Custom) note += " Reset goes back to the logo colour, or an automatic one.";
        root.Children.Add(Ui.Paragraph(note, 13));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = Ui.Button("Save", primary: true, fontSize: 14, padding: new Thickness(13, 6, 13, 6));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(save, "Save colour");
        save.Click += (_, _) =>
        {
            var c = picker.Color;
            flyout.Hide();
            Guard(() =>
            {
                ctx.Colors.Save(meta.Id, project.Id, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
                Rebuild();
            });
        };
        actions.Children.Add(save);
        if (color.Source == ProjectColorSource.Custom)
        {
            var reset = Ui.Button("Reset", fontSize: 14, padding: new Thickness(13, 6, 13, 6));
            reset.Click += (_, _) =>
            {
                flyout.Hide();
                Guard(() =>
                {
                    ctx.Colors.Save(meta.Id, project.Id, null);
                    Rebuild();
                });
            };
            actions.Children.Add(reset);
        }
        var cancel = Ui.Button("Cancel", fontSize: 14, padding: new Thickness(13, 6, 13, 6));
        cancel.Click += (_, _) => flyout.Hide();
        actions.Children.Add(cancel);
        root.Children.Add(actions);
        flyout.Content = root;
        return flyout;
    }

    /// <summary>
    /// The project's display name. A label only: the folder, the project's id
    /// and its history stay as they are, and its colour and hidden state are
    /// kept by id, so they survive a rename. Its logo is matched by this name.
    /// </summary>
    private Flyout RenameEditor(ProviderMeta meta, ProjectSummary project, ProjectConfig own)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var root = new StackPanel { Spacing = 10, Width = 340 };
        root.Children.Add(Ui.Text("Display name", 14, 600));
        var box = new TextBox
        {
            Text = project.Name,
            MaxLength = ProjectSettings.MaxNameLength,
            FontFamily = Fonts.Sans,
            FontSize = 15,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Display name for {project.Name}");
        root.Children.Add(box);
        root.Children.Add(Ui.Paragraph(
            $"Only this app's label changes - the folder ({project.Cwd ?? project.Id}) is left as it is. Logos are matched by this name.", 13));
        var error = Ui.Text("", 13.5, 400, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;

        void Save(string? name)
        {
            try
            {
                ProjectSettings.SaveName(meta.Id, project.Id, name);
                flyout.Hide();
                _ = ctx.State.RefreshAsync(meta.Id);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
            }
        }

        var small = new Thickness(13, 6, 13, 6);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = Ui.Button("Save", primary: true, fontSize: 14, padding: small);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(save, "Save name");
        save.Click += (_, _) => Save(box.Text);
        actions.Children.Add(save);
        if (own.DisplayNames.ContainsKey(project.Id))
        {
            var reset = Ui.Button("Reset", fontSize: 14, padding: small);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reset, "Reset name");
            Ui.SetTip(reset, "Goes back to the name taken from its folder.");
            reset.Click += (_, _) => Save(null);
            actions.Children.Add(reset);
        }
        var cancel = Ui.Button("Cancel", fontSize: 14, padding: small);
        cancel.Click += (_, _) => flyout.Hide();
        actions.Children.Add(cancel);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Save(box.Text);
        };
        root.Children.Add(actions);
        root.Children.Add(error);
        flyout.Content = root;
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return flyout;
    }

    /// <summary>
    /// Folds this project into another. Its usage, sessions and history join
    /// the target's, under the target's name; no total changes. Undone from the
    /// target's menu.
    /// </summary>
    private Flyout MergeEditor(ProviderMeta meta, ProjectSummary project, List<ProjectSummary> others)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var root = new StackPanel { Spacing = 10, Width = 380 };
        root.Children.Add(Ui.Text($"Merge {project.Name} into", 14, 600));
        var picker = new ComboBox
        {
            FontFamily = Fonts.Sans,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Choose a project",
        };
        picker.Resources["ComboBoxDropDownBackground"] = Palette.TooltipBgBrush;
        picker.Resources["ComboBoxDropDownBorderBrush"] = Palette.BorderBrightBrush;
        foreach (var other in others.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // Two projects can share a name; the path tells them apart.
            var item = new StackPanel();
            item.Children.Add(Ui.Text(other.Name, 14, 550, selectable: false));
            item.Children.Add(Ui.Text(other.Cwd ?? other.Id, 12.5, 400, Palette.TextFaintBrush, selectable: false));
            var entry = new ComboBoxItem { Content = item, Tag = other.Id };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(entry, $"{other.Name}, {other.Cwd ?? other.Id}");
            picker.Items.Add(entry);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, $"Project to merge {project.Name} into");
        root.Children.Add(picker);
        root.Children.Add(Ui.Paragraph(
            "Its spend, sessions and history become part of the chosen project, under that project's name. Totals do not change, and nothing on disk is touched. Unmerge it from the chosen project's ⋯ menu; days whose transcripts are already gone stay merged.", 13));
        var error = Ui.Text("", 13.5, 400, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;

        var small = new Thickness(13, 6, 13, 6);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var merge = Ui.Button("Merge", primary: true, fontSize: 14, padding: small);
        merge.IsEnabled = false;
        picker.SelectionChanged += (_, _) => merge.IsEnabled = picker.SelectedItem is ComboBoxItem;
        merge.Click += (_, _) =>
        {
            if (picker.SelectedItem is not ComboBoxItem { Tag: string target }) return;
            try
            {
                ProjectSettings.SaveMerge(meta.Id, project.Id, target);
                flyout.Hide();
                _ = ctx.State.RefreshAsync(meta.Id);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
            }
        };
        actions.Children.Add(merge);
        var cancel = Ui.Button("Cancel", fontSize: 14, padding: small);
        cancel.Click += (_, _) => flyout.Hide();
        actions.Children.Add(cancel);
        root.Children.Add(actions);
        root.Children.Add(error);
        flyout.Content = root;
        return flyout;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _ = Shell.ShowMessage(ctx.Window, "Could not save", ex.Message);
        }
    }
}
