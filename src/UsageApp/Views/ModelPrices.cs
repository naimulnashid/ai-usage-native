using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UsageApp.Controls;
using UsageApp.Theme;
using UsageCore;
using UsageCore.Config;
using UsageCore.Model;
using UsageCore.View;

namespace UsageApp.Views;

/// <summary>
/// What each model is priced at, and the one place a price or a colour is set.
/// </summary>
/// <remarks>
/// <para>A model the rate card has never heard of - a new release, which turns
/// up in the transcripts before the card is updated - is listed as UNPRICED
/// with a "Set price" button. The rate goes into the app's own settings file
/// (<see cref="ModelSettings"/>), wins over the card, and reaches every figure
/// through a fresh parse, archived days included.</para>
/// <para>The same editor picks a model's colour from the agent's own shades.
/// That changes nothing but the view, so it saves and redraws at once.</para>
/// </remarks>
public static class ModelPrices
{
    public const string Subtitle =
        "USD per million tokens. Set a price for a model the rate card does not know yet — it is saved on this computer and wins over the card — or pick the colour a model is drawn in.";

    public static UIElement Table(PageContext ctx, IReadOnlyDictionary<string, UsageCell> perModel, IReadOnlyDictionary<string, ModelRateInfo>? rates)
    {
        var meta = ctx.State.Meta;
        var rows = perModel.Keys
            .Select(model => (Model: model, Info: rates?.GetValueOrDefault(model) ?? new ModelRateInfo(null, RateSource.None, null, false)))
            // Dearest first, like every legend; equal prices fall back to the ramp.
            .OrderByDescending(r => r.Info.Rate?.Output ?? -1)
            .ThenBy(r => r.Model, ModelColors.PriceDescending)
            .ToList();
        if (rows.Count == 0) return Ui.Text("No models found.", 15, 400, Palette.TextFaintBrush);

        var columns = new List<Column> { new("Model"), new("Input") };
        if (meta.HasCacheWrites)
        {
            columns.Add(new Column("Write 5m"));
            columns.Add(new Column("Write 1h"));
        }
        columns.Add(new Column(meta.CacheReadLabel));
        columns.Add(new Column("Output"));
        columns.Add(new Column("Source"));
        columns.Add(new Column(""));
        var table = new DataTable(columns);

        static string Price(double? value) => value is { } v ? "$" + Rate(v) : "—";
        foreach (var (model, info) in rows)
        {
            var rate = info.Rate;
            var cells = new List<UIElement?>
            {
                DataTable.ModelCell(model, info.Source == RateSource.None),
                DataTable.Cell(Price(rate?.Input)),
            };
            if (meta.HasCacheWrites)
            {
                cells.Add(DataTable.Cell(Price(rate?.CacheWrite5m)));
                cells.Add(DataTable.Cell(Price(rate?.CacheWrite1h)));
            }
            cells.Add(DataTable.Cell(Price(rate?.CacheRead)));
            cells.Add(DataTable.Cell(Price(rate?.Output)));
            cells.Add(DataTable.Cell(SourceLabel(info), Palette.TextMutedBrush, numeric: false));

            var edit = Ui.Button(info.Source == RateSource.None ? "Set price" : "Edit", fontSize: 14, padding: new Thickness(13, 5, 13, 5));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, $"{(info.Source == RateSource.None ? "Set price" : "Edit")} {Format.Model(model)}");
            edit.Flyout = Editor(ctx, model, info);
            cells.Add(edit);
            table.AddRow(cells);
        }

        // The rates alone no longer give the cost when a model has a long-context
        // tier, so it is said under them - once per rule, naming its models.
        var tiers = rows
            .Where(r => r.Info.Rate?.LongContext is not null)
            .GroupBy(r => r.Info.Rate!.LongContext!)
            .ToList();
        if (tiers.Count == 0) return table.Build();
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(table.Build());
        foreach (var tier in tiers)
        {
            stack.Children.Add(Ui.Text(LongContextSentence(tier.Key, tier.Select(r => Format.Model(r.Model)).ToList()), 13, 400, Palette.TextFaintBrush, wrap: true));
        }
        return stack;
    }

    /// <summary>"GPT-5.6 Sol and GPT-6.1 Sol: a request whose prompt is over 272K tokens is billed at ..."</summary>
    public static string LongContextSentence(LongContextRule rule, IReadOnlyList<string> models)
    {
        var above = rule.AboveInputTokens >= 1000 ? $"{Rate(rule.AboveInputTokens / 1000d)}K" : rule.AboveInputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var names = models.Count > 1 ? $"{string.Join(", ", models.Take(models.Count - 1))} and {models[^1]}" : models[0];
        return $"{names}: a request whose prompt is over {above} tokens is billed at {Rate(rule.InputMultiplier)}× the input and cache rates and {Rate(rule.OutputMultiplier)}× output.";
    }

    private static string SourceLabel(ModelRateInfo info) => info.Source switch
    {
        RateSource.Card => "Rate card",
        RateSource.Custom => "Yours",
        RateSource.Alias => $"As {Format.Model(info.AliasOf ?? "")}",
        _ => "None",
    };

    /// <summary>Up to four decimals, no trailing zeros: 0.175, 3.75, 25.</summary>
    private static string Rate(double value) => Math.Round(value, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static Flyout Editor(PageContext ctx, string model, ModelRateInfo info)
    {
        var meta = ctx.State.Meta;
        var provider = meta.Id;
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Palette.TooltipBgBrush));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Palette.BorderBrightBrush));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(22, 20, 22, 20)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(Ui.RadiusSmall)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 720d));
        flyout.FlyoutPresenterStyle = style;

        var root = new StackPanel { Spacing = 14 };
        var error = Ui.Text("", 14, 400, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;
        void Fail(Exception ex)
        {
            error.Text = ex.Message;
            error.Visibility = Visibility.Visible;
        }

        // ---- Price -----------------------------------------------------------
        root.Children.Add(Ui.Text($"Price for {model}, USD per million tokens", 13.5, 600, Palette.TextMutedBrush));
        var fields = new List<(string Key, NumberBox Box)>();
        var fieldRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        (string Key, string Label, double? Value)[] specs = meta.HasCacheWrites
            ?
            [
                ("input", "Input", info.Rate?.Input), ("cacheWrite5m", "Write 5m", info.Rate?.CacheWrite5m),
                ("cacheWrite1h", "Write 1h", info.Rate?.CacheWrite1h), ("cacheRead", meta.CacheReadLabel, info.Rate?.CacheRead),
                ("output", "Output", info.Rate?.Output),
            ]
            : [("input", "Input", info.Rate?.Input), ("cacheRead", meta.CacheReadLabel, info.Rate?.CacheRead), ("output", "Output", info.Rate?.Output)];
        foreach (var (key, label, value) in specs)
        {
            var box = new NumberBox
            {
                Width = 112,
                Minimum = 0,
                Maximum = ModelSettings.MaxRate,
                Value = value ?? double.NaN,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                FontFamily = Fonts.Sans,
                FontSize = 15,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"{label} rate");
            var column = new StackPanel { Spacing = 5 };
            column.Children.Add(Ui.Caps(label, 12, 0.06));
            column.Children.Add(box);
            fieldRow.Children.Add(column);
            fields.Add((key, box));
        }
        root.Children.Add(fieldRow);

        // A rate the editor does not show - cache writes, for an agent that hides
        // that column - keeps its current value rather than being saved as 0.
        double Field(string key) => fields.FirstOrDefault(f => f.Key == key).Box?.Value ?? key switch
        {
            "cacheWrite5m" => info.Rate?.CacheWrite5m ?? 0,
            "cacheWrite1h" => info.Rate?.CacheWrite1h ?? 0,
            _ => 0,
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var small = new Thickness(13, 6, 13, 6);
        var save = Ui.Button("Save price", primary: true, fontSize: 14, padding: small);
        save.Click += (_, _) =>
        {
            if (fields.Any(f => double.IsNaN(f.Box.Value) || f.Box.Value < 0))
            {
                Fail(new ArgumentException("Every rate needs a number, 0 or more."));
                return;
            }
            var rate = new ModelRate(Field("input"), Field("cacheWrite5m"), Field("cacheWrite1h"), Field("cacheRead"), Field("output"));
            try
            {
                ModelSettings.Save(provider, model, rate: rate);
                flyout.Hide();
                _ = ctx.State.RefreshAsync(provider);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Fail(ex);
            }
        };
        actions.Children.Add(save);

        if (meta.HasCacheWrites)
        {
            var fill = Ui.Button("Cache rates from input", fontSize: 14, padding: small);
            fill.Click += (_, _) =>
            {
                var input = Field("input");
                if (double.IsNaN(input) || input < 0)
                {
                    Fail(new ArgumentException("Enter an input rate first."));
                    return;
                }
                // The usual multipliers off base input.
                foreach (var (key, box) in fields)
                {
                    if (key == "cacheWrite5m") box.Value = Math.Round(input * 1.25, 4);
                    if (key == "cacheWrite1h") box.Value = Math.Round(input * 2, 4);
                    if (key == "cacheRead") box.Value = Math.Round(input * 0.1, 4);
                }
            };
            actions.Children.Add(fill);
        }

        if (info.Source == RateSource.Custom)
        {
            var reset = Ui.Button(info.OnCard ? "Reset to rate card" : "Remove price", fontSize: 14, padding: small);
            reset.Click += (_, _) =>
            {
                try
                {
                    ModelSettings.Save(provider, model, clearRate: true);
                    flyout.Hide();
                    _ = ctx.State.RefreshAsync(provider);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    Fail(ex);
                }
            };
            actions.Children.Add(reset);
        }

        var cancel = Ui.Button("Cancel", fontSize: 14, padding: small);
        cancel.Click += (_, _) => flyout.Hide();
        actions.Children.Add(cancel);
        root.Children.Add(actions);

        // ---- Colour ----------------------------------------------------------
        var colourHead = Ui.Text("Colour", 13.5, 600, Palette.TextMutedBrush);
        colourHead.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(colourHead);
        var chosen = ModelSettings.Load(provider).Colors.GetValueOrDefault(model);
        var current = chosen ?? ModelColors.Hex(model);
        var palette = ModelColors.Palettes[provider];
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var i = 0; i < palette.Count; i++)
        {
            var shade = palette[i];
            var selected = string.Equals(shade, current, StringComparison.OrdinalIgnoreCase);
            var swatch = new Button
            {
                Width = 30,
                Height = 30,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                // Stored as the dark shade; drawn as the theme's twin of it.
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Palette.PaletteShade(shade)),
                BorderBrush = selected ? Palette.TextBrush : Palette.TransparentBrush,
                BorderThickness = new Thickness(2),
            };
            swatch.Resources["ButtonBackgroundPointerOver"] = swatch.Background;
            swatch.Resources["ButtonBackgroundPressed"] = swatch.Background;
            swatch.Resources["ButtonBorderBrushPointerOver"] = Palette.TextMutedBrush;
            var position = i == 0 ? ", deepest" : i == palette.Count - 1 ? ", lightest" : "";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"Shade {i + 1} of {palette.Count}{position}{(selected ? ", selected" : "")}");
            swatch.Click += (_, _) => SaveColour(shade, clear: false);
            swatches.Children.Add(swatch);
        }
        root.Children.Add(swatches);

        if (chosen is not null)
        {
            var reset = Ui.Button("Default colour", fontSize: 14, padding: small);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Click += (_, _) => SaveColour(null, clear: true);
            root.Children.Add(reset);
        }
        var note = Ui.Text("Deeper shades usually mean dearer models — the charts stack the dearest at the bottom.", 13, 400, Palette.TextFaintBrush, wrap: true);
        root.Children.Add(note);
        root.Children.Add(error);

        void SaveColour(string? shade, bool clear)
        {
            try
            {
                var saved = ModelSettings.Save(provider, model, color: shade, clearColor: clear);
                Palette.SetModelColors(saved.Colors);
                flyout.Hide();
                ctx.Redraw();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Fail(ex);
            }
        }

        flyout.Content = root;
        return flyout;
    }
}
