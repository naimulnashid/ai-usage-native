using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageApp.Charts;
using UsageApp.Controls;
using UsageApp.Theme;
using UsageCore;
using UsageCore.Model;
using UsageCore.View;

namespace UsageApp.Views;

/// <summary>Pieces more than one page draws.</summary>
public static class Parts
{
    public const string RuntimeTip =
        "Runtime = the sum of gaps between consecutive messages in a session, excluding any gap longer than the idle cutoff (maxIdleGapMinutes in settings.json, 30 minutes by default). It measures active session time, not inference time, so it includes your own reading and typing and drops long idle stretches.";

    public const string ReasoningTip =
        "Reasoning tokens are already counted inside Output and billed at the output rate — they are shown separately, not added on top. That is why the columns do not sum to the total.";

    public static (string, string) Runtime => ("About runtime", RuntimeTip);

    /* ------------------------------------------------------------ Headline */

    /// <summary>
    /// The headline card's three columns: the money figure, total tokens, total
    /// runtime. The money figure is as large as it always was UNLESS its own
    /// length would not fit beside the two side columns, and then exactly as
    /// small as it needs - a fixed size overflowed the card at five figures.
    /// </summary>
    public static Grid HeadlineGrid(ProviderMeta meta, double cost, IReadOnlyList<UIElement> costExtras, (string Value, string Sub) tokens, (string Value, string Sub) runtime, UsageCell combined)
    {
        var grid = new Grid { ColumnSpacing = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var first = new StackPanel();
        var label = Ui.Caps(meta.CostLabel);
        label.Margin = new Thickness(0, 0, 0, 10);
        first.Children.Add(label);
        var costText = Format.Usd(cost);
        var value = Ui.Text(costText, 80, 680, Palette.Accent, -0.035, numeric: true);
        value.LineHeight = 0;
        CountUp.Apply(value, cost, v => Format.Usd(v));
        var accent = Palette.AccentColor;
        first.Children.Add(Ui.Glow(value, Windows.UI.Color.FromArgb(150, accent.R, accent.G, accent.B)));
        foreach (var extra in costExtras) first.Children.Add(extra);
        grid.Children.Add(first);

        var tokensCol = SideColumn("Total tokens", null, CountUp.Apply(SideValue(), combined.TotalTokens, Format.Tokens), tokens.Sub);
        Grid.SetColumn(tokensCol, 1);
        grid.Children.Add(tokensCol);
        var runtimeCol = SideColumn("Total runtime", RuntimeTip, CountUp.Apply(SideValue(), combined.RuntimeSeconds, Format.Duration), runtime.Sub);
        Grid.SetColumn(runtimeCol, 2);
        grid.Children.Add(runtimeCol);

        // Size the figure to the card, as the original's container query did.
        grid.SizeChanged += (_, e) =>
        {
            var windowWidth = grid.XamlRoot?.Size.Width ?? 1400;
            var side = Math.Clamp(windowWidth * 0.034, 30, 42);
            var baseSize = Math.Clamp(windowWidth * 0.08, 58, 92);
            var reserve = 2 * 44 + 6.6 * side;
            var fit = (e.NewSize.Width - reserve) / (costText.Length * 0.55);
            value.FontSize = Math.Max(24, Math.Min(baseSize, fit));
            value.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            value.LineHeight = value.FontSize * 1.02;
            foreach (var column in new[] { tokensCol, runtimeCol })
            {
                if (column.Children[1] is TextBlock sideValue) sideValue.FontSize = side;
            }
        };
        return grid;
    }

    private static TextBlock SideValue() => Ui.Text("", 38, 620, Palette.TextBrush, -0.02, numeric: true);

    private static StackPanel SideColumn(string label, string? tip, TextBlock value, string sub)
    {
        var column = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Ui.Caps(label));
        if (tip is not null) head.Children.Add(Ui.InfoTip($"About {label.ToLowerInvariant()[6..]}", tip));
        column.Children.Add(head);
        column.Children.Add(value);
        var subText = Ui.Text(sub, 14.5, 400, Palette.TextFaintBrush, wrap: true);
        subText.Margin = new Thickness(0, 6, 0, 0);
        column.Children.Add(subText);
        return column;
    }

    public static TextBlock HeadlineMeta(string text)
    {
        var meta = Ui.Text(text, 16, 400, Palette.TextMutedBrush, wrap: true);
        meta.Margin = new Thickness(0, 14, 0, 0);
        return meta;
    }

    /* --------------------------------------------------------- Model cards */

    /// <summary>One card per model, dearest first, in the model's own shade.</summary>
    public static FitGrid ModelCards(IReadOnlyDictionary<string, UsageCell> perModel)
    {
        var grid = FitGrid.AutoFit(215, 16);
        grid.Margin = new Thickness(0, 0, 0, 34);
        var index = 0;
        foreach (var (model, cell) in perModel.OrderByDescending(kv => kv.Value.CostUsd))
        {
            var stack = new StackPanel();
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 9) };
            label.Children.Add(Ui.Swatch(Palette.ModelColor(model)));
            label.Children.Add(Ui.Text(Format.Model(model), 13.5, 500, Palette.TextFaintBrush));
            if (cell.Unpriced) label.Children.Add(Ui.UnpricedPill());
            stack.Children.Add(label);
            var value = Ui.Text("", 33, 620, Palette.ModelBrush(model), -0.02, numeric: true);
            if (cell.Unpriced) value.Text = "—";
            else CountUp.Apply(value, cell.CostUsd, v => Format.Usd(v));
            stack.Children.Add(value);
            var sub = Ui.Text($"{Format.Tokens(cell.TotalTokens)} tokens · {Format.Duration(cell.RuntimeSeconds)}", 14, 400, Palette.TextFaintBrush, numeric: true);
            sub.Margin = new Thickness(0, 7, 0, 0);
            stack.Children.Add(sub);
            var card = Ui.Card(stack, new Thickness(26, 24, 26, 24), hover: true);
            Ui.Rise(card, index++ * 55);
            grid.Children.Add(card);
        }
        return grid;
    }

    /* --------------------------------------------------------- Token table */

    /// <summary>
    /// Per-model token detail. Columns follow the agent: cache writes where
    /// they exist and are priced, reasoning where it is reported. A column of
    /// permanent zeroes reads as "we measured nothing", not "this is not a thing here".
    /// </summary>
    public static ScrollViewer ModelBreakdownTable(ProviderMeta meta, IReadOnlyDictionary<string, UsageCell> perModel, UsageCell combined)
    {
        var columns = new List<Column>
        {
            new("Model"),
            new(meta.MessageNoun == "requests" ? "Requests" : "Messages"),
            new("Input"),
            new("Output"),
        };
        if (meta.HasReasoningTokens) columns.Add(new Column("Reasoning", ("About reasoning tokens", ReasoningTip)));
        if (meta.HasCacheWrites) columns.Add(new Column("Cache write"));
        columns.Add(new Column(meta.CacheReadLabel));
        columns.Add(new Column("Total tokens"));
        columns.Add(new Column("Runtime", Runtime));
        columns.Add(new Column("Cost"));
        var table = new DataTable(columns);

        List<UIElement?> Cells(UIElement first, UsageCell cell, bool unpricedDash)
        {
            var cells = new List<UIElement?>
            {
                first,
                DataTable.Cell(Format.Count(cell.Messages)),
                DataTable.Cell(Format.Tokens(cell.Input)),
                DataTable.Cell(Format.Tokens(cell.Output)),
            };
            if (meta.HasReasoningTokens) cells.Add(DataTable.Cell(Format.Tokens(cell.Reasoning ?? 0)));
            if (meta.HasCacheWrites) cells.Add(DataTable.Cell(Format.Tokens(cell.CacheWrite5m + cell.CacheWrite1h)));
            cells.Add(DataTable.Cell(Format.Tokens(cell.CacheRead)));
            cells.Add(DataTable.Cell(Format.Tokens(cell.TotalTokens)));
            cells.Add(DataTable.Cell(Format.Duration(cell.RuntimeSeconds)));
            cells.Add(DataTable.Cost(unpricedDash && cell.Unpriced ? "—" : Format.Usd(cell.CostUsd)));
            return cells;
        }

        foreach (var (model, cell) in perModel.OrderByDescending(kv => kv.Value.CostUsd))
        {
            table.AddRow(Cells(DataTable.ModelCell(model, cell.Unpriced), cell, true));
        }
        table.AddFooter(Cells(DataTable.Cell("All models", numeric: false), combined, false));
        return table.Build();
    }

    /* -------------------------------------------------------- Model legend */

    /// <summary>
    /// The legend under a stacked chart. Every figure beside a share is in the
    /// share's own denominator: token counts under the tokens chart, dollars
    /// under the spend chart. A cost beside a token share reads as a cost share.
    /// Summed over the days SHOWN, never all time.
    /// </summary>
    public static StackPanel ModelLegend(IReadOnlyList<string> models, UsageBucket window, bool spend)
    {
        var legend = new StackPanel { Margin = new Thickness(0, 20, 0, 0), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 0) };
        var rows = models
            .Where(m => window.PerModel.TryGetValue(m, out var c) && (spend ? c.CostUsd > 0 || c.TotalTokens > 0 : c.TotalTokens > 0))
            .ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var model = rows[i];
            var cell = window.PerModel[model];
            var row = new Grid
            {
                ColumnSpacing = 18,
                Padding = new Thickness(4, 10, 4, 10),
                BorderBrush = Palette.RowBorderBrush,
                BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1),
                Background = Palette.TransparentBrush,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 130 });
            if (!spend) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.PointerEntered += (_, _) => row.Background = Palette.SurfaceHoverBrush;
            row.PointerExited += (_, _) => row.Background = Palette.TransparentBrush;

            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            name.Children.Add(Ui.Swatch(Palette.ModelColor(model)));
            name.Children.Add(Ui.Text(Format.Model(model), 16, 570));
            row.Children.Add(name);

            var column = 1;
            if (!spend)
            {
                var detail = Ui.Text($"{Format.Tokens(cell.Input)} in · {Format.Tokens(cell.Output)} out · {Format.Tokens(cell.CacheRead)} cached", 14, 400, Palette.TextFaintBrush, numeric: true);
                detail.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(detail, column++);
                row.Children.Add(detail);
            }
            var denominator = spend ? window.Combined.CostUsd : window.Combined.TotalTokens;
            var share = denominator > 0 ? (spend ? cell.CostUsd : cell.TotalTokens) / denominator * 100 : 0;
            var figure = Ui.Text(spend ? (cell.Unpriced ? "—" : Format.Usd(cell.CostUsd)) : Format.Tokens(cell.TotalTokens), 15.5, 600, Palette.Accent, numeric: true);
            figure.HorizontalAlignment = HorizontalAlignment.Right;
            figure.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(figure, column++);
            row.Children.Add(figure);
            var pct = Ui.Text(spend && cell.Unpriced ? "—" : $"{share:0.0}%", 16, 620, Palette.TextBrush, numeric: true);
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            pct.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(pct, column);
            row.Children.Add(pct);
            legend.Children.Add(row);
        }
        return legend;
    }

    /// <summary>What a daily chart says when its window holds nothing.</summary>
    public static TextBlock NoDaysInRange(IReadOnlyList<DailyEntry> daily, DayRange range)
    {
        string text;
        if (range == DayRange.All) text = "No dated activity found.";
        else
        {
            var days = range == DayRange.Last30 ? 30 : 60;
            var last = DayRanges.LastActiveDate(daily);
            text = $"No activity in the last {days} days." + (last is not null ? $" The most recent was on {Format.DateStamp(last)}." : "");
        }
        var block = Ui.Text(text, 15, 400, Palette.TextFaintBrush, wrap: true);
        block.HorizontalAlignment = HorizontalAlignment.Center;
        block.Margin = new Thickness(0, 40, 0, 40);
        return block;
    }

    /// <summary>A stacked chart plus its legend, for one window of days.</summary>
    public static UIElement StackedWithLegend(IReadOnlyList<DailyEntry> daily, DayRange range, string today, bool spend, bool animate)
    {
        var days = DayRanges.DaysIn(daily, range, today);
        var models = days.SelectMany(d => d.PerModel.Keys).Distinct().OrderBy(m => m, ModelColors.PriceDescending).ToList();
        if (models.Count == 0) return NoDaysInRange(daily, range);
        var chart = spend
            ? new StackedBarChart(days, models, c => c.CostUsd, v => Format.Usd(v, compact: true), v => Format.Usd(v), animate: animate)
            : new StackedBarChart(days, models, c => c.TotalTokens, Format.Tokens, Format.Tokens, animate: animate);
        var stack = new StackPanel();
        stack.Children.Add(chart);
        stack.Children.Add(ModelLegend(models, DayRanges.Sum(days), spend));
        return stack;
    }

    /// <summary>A panel whose body swaps when its range picker changes.</summary>
    public static Border StackedPanel(string title, string subtitle, IReadOnlyList<DailyEntry> daily, string today, bool spend)
    {
        var body = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = StackedWithLegend(daily, DayRange.Last30, today, spend, animate: true),
        };
        var picker = RangePicker.Create($"Days shown in {title}", range => body.Content = StackedWithLegend(daily, range, today, spend, animate: true));
        return Ui.Panel(title, subtitle, picker, body);
    }

    /* ------------------------------------------------------------ Notices */

    /// <summary>A notice box, in the warning style.</summary>
    public static Border Notice(UIElement content)
    {
        var row = new Grid { ColumnSpacing = 13 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = "", FontSize = 16, Foreground = Palette.WarnBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        row.Children.Add(icon);
        Grid.SetColumn((FrameworkElement)content, 1);
        row.Children.Add(content);
        return new Border
        {
            Padding = new Thickness(19, 15, 19, 15),
            CornerRadius = new CornerRadius(Ui.RadiusSmall),
            BorderThickness = new Thickness(1),
            BorderBrush = Palette.WarnBorderBrush,
            Background = Palette.WarnDimBrush,
            Margin = new Thickness(0, 0, 0, 22),
            Child = row,
        };
    }

    /// <summary>Only when a model string has no rate-card entry: tokens counted, cost excluded.</summary>
    public static UIElement? UnpricedNotice(ProviderMeta meta, IReadOnlyList<string> models)
    {
        if (models.Count == 0) return null;
        var one = models.Count == 1;
        var text = Ui.Paragraph(
            $"Unpriced model{(one ? "" : "s")}: {string.Join(" ", models.Select(m => $"`{m}`"))}. Tokens are counted but excluded from cost. Add {(one ? "it" : "them")} to `{Config.RateCardHint(meta)}` to include {(one ? "it" : "them")}.",
            14.5, Palette.TextMutedBrush);
        return Notice(text);
    }

    /// <summary>
    /// The parse's warnings, verbatim. Not "N files could not be read": the same
    /// list carries a merge-rule cycle and an archive that could not be written.
    /// </summary>
    public static UIElement? ParseWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return null;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Ui.Paragraph(
            (warnings.Count == 1 ? "The parse reported a problem" : $"The parse reported {warnings.Count} problems")
            + " and carried on. Anything it could not read is missing from the totals below.", 14.5, Palette.TextMutedBrush));
        foreach (var warning in warnings.Take(4)) stack.Children.Add(Ui.Paragraph("• " + warning, 13.5, Palette.TextMutedBrush));
        return Notice(stack);
    }

    /* ------------------------------------------------------- Empty states */

    /// <summary>
    /// The parse worked and found nothing: say which agent, where it looked,
    /// and how to point it elsewhere - not a page of $0.00.
    /// </summary>
    public static Border EmptyState(ProviderMeta meta, IReadOnlyList<string> warnings)
    {
        var stack = new StackPanel();
        var title = Ui.Text($"No {meta.Label} usage found yet", 21, 620, spacing: -0.015);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        stack.Children.Add(title);
        var lead = Ui.Paragraph($"Nothing was found to read, so there is nothing to show. That is the expected state before you have used {meta.Label} — or if its transcripts live somewhere this app did not look.");
        lead.Margin = new Thickness(0, 5, 0, 20);
        lead.MaxWidth = 620;
        lead.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(lead);

        var facts = new Grid { ColumnSpacing = 18, RowSpacing = 8 };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string, string)[] rows =
        [
            ("Looked in", $"`{meta.TranscriptHint}`"),
            ("Somewhere else?", $"Set `{meta.TranscriptEnvVar}` and restart the app."),
            ("Already used it?", "Hit Refresh — transcripts are read when you ask, and when they change."),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dt = Ui.Text(rows[i].Item1, 14.5, 600, Palette.TextFaintBrush);
            Grid.SetRow(dt, i);
            facts.Children.Add(dt);
            var dd = Ui.Paragraph(rows[i].Item2, 14.5, Palette.TextMutedBrush);
            Grid.SetRow(dd, i);
            Grid.SetColumn(dd, 1);
            facts.Children.Add(dd);
        }
        stack.Children.Add(facts);

        if (warnings.Count > 0)
        {
            var expander = new Expander
            {
                Header = Ui.Text($"What the parser reported ({warnings.Count})", 13.5, 400, Palette.TextFaintBrush),
                Margin = new Thickness(0, 22, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            var list = new StackPanel { Spacing = 4 };
            foreach (var w in warnings.Take(5)) list.Children.Add(Ui.Paragraph("• " + w, 13.5, Palette.TextMutedBrush));
            expander.Content = list;
            stack.Children.Add(expander);
        }
        var card = Ui.Card(stack, new Thickness(30, 30, 30, 26));
        card.Margin = new Thickness(0, 0, 0, 22);
        return card;
    }

    /// <summary>A grey placeholder the shape of the section it stands in for.</summary>
    public static Border Skeleton(double height, double? width = null, double bottom = 22) => new()
    {
        Height = height,
        Width = width ?? double.NaN,
        HorizontalAlignment = width is null ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        CornerRadius = new CornerRadius(Ui.Radius),
        Background = Palette.SurfaceBrush,
        BorderBrush = Palette.BorderBrush,
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 0, bottom),
    };
}

/// <summary>Where the rate cards and project files live, for the copy that names them.</summary>
public static class Config
{
    public static string RateCardHint(ProviderMeta meta) =>
        Path.Combine(AppPaths.ConfigDir, UsageCore.Config.AppConfig.PricingFileName(meta.Id));

    public static string ProjectsHint(ProviderMeta meta) =>
        Path.Combine(AppPaths.ConfigDir, UsageCore.Config.AppConfig.ProjectsFileName(meta.Id));
}
