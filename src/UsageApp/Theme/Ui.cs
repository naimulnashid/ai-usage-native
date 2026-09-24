using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace UsageApp.Theme;

/// <summary>
/// Element builders for the design system. The pages are assembled in code from
/// these, so every card, label and panel head comes out of one definition -
/// the same role the original's shared CSS classes played.
/// </summary>
public static class Ui
{
    public const double Radius = 14;
    public const double RadiusSmall = 9;

    /* ---------------------------------------------------------------- Text */

    public static TextBlock Text(
        string text,
        double size = 15,
        int weight = 400,
        Brush? brush = null,
        double spacing = 0,
        bool numeric = false,
        bool wrap = false,
        bool selectable = true)
    {
        var block = new TextBlock
        {
            // Text is selectable and copyable, as it was in the browser. Labels
            // on controls are not (see NoSelect): dragging there should press
            // the control, not start a selection.
            IsTextSelectionEnabled = selectable,
            SelectionHighlightColor = Palette.Accent,
            Text = text,
            FontFamily = Fonts.Sans,
            FontSize = size,
            FontWeight = Fonts.Weight(weight),
            Foreground = brush ?? Palette.TextBrush,
            CharacterSpacing = (int)Math.Round(spacing * 1000),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = true,
        };
        if (numeric) Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    /// <summary>The small uppercase label: section titles, headline labels, table heads.</summary>
    public static TextBlock Caps(string text, double size = 13, double spacing = 0.13, Brush? brush = null) =>
        Text(text.ToUpperInvariant(), size, 600, brush ?? Palette.TextFaintBrush, spacing);

    public static TextBlock Mono(string text, double size = 13.5, Brush? brush = null)
    {
        var block = Text(text, size, 400, brush ?? Palette.TextMutedBrush);
        block.FontFamily = Fonts.Mono;
        return block;
    }

    /// <summary>A paragraph that wraps, with inline <c>code</c> runs marked by backticks.</summary>
    public static TextBlock Paragraph(string text, double size = 14.5, Brush? brush = null)
    {
        var block = Text("", size, 400, brush ?? Palette.TextFaintBrush, wrap: true);
        block.Text = null;
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var run = new Run { Text = parts[i] };
            if (i % 2 == 1)
            {
                run.FontFamily = Fonts.Mono;
                run.FontSize = size - 1;
            }
            block.Inlines.Add(run);
        }
        block.LineHeight = size * 1.55;
        return block;
    }

    /* --------------------------------------------------------------- Cards */

    public static Border Card(UIElement child, Thickness padding, bool hover = false)
    {
        var card = new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = padding,
            Child = child,
        };
        if (hover) HoverLift(card);
        return card;
    }

    /// <summary>
    /// The card hover: the border brightens and the card lifts 2px, both eased.
    /// Translation rather than a margin change, so nothing around it reflows.
    /// </summary>
    public static void HoverLift(Border card, double lift = 2)
    {
        // The same TranslateTransform the entry animation uses: WinUI refuses a
        // RenderTransform on an element that also has a TranslationTransition.
        card.PointerEntered += (_, _) =>
        {
            card.BorderBrush = Palette.BorderBrightBrush;
            Slide(card, -lift, 200);
        };
        card.PointerExited += (_, _) =>
        {
            card.BorderBrush = Palette.BorderBrush;
            Slide(card, 0, 200);
        };
    }

    private static TranslateTransform Transform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static void Slide(UIElement element, double to, int ms)
    {
        if (!Motion.Enabled)
        {
            Transform(element).Y = to;
            return;
        }
        var story = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
        };
        Storyboard.SetTarget(anim, Transform(element));
        Storyboard.SetTargetProperty(anim, "Y");
        story.Children.Add(anim);
        story.Begin();
    }

    /// <summary>The section heading between groups of panels.</summary>
    public static TextBlock SectionTitle(string text)
    {
        var title = Caps(text);
        title.Margin = new Thickness(0, 40, 0, 18);
        return title;
    }

    /// <summary>
    /// A panel: title, optional subtitle, optional control on the right, then
    /// the body. The head wraps the control under the title only when there is
    /// genuinely no room for both.
    /// </summary>
    public static Border Panel(string title, string? subtitle, UIElement? right, UIElement body, bool titleIsPage = false)
    {
        var head = new Grid { ColumnSpacing = 18, Margin = new Thickness(0, 0, 0, 22) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        var titleBlock = Text(title, 21, 620, spacing: -0.015);
        if (titleIsPage) Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(titleBlock, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        else Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(titleBlock, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        titles.Children.Add(titleBlock);
        if (subtitle is not null)
        {
            var sub = Paragraph(subtitle);
            sub.Margin = new Thickness(0, 5, 0, 0);
            titles.Children.Add(sub);
        }
        head.Children.Add(titles);
        if (right is FrameworkElement r)
        {
            Grid.SetColumn(r, 1);
            r.VerticalAlignment = VerticalAlignment.Top;
            head.Children.Add(r);
        }

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        var card = Card(stack, new Thickness(28, 26, 28, 20));
        card.Margin = new Thickness(0, 0, 0, 22);
        return card;
    }

    /* --------------------------------------------------------------- Small */

    public static Border Swatch(Color color, double size = 11, double radius = 3) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(radius),
        Background = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Pill(string text, Brush foreground, Brush border, Brush? background = null) => new()
    {
        Padding = new Thickness(8, 1, 8, 2),
        // Half the pill's height. WinUI does not clamp an oversized radius the
        // way CSS clamps 999px, so "fully round" has to be stated exactly.
        CornerRadius = new CornerRadius(11),
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        Background = background ?? Palette.TransparentBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text.ToUpperInvariant(), 11.5, 700, foreground, 0.05),
    };

    public static Border UnpricedPill() =>
        Pill("Unpriced", Palette.WarnBrush, Palette.WarnBorderBrush, Palette.WarnDimBrush);

    /// <summary>
    /// The "i" that explains a figure: a real, focusable button whose tooltip
    /// carries the text, and whose accessible name and description say it too.
    /// </summary>
    public static Button InfoTip(string label, string text)
    {
        var glyph = Text("i", 11, 700, Palette.TextFaintBrush, selectable: false);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button
        {
            Width = 17,
            Height = 17,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8.5),
            Content = glyph,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.TransparentBrush;
        button.Resources["ButtonBackgroundPressed"] = Palette.TransparentBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.Accent;
        button.Resources["ButtonBorderBrushPressed"] = Palette.Accent;
        button.PointerEntered += (_, _) => glyph.Foreground = Palette.Accent;
        button.PointerExited += (_, _) => glyph.Foreground = Palette.TextFaintBrush;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetFullDescription(button, text);
        ToolTipService.SetToolTip(button, TipContent(text));
        ToolTipService.SetPlacement(button, PlacementMode.Bottom);
        return button;
    }

    /// <summary>A tooltip body in the explainer style: 290px wide, muted text.</summary>
    public static ToolTip TipContent(string text)
    {
        var block = Text(text, 13, 400, Palette.TextMutedBrush, wrap: true);
        block.LineHeight = 20;
        return new ToolTip
        {
            Content = block,
            MaxWidth = 320,
            Background = Palette.TooltipBgBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusSmall),
            Padding = new Thickness(13, 11, 13, 11),
        };
    }

    /// <summary>A text-only tooltip for things like a clipped label.</summary>
    public static void SetTip(FrameworkElement element, string text) =>
        ToolTipService.SetToolTip(element, TipContent(text));

    /// <summary>Turns text selection off throughout a control's content.</summary>
    public static T NoSelect<T>(T content) where T : class
    {
        switch (content)
        {
            case TextBlock block:
                block.IsTextSelectionEnabled = false;
                break;
            case Panel panel:
                foreach (var child in panel.Children) NoSelect(child);
                break;
            case Border border when border.Child is not null:
                NoSelect(border.Child);
                break;
            case ContentControl control when control.Content is not null:
                NoSelect(control.Content);
                break;
        }
        return content;
    }

    /* ------------------------------------------------------------ Buttons */

    /// <summary>The outlined button. <paramref name="primary"/> is the accent one (Refresh).</summary>
    public static Button Button(object content, bool primary = false, double fontSize = 15, Thickness? padding = null)
    {
        var button = new Button
        {
            Content = NoSelect(content),
            FontFamily = Fonts.Sans,
            FontSize = fontSize,
            FontWeight = Fonts.Weight(570),
            Padding = padding ?? new Thickness(19, 10, 19, 10),
            CornerRadius = new CornerRadius(RadiusSmall),
            BorderThickness = new Thickness(1),
            Background = primary ? Palette.AccentDim : Palette.SurfaceBrush,
            BorderBrush = primary ? Palette.AccentBorderStrong : Palette.BorderBrightBrush,
            Foreground = primary ? Palette.AccentBright : Palette.TextBrush,
        };
        button.Resources["ButtonBackgroundPointerOver"] = primary ? Palette.AccentFill : Palette.SurfaceHoverBrush;
        button.Resources["ButtonBackgroundPressed"] = primary ? Palette.AccentFill : Palette.SurfaceHoverBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.Accent;
        button.Resources["ButtonBorderBrushPressed"] = Palette.Accent;
        button.Resources["ButtonForegroundPointerOver"] = primary ? Palette.AccentBright : Palette.TextBrush;
        button.Resources["ButtonForegroundPressed"] = primary ? Palette.AccentBright : Palette.TextBrush;
        button.Resources["ButtonBackgroundDisabled"] = primary ? Palette.AccentDim : Palette.SurfaceBrush;
        button.Resources["ButtonBorderBrushDisabled"] = primary ? Palette.AccentBorderStrong : Palette.BorderBrightBrush;
        button.Resources["ButtonForegroundDisabled"] = primary ? Palette.AccentBright : Palette.TextMutedBrush;
        button.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        button.PointerEntered += (_, _) => { if (button.IsEnabled) button.Translation = new Vector3(0, -1, 0); };
        button.PointerExited += (_, _) => button.Translation = Vector3.Zero;
        return button;
    }

    /// <summary>A text link in the accent colour, like "← All projects".</summary>
    public static HyperlinkButton Link(string text, Action onClick, double size = 14.5, Brush? brush = null)
    {
        var link = new HyperlinkButton
        {
            Content = Text(text, size, 400, brush ?? Palette.TextMutedBrush, selectable: false),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Palette.TransparentBrush,
        };
        link.Resources["HyperlinkButtonBackgroundPointerOver"] = Palette.TransparentBrush;
        link.Resources["HyperlinkButtonBackgroundPressed"] = Palette.TransparentBrush;
        link.Click += (_, _) => onClick();
        return link;
    }

    /// <summary>
    /// A soft glow behind a piece of text - the headline total's accent halo.
    /// XAML has no text-shadow, so this is a composition drop shadow cut from
    /// the text's own alpha mask, which follows the text as it counts up.
    /// </summary>
    public static Grid Glow(TextBlock text, Color color, float blur = 60)
    {
        var host = new Grid();
        var behind = new Canvas { IsHitTestVisible = false };
        host.Children.Add(behind);
        host.Children.Add(text);
        text.SizeChanged += (_, _) =>
        {
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(text).Compositor;
            var shadow = compositor.CreateDropShadow();
            shadow.Mask = text.GetAlphaMask();
            shadow.Color = color;
            shadow.BlurRadius = blur;
            shadow.Offset = Vector3.Zero;
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2((float)text.ActualWidth, (float)text.ActualHeight);
            sprite.Shadow = shadow;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(behind, sprite);
        };
        return host;
    }

    /* ------------------------------------------------------------- Motion */

    /// <summary>
    /// The "rise" entry: fade in from 14px below, staggered by <paramref name="delayMs"/>.
    /// Skipped when the system asks for reduced motion.
    /// </summary>
    public static void Rise(UIElement element, int delayMs = 0)
    {
        if (!Motion.Enabled) return;
        var transform = Transform(element);
        transform.Y = 14;
        element.Opacity = 0;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var story = new Storyboard();
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = new Duration(TimeSpan.FromMilliseconds(450));

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, BeginTime = begin, EasingFunction = ease };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);

        var slide = new DoubleAnimation { From = 14, To = 0, Duration = duration, BeginTime = begin, EasingFunction = ease };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");
        story.Children.Add(slide);
        story.Begin();
    }
}

/// <summary>Whether the system allows animation (Settings, Accessibility, Visual effects).</summary>
public static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();

    public static bool Enabled
    {
        get
        {
            try
            {
                return Settings.AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }
}
