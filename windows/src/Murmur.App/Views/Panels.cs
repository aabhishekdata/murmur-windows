using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Murmur.App.Controls;
using Murmur.App.Design;

namespace Murmur.App.Views;

/// <summary>
/// Shared furniture — cards, setting rows, fields, empty states.
/// </summary>
/// <remarks>
/// <para>
/// Every value comes from <see cref="Tokens"/>, and every colour is bound through
/// <see cref="Tokens.Live"/>, so whatever is built here repaints on a theme switch. Factoring
/// these out is what stops the same padding being typed slightly differently in three views,
/// which is how a design system erodes one reasonable-looking commit at a time.
/// </para>
/// <para>
/// Settings follow Windows 11's own rhythm: a group header in body-strong, then cards 4 px apart,
/// each with an icon, a title, a one-line description and its control on the right — the
/// <see cref="FASettingsExpander"/> FluentAvalonia ports from WinUI.
/// </para>
/// </remarks>
internal static class Panels
{
    /// <summary>A search field, with Windows' search glyph inside it.</summary>
    public static TextBox SearchBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Body,
        MinWidth = 240,
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        InnerLeftContent = new FluentIcon
        {
            Data = Icons.Search,
            Margin = new Thickness(Tokens.Space.Base, 0, 0, 0),
            [!FluentIcon.ForegroundProperty] = Tokens.Live.InkFaint,
        },
    };

    /// <summary>An ordinary text field.</summary>
    public static TextBox Field(string placeholder, string text) => new()
    {
        Text = text,
        PlaceholderText = placeholder,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Body,
    };

    /// <summary>The row a search field sits in, with an action on the right.</summary>
    public static Control SearchRow(Control search, Control? trailing = null)
    {
        var row = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(Tokens.Space.Section, 0, Tokens.Space.Section, Tokens.Space.Base),
        };

        if (trailing is not null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            row.Children.Add(trailing);
        }

        DockPanel.SetDock(search, Dock.Left);
        row.Children.Add(search);
        return row;
    }

    /// <summary>A footer strip with a count on the left and an action on the right.</summary>
    public static Border Footer(Control leading, Control trailing)
    {
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(trailing, Dock.Right);
        row.Children.Add(trailing);
        leading.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(leading);

        return new Border
        {
            Padding = new Thickness(Tokens.Space.Section, Tokens.Space.Snug),
            BorderThickness = new Thickness(0, Tokens.Border.Hairline, 0, 0),
            [!Border.BorderBrushProperty] = Tokens.Live.Line,
            Child = row,
        };
    }

    /// <summary>A subtle button, for row actions.</summary>
    public static SoftButton QuietButton(string label) => new()
    {
        Content = label,
        Tone = ButtonTone.Quiet,
        FontSize = Tokens.Fonts.Label,
        Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Tight),
    };

    /// <summary>A standard button.</summary>
    public static SoftButton OutlineButton(string label) => new() { Content = label, Tone = ButtonTone.Outline };

    /// <summary>The one accent button on a screen.</summary>
    public static SoftButton PrimaryButton(string label) => new() { Content = label, Tone = ButtonTone.Primary };

    /// <summary>One block of content on a card.</summary>
    public static Border Card(Control content) => new()
    {
        CornerRadius = new CornerRadius(Tokens.Radius.Card),
        BorderThickness = new Thickness(Tokens.Border.Hairline),
        Padding = new Thickness(Tokens.Space.Roomy),
        [!Border.BackgroundProperty] = Tokens.Live.Surface,
        [!Border.BorderBrushProperty] = Tokens.Live.Line,
        Child = content,
    };

    /// <summary>Body copy.</summary>
    public static TextBlock Body(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Body,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Tokens.Fonts.Body * Tokens.Fonts.BodyLineHeight,
        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
    };

    /// <summary>Supporting copy beneath a control.</summary>
    public static TextBlock Note(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Label,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = Tokens.Fonts.Label * Tokens.Fonts.BodyLineHeight,
        [!TextBlock.ForegroundProperty] = Tokens.Live.InkMuted,
    };

    /// <summary>A page title, as Windows Settings sets it.</summary>
    public static TextBlock Title(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.UiDisplay,
        FontSize = Tokens.Fonts.Title,
        FontWeight = FontWeight.SemiBold,
        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
    };

    /// <summary>A card or group title.</summary>
    public static TextBlock Subtitle(string text) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Subtitle,
        FontWeight = FontWeight.SemiBold,
        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
    };

    /// <summary>Centred "nothing here yet" copy, with an icon above it.</summary>
    public static Control EmptyState(string label, string detail, string? icon = null)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = Tokens.Space.Snug,
            Margin = new Thickness(Tokens.Space.Wide, Tokens.Space.Page),
        };

        if (icon is not null)
        {
            panel.Children.Add(new FluentIcon
            {
                Data = icon,
                Width = Tokens.Space.Section,
                Height = Tokens.Space.Section,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, Tokens.Space.Tight),
                [!FluentIcon.ForegroundProperty] = Tokens.Live.InkFaint,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Subtitle,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
        });

        panel.Children.Add(new TextBlock
        {
            Text = detail,
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Body,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            [!TextBlock.ForegroundProperty] = Tokens.Live.InkMuted,
        });

        return panel;
    }

    /// <summary>
    /// A standing notice — the missing speech model, a missing key.
    /// </summary>
    /// <remarks>
    /// Windows has no built-in engine to fall back on, so a missing model means the app does
    /// nothing at all when the key is pressed. That has to be visible where the user is, not
    /// buried in Settings. Drawn like Windows' warning InfoBar: amber wash, a warning glyph.
    /// </remarks>
    public static Border Notice(string text, Control? action = null)
    {
        var row = new DockPanel { LastChildFill = true };

        if (action is not null)
        {
            DockPanel.SetDock(action, Dock.Right);
            action.Margin = new Thickness(Tokens.Space.Base, 0, 0, 0);
            row.Children.Add(action);
        }

        row.Children.Add(new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Docked(new FluentIcon
                {
                    Data = Icons.Warning,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, Tokens.Space.Base, 0),
                    [!FluentIcon.ForegroundProperty] = Tokens.Live.Notice,
                }, Dock.Left),
                new TextBlock
                {
                    Text = text,
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                },
            },
        });

        return new Border
        {
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            BorderThickness = new Thickness(Tokens.Border.Hairline),
            Padding = new Thickness(Tokens.Space.Roomy, Tokens.Space.Base),
            [!Border.BackgroundProperty] = Tokens.Live.NoticeSoft,
            [!Border.BorderBrushProperty] = Tokens.Live.Line,
            Child = row,
        };
    }

    /// <summary>A small tag. Accent chips are the accent wash; neutral ones are grey.</summary>
    public static Border Chip(string text, bool accent = false) => new()
    {
        CornerRadius = new CornerRadius(Tokens.Radius.Chip),
        Padding = new Thickness(Tokens.Space.Snug - Tokens.Space.Hair, Tokens.Space.Hair - 1),
        VerticalAlignment = VerticalAlignment.Center,
        [!Border.BackgroundProperty] = accent ? Tokens.Live.AccentSoft : Tokens.Live.SurfaceMuted,
        Child = new TextBlock
        {
            Text = text,
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Caption,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            [!TextBlock.ForegroundProperty] = accent ? Tokens.Live.Accent : Tokens.Live.InkMuted,
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
                FontFamily = Tokens.Fonts.UiDisplay,
                FontSize = Tokens.Fonts.Display,
                FontWeight = FontWeight.SemiBold,
                [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
            },
            new TextBlock
            {
                Text = label,
                FontFamily = Tokens.Fonts.Ui,
                FontSize = Tokens.Fonts.Label,
                [!TextBlock.ForegroundProperty] = Tokens.Live.InkMuted,
            },
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
        Spacing = Tokens.Space.Tight + Tokens.Space.Hair,
        Children = { FieldLabel(label), content },
    };

    /// <summary>The label over a form field: body size, regular weight, as Windows sets a TextBox header.</summary>
    public static TextBlock FieldLabel(string label) => new()
    {
        Text = label,
        FontFamily = Tokens.Fonts.Ui,
        FontSize = Tokens.Fonts.Body,
        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
    };

    // ---- Settings ----

    /// <summary>
    /// A group of setting cards under a header, Windows 11 Settings style.
    /// </summary>
    /// <param name="title">The group's header.</param>
    /// <param name="cards">Cards from <see cref="Setting"/> and <see cref="Expander"/>.</param>
    /// <returns>The group.</returns>
    public static StackPanel Group(string title, params Control[] cards)
    {
        var group = new StackPanel { Spacing = Tokens.Space.Tight };
        group.Children.Add(new Caption
        {
            Text = title,
            IsHeader = true,
            Margin = new Thickness(Tokens.Space.Tight, 0, 0, Tokens.Space.Tight),
        });

        foreach (var card in cards) group.Children.Add(card);
        return group;
    }

    /// <summary>
    /// One setting: icon, title, description, and its control on the right.
    /// </summary>
    /// <param name="icon">Path data from <see cref="Icons"/>, or null.</param>
    /// <param name="title">What the setting is.</param>
    /// <param name="description">One line on what it does, or null.</param>
    /// <param name="action">The control — a toggle, a combo box, a button — or null.</param>
    /// <returns>The card.</returns>
    public static FASettingsExpander Setting(string? icon, string title, string? description, Control? action) =>
        new()
        {
            Header = title,
            Description = description,
            IconSource = icon is null ? null : IconSource(icon),
            Footer = action,
        };

    /// <summary>
    /// A setting that opens to show more: the header row on top, <paramref name="items"/> inside.
    /// </summary>
    /// <param name="icon">Path data from <see cref="Icons"/>, or null.</param>
    /// <param name="title">What the setting is.</param>
    /// <param name="description">One line on what it does, or null.</param>
    /// <param name="action">A control on the header row, or null.</param>
    /// <param name="items">Rows inside, from <c>Item</c> or any control.</param>
    /// <returns>The expander, open.</returns>
    public static FASettingsExpander Expander(
        string? icon, string title, string? description, Control? action, params Control[] items)
    {
        var expander = Setting(icon, title, description, action);
        foreach (var item in items) expander.Items.Add(item);
        expander.IsExpanded = true;
        return expander;
    }

    /// <summary>One row inside an <see cref="Expander"/>.</summary>
    /// <param name="title">What the row is, or null for a row that is all content.</param>
    /// <param name="description">One line under it, or null.</param>
    /// <param name="action">The control on the right, or null.</param>
    /// <returns>The row.</returns>
    public static FASettingsExpanderItem Item(string? title, string? description, Control? action) => new()
    {
        Content = title,
        Description = description,
        Footer = action,
    };

    /// <summary>One row inside an <see cref="Expander"/> whose whole width is <paramref name="content"/>.</summary>
    public static FASettingsExpanderItem Item(Control content) => new() { Content = content };

    /// <summary>A Windows toggle switch that reports its new value.</summary>
    /// <param name="value">The current value.</param>
    /// <param name="onChange">Called with the new value when the user flips it.</param>
    /// <returns>The switch.</returns>
    public static ToggleSwitch Toggle(bool value, Action<bool> onChange)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = value,
            OnContent = "On",
            OffContent = "Off",
            MinWidth = 0,
        };

        toggle.IsCheckedChanged += (_, _) => onChange(toggle.IsChecked ?? false);
        return toggle;
    }

    /// <summary>A combo box over labelled values.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="options">Each option's value and label, in order.</param>
    /// <param name="selected">The value to show selected.</param>
    /// <param name="onChange">Called with the chosen value when the user changes it.</param>
    /// <returns>The combo box.</returns>
    public static ComboBox Choice<T>(IReadOnlyList<(T Value, string Label)> options, T selected, Action<T> onChange)
    {
        var combo = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = options.Select(o => o.Label).ToList(),
            SelectedIndex = IndexOf(options, selected),
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) onChange(options[combo.SelectedIndex].Value);
        };

        return combo;
    }

    private static int IndexOf<T>(IReadOnlyList<(T Value, string Label)> options, T value)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(options[i].Value, value)) return i;
        }

        return options.Count > 0 ? 0 : -1;
    }

    /// <summary>A FluentAvalonia icon source for one of <see cref="Icons"/>.</summary>
    public static FAPathIconSource IconSource(string data) => new() { Data = Icons.Geometry(data) };
}
