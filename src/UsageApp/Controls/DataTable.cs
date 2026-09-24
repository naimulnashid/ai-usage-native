using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageApp.Theme;

namespace UsageApp.Controls;

/// <summary>A column: its header, and an optional info tip beside it.</summary>
public sealed record Column(string Header, (string Label, string Text)? Tip = null, double? Width = null);

/// <summary>
/// A data table in the dashboard's style: small uppercase headers, figures
/// right-aligned in tabular digits, rows ruled faintly and lit on hover, and a
/// footer row ruled a shade brighter. Wider than its panel, it scrolls sideways
/// rather than squeezing - the same 760px floor the original used.
/// </summary>
public sealed class DataTable
{
    private const double MinWidth = 760;
    private readonly Grid _grid = new();
    private readonly List<Column> _columns;
    private int _row;

    public DataTable(IReadOnlyList<Column> columns)
    {
        _columns = columns.ToList();
        for (var i = 0; i < _columns.Count; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = _columns[i].Width is { } w ? new GridLength(w) : i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }
        AddHeader();
    }

    private void AddHeader()
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(Ui.Caps(column.Header, 12.5, 0.08));
            if (column.Tip is { } tip) head.Children.Add(Ui.InfoTip(tip.Label, tip.Text));
            var cell = new Border
            {
                Padding = new Thickness(14, 11, 14, 11),
                BorderBrush = Palette.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = head,
            };
            Grid.SetColumn(cell, i);
            Grid.SetRow(cell, 0);
            _grid.Children.Add(cell);
        }
        _row = 1;
    }

    /// <summary>A plain text cell.</summary>
    public static TextBlock Cell(string text, Brush? brush = null, int weight = 400, bool numeric = true) =>
        Ui.Text(text, 15.5, weight, brush ?? Palette.TextBrush, numeric: numeric);

    /// <summary>The cost column: accent, semibold.</summary>
    public static TextBlock Cost(string text) => Cell(text, Palette.Accent, 600);

    /// <summary>A swatch and a model name.</summary>
    public static StackPanel ModelCell(string model, bool unpriced = false, bool alignRight = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        panel.Children.Add(Ui.Swatch(Palette.ModelColor(model)));
        panel.Children.Add(Ui.Text(UsageCore.View.Format.Model(model), 15.5, 550));
        if (unpriced) panel.Children.Add(Ui.UnpricedPill());
        return panel;
    }

    /// <summary>
    /// Swatches only - with the names in the automation name, so a screen
    /// reader hears the models rather than an empty cell.
    /// </summary>
    public static StackPanel Swatches(IEnumerable<string> models)
    {
        var list = models.ToList();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var model in list)
        {
            var swatch = Ui.Swatch(Palette.ModelColor(model));
            Ui.SetTip(swatch, UsageCore.View.Format.Model(model));
            panel.Children.Add(swatch);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panel, string.Join(", ", list.Select(UsageCore.View.Format.Model)));
        return panel;
    }

    public void AddRow(IReadOnlyList<UIElement?> cells, bool separatorAbove = false)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = _row++;

        // The hover wash spans the row, behind its cells.
        var wash = new Border
        {
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.RowBorderBrush,
            BorderThickness = new Thickness(0, separatorAbove ? 1 : 0, 0, 1),
        };
        if (separatorAbove) wash.BorderBrush = Palette.BorderBrightBrush;
        Grid.SetRow(wash, row);
        Grid.SetColumnSpan(wash, _columns.Count);
        _grid.Children.Add(wash);
        void Lit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => wash.Background = Palette.SurfaceHoverBrush;
        void Unlit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => wash.Background = Palette.TransparentBrush;
        wash.PointerEntered += Lit;
        wash.PointerExited += Unlit;

        for (var i = 0; i < cells.Count && i < _columns.Count; i++)
        {
            if (cells[i] is not FrameworkElement content) continue;
            content.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            content.VerticalAlignment = VerticalAlignment.Center;
            content.Margin = new Thickness(14, 13, 14, 13);
            // Cells stay hit-testable (a swatch carries a tooltip), so they
            // light the row too.
            content.PointerEntered += Lit;
            content.PointerExited += Unlit;
            Grid.SetColumn(content, i);
            Grid.SetRow(content, row);
            _grid.Children.Add(content);
        }
    }

    public void AddFooter(IReadOnlyList<UIElement?> cells)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = _row++;
        var rule = new Border { BorderBrush = Palette.BorderBrightBrush, BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(rule, row);
        Grid.SetColumnSpan(rule, _columns.Count);
        _grid.Children.Add(rule);
        for (var i = 0; i < cells.Count && i < _columns.Count; i++)
        {
            if (cells[i] is not FrameworkElement content) continue;
            if (content is TextBlock text) text.FontWeight = Fonts.Weight(620);
            content.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            content.Margin = new Thickness(14, 15, 14, 13);
            Grid.SetColumn(content, i);
            Grid.SetRow(content, row);
            _grid.Children.Add(content);
        }
    }

    public int RowCount => _row - 1;

    /// <summary>The table in its sideways scroller.</summary>
    public ScrollViewer Build()
    {
        _grid.MinWidth = MinWidth;
        return new ScrollViewer
        {
            Content = _grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
    }
}
