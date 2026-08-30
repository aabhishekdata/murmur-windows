using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;

namespace Murmur.App.Views;

/// <summary>
/// Shared furniture — cards, rows, fields, empty states.
/// </summary>
/// <remarks>
/// Every value comes from <see cref="Tokens"/>. Factoring these out is what stops the same
/// padding being typed slightly differently in three views, which is how a design system
/// erodes one reasonable-looking commit at a time.
/// </remarks>
internal static class Panels
{
    /// <summary>A search field.</summary>
    public static TextBox SearchBox(string placeholder) => new()
    {
        Watermark = placeholder,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Body,
        Foreground = Tokens.Brushes.Ink,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>An ordinary text field, for dialogs.</summary>
    public static TextBox Field(string placeholder, string text) => new()
    {
        Text = text,
        Watermark = placeholder,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Body,
        Foreground = Tokens.Brushes.Ink,
        Background = Tokens.Brushes.Surface,
        BorderBrush = Tokens.Brushes.Line,
        BorderThickness = new Thickness(Tokens.Border.Hairline),
        CornerRadius = new CornerRadius(Tokens.Radius.Control),
        Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
    };

    /// <summary>The row a search field sits in, with a hairline beneath it.</summary>
    public static Border SearchRow(Control search, Control? trailing = null)
    {
        var row = new DockPanel { LastChildFill = true };

        if (trailing is not null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            row.Children.Add(trailing);
        }

        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new TextBlock
                {
                    Text = "⌕",
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Subtitle,
                    Foreground = Tokens.Brushes.InkFaint,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                search,
            },
        });

        return new Border
        {
            Background = Tokens.Brushes.Canvas,
            Padding = new Thickness(Tokens.Space.Wide, Tokens.Space.Base),
            BorderBrush = Tokens.Brushes.Line,
            BorderThickness = new Thickness(0, 0, 0, Tokens.Border.Hairline),
            Child = row,
        };
    }

    /// <summary>A footer strip with a count on the left and an action on the right.</summary>
    public static Border Footer(Control leading, Control trailing)
    {
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(trailing, Dock.Right);
        row.Children.Add(trailing);
        row.Children.Add(leading);

        return new Border
        {
            Background = Tokens.Brushes.Canvas,
            Padding = new Thickness(Tokens.Space.Wide, Tokens.Space.Base),
            BorderBrush = Tokens.Brushes.Line,
            BorderThickness = new Thickness(0, Tokens.Border.Hairline, 0, 0),
            Child = row,
        };
    }

    /// <summary>A small quiet button, for row actions.</summary>
    public static SoftButton QuietButton(string label) => new()
    {
        Content = label,
        Tone = ButtonTone.Quiet,
        Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Tight),
        FontSize = Tokens.Fonts.Caption,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A bordered button.</summary>
    public static SoftButton OutlineButton(string label) => new()
    {
        Content = label,
        Tone = ButtonTone.Outline,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The one filled button on a screen.</summary>
    public static SoftButton PrimaryButton(string label) => new()
    {
        Content = label,
        Tone = ButtonTone.Primary,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>One row of content on a raised surface.</summary>
    public static Border Card(Control content) => new()
    {
        Background = Tokens.Brushes.Surface,
        CornerRadius = new CornerRadius(Tokens.Radius.Card),
        BorderBrush = Tokens.Brushes.Line,
        BorderThickness = new Thickness(Tokens.Border.Hairline),
        Padding = new Thickness(Tokens.Space.Roomy),
        Child = content,
    };

    /// <summary>Body copy.</summary>
    public static TextBlock Body(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Body,
        Foreground = Tokens.Brushes.Ink,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Tokens.Fonts.Body * Tokens.Fonts.BodyLineHeight,
    };

    /// <summary>Supporting copy beneath a control.</summary>
    public static TextBlock Note(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Label,
        Foreground = Tokens.Brushes.InkMuted,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Tokens.Fonts.Label * Tokens.Fonts.BodyLineHeight,
    };

    /// <summary>A section title.</summary>
    public static TextBlock Title(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Title,
        FontWeight = FontWeight.SemiBold,
        Foreground = Tokens.Brushes.Ink,
    };

    /// <summary>Centred "nothing here yet" copy.</summary>
    public static Control EmptyState(string label, string detail) => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = Tokens.Space.Snug,
        Margin = new Thickness(0, Tokens.Space.Page),
        Children =
        {
            new TextBlock
            {
                Text = label,
                FontFamily = Tokens.Fonts.Ui,
                FontSize = Tokens.Fonts.Subtitle,
                FontWeight = FontWeight.SemiBold,
                Foreground = Tokens.Brushes.InkMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            new TextBlock
            {
                Text = detail,
                FontFamily = Tokens.Fonts.Ui,
                FontSize = Tokens.Fonts.Label,
                Foreground = Tokens.Brushes.InkFaint,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
            },
        },
    };

    /// <summary>
    /// A standing notice — used for the missing speech model.
    /// </summary>
    /// <remarks>
    /// Windows has no built-in engine to fall back on, so a missing model means the app does
    /// nothing at all when the key is pressed. That has to be visible where the user is, not
    /// buried in Settings.
    /// </remarks>
    public static Border Notice(string text, Control? action = null)
    {
        var row = new DockPanel { LastChildFill = true };

        if (action is not null)
        {
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
        }

        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            Children =
            {
                new StatusDot
                {
                    IsLit = true,
                    DotColor = Tokens.Colors.Notice,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = text,
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Label,
                    Foreground = Tokens.Brushes.Ink,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });

        return new Border
        {
            Background = Tokens.Brushes.NoticeSoft,
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            BorderBrush = new SolidColorBrush(Tokens.Colors.Notice, 0.3),
            BorderThickness = new Thickness(Tokens.Border.Hairline),
            Padding = new Thickness(Tokens.Space.Base),
            Child = row,
        };
    }

    /// <summary>A small pill-shaped tag.</summary>
    public static Border Chip(string text, Color colour) => new()
    {
        Background = new SolidColorBrush(colour, 0.12),
        CornerRadius = new CornerRadius(Tokens.Radius.Chip),
        Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Hair),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Caption,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(colour),
        },
    };

    /// <summary>One statistic: a big number over a quiet label.</summary>
    public static Control Stat(string value, string label) => new StackPanel
    {
        Spacing = Tokens.Space.Hair,
        Children =
        {
            new TextBlock
            {
                Text = value,
                FontFamily = Tokens.Fonts.Ui,
                FontSize = Tokens.Fonts.Display,
                FontWeight = FontWeight.SemiBold,
                Foreground = Tokens.Brushes.Ink,
            },
            new Caption { Text = label },
        },
    };

    /// <summary>
    /// Docks a control and returns it, so it reads inline in a Children list.
    /// </summary>
    /// <remarks>Not named <c>Dock</c>: that shadows the <see cref="Avalonia.Controls.Dock"/>
    /// enum at every call site.</remarks>
    public static Control Docked(Control control, Dock side)
    {
        DockPanel.SetDock(control, side);
        return control;
    }

    /// <summary>A label above a control.</summary>
    public static StackPanel Labelled(string label, Control content) => new()
    {
        Spacing = Tokens.Space.Snug,
        Children = { new Caption { Text = label, IsHeader = true }, content },
    };

    /// <summary>A titled group of settings on a card.</summary>
    public static Border Section(string label, Control content) => Card(new StackPanel
    {
        Spacing = Tokens.Space.Base,
        Children = { new Caption { Text = label, IsHeader = true }, content },
    });
}
