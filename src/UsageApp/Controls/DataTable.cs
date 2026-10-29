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

    // Reordering (EnableReorder): each data row's wash and cells, and the drag in progress.
    private Action<int, int>? _moved;
    private readonly List<Border> _washes = [];
    private readonly List<List<UIElement>> _rowElements = [];
    private Border? _dropLine;
    private int _dragFrom = -1;
    private int _dragTo = -1;
    private double _dragStartY;
    private (double Top, double Height)[] _dragRows = [];

    /// <summary>The row whose grip takes focus once the page is rebuilt after a keyboard move.</summary>
    private static int? _focusRow;

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

    /// <summary>
    /// Lets the rows be dragged into a new order by a grip at the start of each.
    /// Call before adding rows. <paramref name="moved"/> gets the row's index and
    /// the index it was dropped at, both counted in data rows, and only for a
    /// drop that changes the order.
    /// </summary>
    /// <remarks>
    /// Pointer capture on the grip rather than WinUI's drag and drop: that one
    /// drags a picture of the grip alone and needs a data package, where this
    /// lifts the row itself and moves it with the pointer, for mouse, pen and
    /// touch alike. The rest of the row stays as it was, so its text can still be
    /// selected and its buttons pressed.
    /// </remarks>
    public void EnableReorder(Action<int, int> moved) => _moved = moved;

    public void AddRow(IReadOnlyList<UIElement?> cells, bool separatorAbove = false)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = _row++;
        var index = _washes.Count;
        var elements = new List<UIElement>();

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
        _washes.Add(wash);
        elements.Add(wash);
        // A lifted row stays lit, wherever the pointer has gone.
        void Lit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => wash.Background = Palette.SurfaceHoverBrush;
        void Unlit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_dragFrom != index) wash.Background = Palette.TransparentBrush;
        }
        wash.PointerEntered += Lit;
        wash.PointerExited += Unlit;

        for (var i = 0; i < cells.Count && i < _columns.Count; i++)
        {
            if (cells[i] is not FrameworkElement content) continue;
            var margin = new Thickness(14, 13, 14, 13);
            if (i == 0 && _moved is not null)
            {
                var withGrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                withGrip.Children.Add(Grip(index));
                content.VerticalAlignment = VerticalAlignment.Center;
                withGrip.Children.Add(content);
                content = withGrip;
                margin.Left = 4;
            }
            content.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            content.VerticalAlignment = VerticalAlignment.Center;
            content.Margin = margin;
            // Cells stay hit-testable (a swatch carries a tooltip), so they
            // light the row too.
            content.PointerEntered += Lit;
            content.PointerExited += Unlit;
            Grid.SetColumn(content, i);
            Grid.SetRow(content, row);
            _grid.Children.Add(content);
            elements.Add(content);
        }
        _rowElements.Add(elements);
    }

    /// <summary>Six dots, the usual mark of a handle.</summary>
    private FrameworkElement Grip(int index)
    {
        var dots = new Canvas { Width = 10, Height = 16 };
        var shapes = new List<Microsoft.UI.Xaml.Shapes.Ellipse>();
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 2; c++)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 3.4, Height = 3.4, Fill = Palette.TextFaintBrush };
                Canvas.SetLeft(dot, 1 + c * 5);
                Canvas.SetTop(dot, 1.3 + r * 5);
                dots.Children.Add(dot);
                shapes.Add(dot);
            }
        }
        // A target wider than its dots, and hit-testable across all of it.
        var grip = new Grid
        {
            Width = 24,
            Height = 32,
            Background = Palette.TransparentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dots },
        };
        dots.HorizontalAlignment = HorizontalAlignment.Center;
        dots.VerticalAlignment = VerticalAlignment.Center;
        Ui.SetTip(grip, "Drag, or use the Up and Down keys, to reorder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grip, "Reorder: Up and Down keys move this row");

        // The keyboard's way to do the same: the grip takes focus, and the
        // arrow keys move its row one place (Home and End to either end).
        grip.IsTabStop = true;
        grip.UseSystemFocusVisuals = true;
        grip.GotFocus += (_, _) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            grip, $"Reorder: row {index + 1} of {_washes.Count}. Up and Down keys move it.");
        grip.KeyDown += (_, e) =>
        {
            var to = e.Key switch
            {
                Windows.System.VirtualKey.Up => index - 1,
                Windows.System.VirtualKey.Down => index + 1,
                Windows.System.VirtualKey.Home => 0,
                Windows.System.VirtualKey.End => _washes.Count - 1,
                _ => index,
            };
            if (to == index || to < 0 || to >= _washes.Count || _dragFrom >= 0 || _moved is not { } moved) return;
            e.Handled = true;
            // The callback rebuilds the page; the moved row's new grip takes focus back.
            _focusRow = to;
            _grid.DispatcherQueue.TryEnqueue(() => moved(index, to));
        };
        grip.Loaded += (_, _) =>
        {
            if (_focusRow != index) return;
            _focusRow = null;
            grip.Focus(FocusState.Keyboard);
        };
        void Shade(Brush brush)
        {
            foreach (var dot in shapes) dot.Fill = brush;
        }
        grip.PointerEntered += (_, _) => Shade(Palette.TextMutedBrush);
        grip.PointerExited += (_, _) =>
        {
            if (_dragFrom != index) Shade(Palette.TextFaintBrush);
        };

        grip.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(_grid);
            if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
            if (_dragFrom >= 0 || !grip.CapturePointer(e.Pointer)) return;
            BeginDrag(index, point.Position.Y);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_dragFrom != index) return;
            DragTo(e.GetCurrentPoint(_grid).Position.Y);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (_dragFrom != index) return;
            var (from, to) = (_dragFrom, _dragTo);
            EndDrag();
            Shade(Palette.TextFaintBrush);
            grip.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            // After the handler: the callback rebuilds the page this grip is on.
            if (to != from && _moved is { } moved) _grid.DispatcherQueue.TryEnqueue(() => moved(from, to));
        };
        grip.PointerCaptureLost += (_, _) =>
        {
            if (_dragFrom != index) return;
            EndDrag();
            Shade(Palette.TextFaintBrush);
        };
        return grip;
    }

    private void BeginDrag(int index, double y)
    {
        _dragFrom = _dragTo = index;
        _dragStartY = y;
        // Where every row sits before anything moves; Translation leaves layout alone.
        _dragRows = _washes.Select(w => ((double)w.ActualOffset.Y, w.ActualHeight)).ToArray();
        foreach (var element in _rowElements[index]) Canvas.SetZIndex(element, 10);
        _washes[index].Background = Palette.SurfaceHoverBrush;
        if (_dropLine is null)
        {
            _dropLine = new Border
            {
                Height = 2,
                Background = Palette.Accent,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                RenderTransform = new TranslateTransform(),
            };
            Canvas.SetZIndex(_dropLine, 20);
            Grid.SetRow(_dropLine, 1);
            Grid.SetColumnSpan(_dropLine, _columns.Count);
            _grid.Children.Add(_dropLine);
        }
    }

    private void DragTo(double y)
    {
        var from = _dragFrom;
        var (top, height) = _dragRows[from];
        // The row moves with the pointer but never leaves the table's rows.
        var first = _dragRows[0].Top;
        var last = _dragRows[^1].Top + _dragRows[^1].Height;
        var dy = Math.Clamp(y - _dragStartY, first - top, last - (top + height));
        foreach (var element in _rowElements[from]) element.Translation = new System.Numerics.Vector3(0, (float)dy, 0);

        // It lands below every other row whose middle its own middle has passed.
        var middle = top + height / 2 + dy;
        var to = 0;
        for (var i = 0; i < _dragRows.Length; i++)
        {
            if (i != from && _dragRows[i].Top + _dragRows[i].Height / 2 < middle) to++;
        }
        _dragTo = to;

        if (_dropLine is null) return;
        if (to == from)
        {
            _dropLine.Visibility = Visibility.Collapsed;
            return;
        }
        var lineY = to < from ? _dragRows[to].Top : _dragRows[to].Top + _dragRows[to].Height;
        ((TranslateTransform)_dropLine.RenderTransform).Y = lineY - first - 1;
        _dropLine.Visibility = Visibility.Visible;
    }

    private void EndDrag()
    {
        if (_dragFrom < 0) return;
        foreach (var element in _rowElements[_dragFrom])
        {
            element.Translation = System.Numerics.Vector3.Zero;
            Canvas.SetZIndex(element, 0);
        }
        _washes[_dragFrom].Background = Palette.TransparentBrush;
        if (_dropLine is not null) _dropLine.Visibility = Visibility.Collapsed;
        _dragFrom = _dragTo = -1;
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
