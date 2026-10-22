using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pickverter.UI;

/// Windows 11 style on/off switch.
internal sealed class ToggleSwitch : ToggleButton
{
    private static readonly ControlTemplate Empty = new(typeof(ToggleButton));

    public static readonly DependencyProperty KnobProperty = DependencyProperty.Register(nameof(Knob), typeof(double), typeof(ToggleSwitch),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Knob
    {
        get => (double)GetValue(KnobProperty);
        set => SetValue(KnobProperty, value);
    }

    public ToggleSwitch()
    {
        Template = Empty;
        Width = 40;
        Height = 20;
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = Ui.FocusRing(10);
        VerticalAlignment = VerticalAlignment.Center;
        Checked += (_, _) => Slide(1);
        Unchecked += (_, _) => Slide(0);
        Loaded += (_, _) => Knob = IsChecked == true ? 1 : 0;
    }

    private void Slide(double to) =>
        BeginAnimation(KnobProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(130)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint) => new(40, 20);
    protected override Size ArrangeOverride(Size arrangeBounds) => new(40, 20);

    protected override void OnRender(DrawingContext dc)
    {
        var on = IsChecked == true;
        var track = new Rect(0.5, 0.5, 39, 19);
        var dim = IsEnabled ? 1.0 : 0.4;
        dc.PushOpacity(dim);
        if (on)
        {
            dc.DrawRoundedRectangle(IsMouseOver ? Theme.AccentHover : Theme.Accent, null, track, 9.5, 9.5);
        }
        else
        {
            dc.DrawRoundedRectangle(IsMouseOver ? Theme.SubtleHover : Brushes.Transparent, new Pen(Theme.TextSecondary, 1), track, 9.5, 9.5);
        }
        var radius = IsPressed ? 7.0 : IsMouseOver ? 6.5 : 6.0;
        var x = 10 + Knob * 20;
        dc.DrawEllipse(on ? Theme.TextOnAccent : Theme.TextSecondary, null, new Point(x, 10), IsPressed ? radius + 1.5 : radius, radius);
        dc.Pop();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ToggleButtonAutomationPeer(this);
}

/// Flat button: neutral fill with a hairline, or the accent colour. The label never wraps.
internal sealed class FlatButton : Button
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(CornerRadius), typeof(FlatButton),
        new PropertyMetadata(new CornerRadius(4)));

    public CornerRadius Radius
    {
        get => (CornerRadius)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private static readonly ControlTemplate Chrome = BuildTemplate();
    private readonly TextBlock? label;
    private readonly TextBlock? glyph;
    private Brush? tint;

    public bool Prominent { get; }
    public bool Subtle { get; }

    /// Text colour that wins over the style (links).
    public void Tint(Brush brush)
    {
        tint = brush;
        Update();
    }

    public FlatButton(string? title, string? icon = null, bool prominent = false, bool subtle = false, bool compact = false, double iconSize = 0)
    {
        Prominent = prominent;
        Subtle = subtle;
        Template = Chrome;
        Cursor = Cursors.Hand;
        FocusVisualStyle = Ui.FocusRing(6);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        BorderThickness = new Thickness(subtle || prominent ? 0 : 1);
        var height = compact ? 26.0 : 32.0;
        MinHeight = height;
        Height = height;
        Padding = new Thickness(title == null ? (compact ? 6 : 8) : (compact ? 9 : 12), 0, title == null ? (compact ? 6 : 8) : (compact ? 10 : 12), 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null)
        {
            glyph = Glyphs.Icon(icon, iconSize > 0 ? iconSize : compact ? 13 : 15);
            row.Children.Add(glyph);
        }
        if (title != null)
        {
            label = new TextBlock
            {
                Text = title,
                FontSize = compact ? 12 : 14,
                FontFamily = Theme.TextFont,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(icon != null ? (compact ? 5 : 7) : 0, 0, 0, compact ? 1 : 1),
            };
            row.Children.Add(label);
            AutomationProperties.SetName(this, title);
        }
        Content = row;
        if (title == null && icon != null) ToolTip = null;
        Update();
    }

    private static ControlTemplate BuildTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new TemplateBindingExtension(RadiusProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(VerticalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, false);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(FlatButton)) { VisualTree = border };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty) Update();
    }

    public void Update()
    {
        Brush background, foreground;
        if (Prominent)
        {
            background = !IsEnabled ? Theme.Tile : IsPressed ? Theme.AccentSubtle : IsMouseOver ? Theme.AccentHover : Theme.Accent;
            foreground = IsEnabled ? Theme.TextOnAccent : Theme.TextDisabled;
            if (IsPressed && IsEnabled) foreground = Theme.TextPrimary;
        }
        else if (Subtle)
        {
            background = IsPressed ? Theme.SubtlePressed : IsMouseOver ? Theme.SubtleHover : Brushes.Transparent;
            foreground = IsEnabled ? Theme.TextPrimary : Theme.TextDisabled;
        }
        else
        {
            background = IsPressed ? Theme.SubtlePressed : IsMouseOver ? Theme.ControlHover : Theme.Control;
            foreground = IsEnabled ? Theme.TextPrimary : Theme.TextDisabled;
        }
        Background = background;
        BorderBrush = Theme.ControlStroke;
        if (tint != null && IsEnabled) foreground = tint;
        Foreground = foreground;
        if (label != null) label.Foreground = foreground;
        if (glyph != null) glyph.Foreground = foreground;
        if (label != null && tint != null) label.TextDecorations = IsMouseOver ? TextDecorations.Underline : null;
    }

    /// Only the icon is shown; the title becomes the tooltip and the accessible name.
    public static FlatButton IconOnly(string title, string icon, Action click, bool compact = true, bool subtle = false)
    {
        var button = new FlatButton(null, icon, compact: compact, subtle: subtle) { ToolTip = title };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => click();
        return button;
    }
}

/// A key as printed on the keyboard ("Alt", "Ctrl", "Shift").
internal static class KeyCap
{
    public static Border Make(string text, double size = 11.5) => new()
    {
        Background = Theme.Control,
        BorderBrush = Theme.ControlStroke,
        BorderThickness = new Thickness(1, 1, 1, 1.6),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        MinWidth = 22,
        MinHeight = 20,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = Theme.TextFont,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
}

/// Stack with even spacing between children.
internal sealed class SpacedStack : Panel
{
    public Orientation Orientation { get; set; } = Orientation.Vertical;
    public double Spacing { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        double main = 0, cross = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(Orientation == Orientation.Vertical ? new Size(available.Width, double.PositiveInfinity) : new Size(double.PositiveInfinity, available.Height));
            var size = child.DesiredSize;
            main += Orientation == Orientation.Vertical ? size.Height : size.Width;
            cross = Math.Max(cross, Orientation == Orientation.Vertical ? size.Width : size.Height);
            count++;
        }
        main += Math.Max(0, count - 1) * Spacing;
        return Orientation == Orientation.Vertical ? new Size(cross, main) : new Size(main, cross);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double offset = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (Orientation == Orientation.Vertical)
            {
                child.Arrange(new Rect(0, offset, final.Width, size.Height));
                offset += size.Height + Spacing;
            }
            else
            {
                child.Arrange(new Rect(offset, 0, size.Width, final.Height));
                offset += size.Width + Spacing;
            }
        }
        return final;
    }
}

/// Shows the first child that fits the width without wrapping or clipping (like SwiftUI's ViewThatFits).
internal sealed class ViewThatFits : Panel
{
    private UIElement? chosen;

    protected override Size MeasureOverride(Size available)
    {
        chosen = null;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            if (chosen == null && child.DesiredSize.Width <= available.Width + 0.5) chosen = child;
        }
        if (InternalChildren.Count == 0) return new Size();
        chosen ??= InternalChildren[InternalChildren.Count - 1];
        chosen.Measure(available);
        return new Size(Math.Min(chosen.DesiredSize.Width, available.Width), chosen.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.ClipToBounds = true;
            child.IsHitTestVisible = child == chosen;
            child.Arrange(child == chosen ? new Rect(final) : new Rect(0, 0, 0, 0));
        }
        return final;
    }
}

/// Circular progress around a job's icon (indeterminate when the fraction is unknown).
internal sealed class ProgressRing : FrameworkElement
{
    private double? value;
    private readonly RotateTransform spin = new();

    public double Thickness { get; init; } = 2.5;

    public ProgressRing()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = spin;
    }

    public double? Value
    {
        get => value;
        set
        {
            this.value = value;
            if (value == null)
            {
                spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                spin.BeginAnimation(RotateTransform.AngleProperty, null);
                spin.Angle = 0;
            }
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - Thickness / 2;
        dc.DrawEllipse(null, new Pen(Theme.Tile, Thickness), center, radius, radius);
        var fraction = value ?? 0.25;
        fraction = Math.Clamp(fraction, 0.04, 0.999);
        var pen = new Pen(Theme.Accent, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, Arc(center, radius, 0, fraction * 2 * Math.PI));
    }

    public static Geometry Arc(Point center, double radius, double start, double end)
    {
        var a = new Point(center.X + Math.Sin(start) * radius, center.Y - Math.Cos(start) * radius);
        var b = new Point(center.X + Math.Sin(end) * radius, center.Y - Math.Cos(end) * radius);
        var figure = new PathFigure { StartPoint = a, IsClosed = false };
        figure.Segments.Add(new ArcSegment(b, new Size(radius, radius), 0, end - start > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}

/// Thin linear progress (indeterminate: a sliding segment).
internal sealed class LinearProgress : FrameworkElement
{
    private double? value;
    private double phase;

    public LinearProgress()
    {
        Height = 3;
        Loaded += (_, _) => CompositionTarget.Rendering += Tick;
        Unloaded += (_, _) => CompositionTarget.Rendering -= Tick;
    }

    public double? Value
    {
        get => value;
        set
        {
            this.value = value;
            InvalidateVisual();
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (value != null) return;
        phase = (phase + 0.012) % 1.6;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0) return;
        var radius = height / 2;
        dc.DrawRoundedRectangle(Theme.Tile, null, new Rect(0, 0, width, height), radius, radius);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));
        if (value is { } fraction)
        {
            dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(0, 0, Math.Max(height * 2, width * Math.Clamp(fraction, 0, 1)), height), radius, radius);
        }
        else
        {
            var segment = width * 0.32;
            dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(width * (phase - 0.4), 0, segment, height), radius, radius);
        }
        dc.Pop();
    }
}

internal static class Ui
{
    private static readonly Dictionary<double, Style> Rings = new();

    /// Keyboard focus ring like Windows 11's (two pixels, outside the control).
    public static Style FocusRing(double radius)
    {
        if (Rings.TryGetValue(radius, out var cached)) return cached;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius + 3));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlTextBrushKey);
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = border }));
        style.Seal();
        Rings[radius] = style;
        return style;
    }

    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, Brush? brush = null, bool wrap = false,
                                 FontFamily? family = null, TextAlignment alignment = TextAlignment.Left)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = family ?? (size >= 20 ? Theme.DisplayFont : size <= 12 ? Theme.SmallFont : Theme.TextFont),
            Foreground = brush ?? Theme.TextPrimary,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Caption(string text, bool wrap = true) => Text(text, 12, brush: Theme.TextSecondary, wrap: wrap);

    public static SpacedStack Stack(Orientation orientation, double spacing, params UIElement[] children)
    {
        var stack = new SpacedStack { Orientation = orientation, Spacing = spacing };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    public static SpacedStack Column(double spacing, params UIElement[] children) => Stack(Orientation.Vertical, spacing, children);
    public static SpacedStack Row(double spacing, params UIElement[] children) => Stack(Orientation.Horizontal, spacing, children);

    public static FlatButton Button(string title, Action click, bool prominent = false, string? icon = null, bool compact = false, bool subtle = false)
    {
        var button = new FlatButton(title, icon, prominent, subtle, compact);
        button.Click += (_, _) => click();
        return button;
    }

    public static ToggleSwitch Toggle(bool value, Action<bool> changed, string name)
    {
        var toggle = new ToggleSwitch { IsChecked = value };
        AutomationProperties.SetName(toggle, name);
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> items, T selected, Action<T> changed, string name, double minWidth = 180)
    {
        var box = new ComboBox { MinWidth = minWidth, VerticalAlignment = VerticalAlignment.Center, FontFamily = Theme.TextFont, FontSize = 14 };
        AutomationProperties.SetName(box, name);
        var index = 0;
        foreach (var (value, label) in items)
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value, FontFamily = Theme.TextFont });
            if (EqualityComparer<T>.Default.Equals(value, selected)) box.SelectedIndex = index;
            index++;
        }
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: T value }) changed(value);
        };
        return box;
    }

    public static Border Card(UIElement content, Thickness? padding = null) => new()
    {
        Background = Theme.Card,
        BorderBrush = Theme.CardStroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = padding ?? new Thickness(16, 12, 16, 12),
        Child = content,
    };

    /// A Settings row: icon, title and description on the left, the control on the right.
    public static Border SettingRow(string title, string? detail, UIElement? control, string? icon = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon != null)
        {
            var glyph = Glyphs.Icon(icon, 18, Theme.TextPrimary);
            glyph.Margin = new Thickness(0, 0, 16, 0);
            grid.Children.Add(glyph);
        }
        var texts = Column(2, Text(title, 14, wrap: true));
        if (!string.IsNullOrEmpty(detail)) texts.Children.Add(Caption(detail));
        texts.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (control is FrameworkElement element)
        {
            element.Margin = new Thickness(16, 0, 0, 0);
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, 2);
            grid.Children.Add(element);
        }
        var card = Card(grid);
        card.MinHeight = 64;
        return card;
    }

    public static TextBlock SectionHeader(string text)
    {
        var block = Text(text, 14, FontWeights.SemiBold);
        block.Margin = new Thickness(2, 18, 0, 6);
        return block;
    }

    /// Text that looks and works like a link.
    public static FlatButton Link(string title, Action click)
    {
        var button = new FlatButton(title, null, subtle: true, compact: true) { Padding = new Thickness(4, 0, 4, 0) };
        button.Tint(Theme.AccentText);
        button.Click += (_, _) => click();
        return button;
    }

    public static Border Divider() => new() { Height = 1, Background = Theme.Divider, Margin = new Thickness(0, 4, 0, 4) };

    /// Takes an element out of whatever holds it, so a rebuilt layout can reuse it.
    public static T Detach<T>(T element) where T : FrameworkElement
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl control:
                control.Content = null;
                break;
            case ItemsControl items:
                items.Items.Remove(element);
                break;
        }
        return element;
    }
}
