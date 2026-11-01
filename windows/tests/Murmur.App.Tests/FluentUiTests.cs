using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Murmur.App;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.App.Views;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.AppTests;

/// <summary>
/// The accent: the user's Windows accent, unless it is red.
/// </summary>
public sealed class AccentPolicyTests
{
    [Theory]
    // Windows' own accent swatches that are not red are followed as they are.
    [InlineData(0x0078D4, 0x0078D4)] // Windows 11 default blue
    [InlineData(0x0063B1, 0x0063B1)] // navy
    [InlineData(0x00B294, 0x00B294)] // seafoam
    [InlineData(0x107C10, 0x107C10)] // green
    [InlineData(0x8764B8, 0x8764B8)] // violet
    [InlineData(0xFFB900, 0xFFB900)] // gold: hue 44°, outside the red band
    [InlineData(0xF7630C, 0xF7630C)] // orange: hue 22°, just outside
    // Red, crimson and raspberry are not: red means recording.
    [InlineData(0xE81123, 0x5B5BD6)] // red: hue 356°
    [InlineData(0xC50F1F, 0x5B5BD6)] // dark red
    [InlineData(0xD13438, 0x5B5BD6)] // brick: hue 359°
    [InlineData(0xFF4343, 0x5B5BD6)] // coral red: hue 0°
    [InlineData(0xE3008C, 0xE3008C)] // magenta: hue 323°, outside the band
    [InlineData(0xEA005E, 0xEA005E)] // raspberry: hue 336°, just outside the band
    [InlineData(0xBF0077, 0xBF0077)] // plum: hue 323°
    // Desaturated reds read as dusty pink or brown, not as "recording".
    [InlineData(0xB47C7C, 0xB47C7C)] // dusty rose: saturation 0.31
    [InlineData(0x7A7574, 0x7A7574)] // warm grey
    public void Red_accents_are_replaced_with_indigo_and_others_are_followed(uint system, uint expected)
    {
        var chosen = AccentPolicy.Choose(Color.FromUInt32(0xFF000000 | system));

        chosen.ShouldBe(Color.FromUInt32(0xFF000000 | expected));
    }

    [Theory]
    [InlineData(341, true)]
    [InlineData(350, true)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(19, true)]
    [InlineData(22, false)]
    [InlineData(338, false)]
    [InlineData(120, false)]
    [InlineData(240, false)]
    public void The_red_band_runs_from_340_to_20_degrees(double hue, bool red)
    {
        AccentPolicy.IsRed(FromHue(hue, saturation: 0.9)).ShouldBe(red);
    }

    [Theory]
    [InlineData(0.39, false)]
    [InlineData(0.41, true)]
    [InlineData(1.0, true)]
    public void Only_a_saturated_red_counts(double saturation, bool red)
    {
        AccentPolicy.IsRed(FromHue(0, saturation)).ShouldBe(red);
    }

    [Fact]
    public void No_accent_at_all_is_indigo()
    {
        AccentPolicy.Choose(null).ShouldBe(AccentPolicy.Fallback);
        AccentPolicy.Choose(Colors.Transparent).ShouldBe(AccentPolicy.Fallback);
        AccentPolicy.Fallback.ShouldBe(Color.FromRgb(0x5B, 0x5B, 0xD6));
    }

    [Fact]
    public void The_fallback_keeps_the_exact_pair_the_design_was_drawn_with()
    {
        var palette = AccentPolicy.Palette(AccentPolicy.Fallback);

        palette.For(ColorToken.Accent, ThemeKind.Light).ShouldBe(Color.FromRgb(0x5B, 0x5B, 0xD6));
        palette.For(ColorToken.Accent, ThemeKind.Dark).ShouldBe(Color.FromRgb(0x8A, 0x8A, 0xF0));
        palette.For(ColorToken.Accent, ThemeKind.HighContrast).ShouldBe(AccentPalette.HighContrastAccent);
    }

    [Theory]
    [InlineData(0x0078D4)]
    [InlineData(0xFFB900)]
    [InlineData(0x5B5BD6)]
    [InlineData(0x107C10)]
    public void Text_on_any_accent_is_readable(uint accent)
    {
        var palette = AccentPolicy.Palette(Color.FromUInt32(0xFF000000 | accent));

        foreach (var kind in Enum.GetValues<ThemeKind>())
        {
            var fill = palette.For(ColorToken.Accent, kind);
            var text = palette.For(ColorToken.OnAccent, kind);
            AccentPolicy.Contrast(fill, text).ShouldBeGreaterThan(3, $"{kind} on #{accent:X6}");
        }
    }

    [Fact]
    public void No_derived_accent_shade_is_red_either()
    {
        // The light and dark shades are blends towards white and black, which keep the hue.
        foreach (var accent in new uint[] { 0x0078D4, 0x8764B8, 0xF7630C, 0x5B5BD6 })
        {
            var palette = AccentPolicy.Palette(Color.FromUInt32(0xFF000000 | accent));
            AccentPolicy.IsRed(palette.OnLightBackground).ShouldBeFalse();
            AccentPolicy.IsRed(palette.OnDarkBackground).ShouldBeFalse();
        }
    }

    private static Color FromHue(double hue, double saturation)
    {
        // HSV with value 1.
        var c = saturation;
        var x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = 1 - c;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>Light, dark and high contrast, switched live; the accent, re-tinted live.</summary>
public sealed class LiveThemeTests
{
    [AvaloniaFact]
    public void Switching_theme_repaints_a_bound_brush_without_rebuilding_the_view()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;

        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;

            var title = Panels.Title("Settings");
            var card = Panels.Card(title);
            var window = new Window { Content = card };
            window.Show();

            ((ISolidColorBrush)title.Foreground!).Color.ShouldBe(ThemePalette.Get(ColorToken.Ink, ThemeKind.Light));
            ((ISolidColorBrush)card.Background!).Color.ShouldBe(ThemePalette.Get(ColorToken.Surface, ThemeKind.Light));

            app.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();

            // The same controls, not new ones: the binding followed the theme.
            window.Content.ShouldBeSameAs(card);
            ((ISolidColorBrush)title.Foreground!).Color.ShouldBe(ThemePalette.Get(ColorToken.Ink, ThemeKind.Dark));
            ((ISolidColorBrush)card.Background!).Color.ShouldBe(ThemePalette.Get(ColorToken.Surface, ThemeKind.Dark));

            app.RequestedThemeVariant = ThemeResources.HighContrast;
            Dispatcher.UIThread.RunJobs();

            ((ISolidColorBrush)card.Background!).Color.ShouldBe(Colors.Black);
            ((ISolidColorBrush)card.BorderBrush!).Color.ShouldBe(Colors.White);
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void A_whole_hub_page_follows_a_theme_switch()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;

        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            var hub = new HubWindow();
            hub.Show();

            var titles = hub.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "Home").ToList();
            titles.ShouldNotBeEmpty();
            var page = titles.Last();
            ((ISolidColorBrush)page.Foreground!).Color.ShouldBe(ThemePalette.Get(ColorToken.Ink, ThemeKind.Light));

            app.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();

            ((ISolidColorBrush)page.Foreground!).Color.ShouldBe(ThemePalette.Get(ColorToken.Ink, ThemeKind.Dark));
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void A_new_system_accent_retints_open_views_and_a_red_one_is_refused()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;

        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;

            var chip = Panels.Chip("Fix", accent: true);
            var text = (TextBlock)chip.Child!;
            var window = new Window { Content = chip };
            window.Show();

            ((ISolidColorBrush)text.Foreground!).Color.ShouldBe(AccentPolicy.Fallback);

            var green = Color.FromRgb(0x10, 0x7C, 0x10);
            ThemeResources.ApplyAccent(app, green);
            Dispatcher.UIThread.RunJobs();

            ((ISolidColorBrush)text.Foreground!).Color.ShouldBe(AccentPolicy.Palette(green).OnLightBackground);
            app.TryGetResource("SystemAccentColor", ThemeVariant.Light, out var fluent).ShouldBeTrue();
            fluent.ShouldBe(green, "Fluent's own controls follow too");

            // Red is refused, live as well: the views go back to indigo, not to red.
            ThemeResources.ApplyAccent(app, Color.FromRgb(0xE8, 0x11, 0x23));
            Dispatcher.UIThread.RunJobs();

            ((ISolidColorBrush)text.Foreground!).Color.ShouldBe(AccentPolicy.Fallback);
            app.TryGetResource("SystemAccentColor", ThemeVariant.Light, out fluent).ShouldBeTrue();
            fluent.ShouldBe(AccentPolicy.Fallback);
        }
        finally
        {
            ThemeResources.ApplyAccent(app, null);
            app.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void High_contrast_has_its_own_accent_whatever_the_system_one_is()
    {
        Application.Current!.TryGetResource("SystemAccentColor", ThemeResources.HighContrast, out var accent).ShouldBeTrue();
        accent.ShouldBe(AccentPalette.HighContrastAccent);
    }

    [AvaloniaFact]
    public void High_contrast_is_a_variant_of_dark_with_solid_surfaces()
    {
        ThemeResources.HighContrast.InheritVariant.ShouldBe(ThemeVariant.Dark);
        ThemeResources.KindOf(ThemeResources.HighContrast).ShouldBe(ThemeKind.HighContrast);
        ThemeResources.KindOf(ThemeVariant.Dark).ShouldBe(ThemeKind.Dark);
        ThemeResources.KindOf(ThemeVariant.Light).ShouldBe(ThemeKind.Light);

        // Nothing translucent: a material showing wallpaper through is what high contrast asks
        // apps not to do.
        foreach (var token in Enum.GetValues<ColorToken>())
        {
            ThemeResources.Resolve(token, ThemeKind.HighContrast).A.ShouldBe((byte)0xFF, $"{token}");
        }
    }

    [AvaloniaFact]
    public void Contrast_preference_switches_to_high_contrast_and_back()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;

        try
        {
            ThemeResources.ApplyContrast(app, high: true);
            app.ActualThemeVariant.ShouldBe(ThemeResources.HighContrast);

            ThemeResources.ApplyContrast(app, high: false);
            app.RequestedThemeVariant.ShouldBe(ThemeVariant.Default);
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void Every_themed_token_reads_in_every_palette()
    {
        foreach (var kind in Enum.GetValues<ThemeKind>())
        {
            var surface = ThemeResources.Resolve(ColorToken.Surface, kind);

            AccentPolicy.Contrast(ThemeResources.Resolve(ColorToken.Ink, kind), surface)
                .ShouldBeGreaterThan(7, $"{kind}: body text");
            AccentPolicy.Contrast(ThemeResources.Resolve(ColorToken.InkMuted, kind), surface)
                .ShouldBeGreaterThan(4.5, $"{kind}: descriptions");
            AccentPolicy.Contrast(ThemeResources.Resolve(ColorToken.Accent, kind), surface)
                .ShouldBeGreaterThan(3, $"{kind}: the accent against a card");
        }
    }
}

/// <summary>The icons, and the rule that the redesign is drawn with them.</summary>
public sealed class IconTests
{
    public static TheoryData<string> AllIcons()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(Icons).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            data.Add(field.Name);
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllIcons))]
    public void Every_icon_parses_and_sits_on_the_20px_grid(string name)
    {
        var data = (string)typeof(Icons).GetField(name)!.GetRawConstantValue()!;

        data.ShouldStartWith("F1 ", Case.Sensitive, "non-zero fill, as the SVG was drawn");

        var geometry = Icons.Geometry(data);
        var bounds = geometry.Bounds;

        bounds.Width.ShouldBeGreaterThan(0, name);
        bounds.Height.ShouldBeGreaterThan(0, name);
        bounds.X.ShouldBeGreaterThanOrEqualTo(0, name);
        bounds.Y.ShouldBeGreaterThanOrEqualTo(0, name);
        bounds.Right.ShouldBeLessThanOrEqualTo(Icons.Size, name);
        bounds.Bottom.ShouldBeLessThanOrEqualTo(Icons.Size, name);
    }

    [AvaloniaFact]
    public void Parsed_geometry_is_shared()
    {
        Icons.Geometry(Icons.Home).ShouldBeSameAs(Icons.Geometry(Icons.Home));
    }

    [AvaloniaFact]
    public void An_icon_draws_in_the_inherited_text_colour()
    {
        var icon = new FluentIcon { Data = Icons.Mic };
        var host = new Border { Child = icon, [TextBlock.ForegroundProperty] = Brushes.Orange };
        var window = new Window { Content = host };
        window.Show();

        icon.Foreground.ShouldBe(Brushes.Orange);
        icon.Bounds.Width.ShouldBe(Tokens.Chrome.Icon);
    }
}

/// <summary>The hub's Windows 11 frame and navigation.</summary>
public sealed class FluentHubTests
{
    [AvaloniaFact]
    public void The_hub_asks_for_Mica_then_acrylic_then_nothing()
    {
        var hub = new HubWindow();
        hub.Show();

        hub.TransparencyLevelHint.ShouldBe(
            [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]);
    }

    [AvaloniaFact]
    public void Where_no_material_is_granted_the_window_is_solid_rather_than_black()
    {
        // A headless window is never granted a material, which is the remote-desktop case.
        var hub = new HubWindow();
        hub.Show();

        hub.ActualTransparencyLevel.ShouldBe(WindowTransparencyLevel.None);
        WindowChrome.HasMaterial(hub).ShouldBeFalse();
        ((ISolidColorBrush)hub.Background!).Color.ShouldBe(Tokens.Colors.Canvas);
        ((ISolidColorBrush)hub.TransparencyBackgroundFallback!).Color.ShouldBe(Tokens.Colors.Canvas);
    }

    [AvaloniaFact]
    public void High_contrast_asks_for_no_material_at_all()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;

        try
        {
            var hub = new HubWindow();
            hub.Show();

            app.RequestedThemeVariant = ThemeResources.HighContrast;
            Dispatcher.UIThread.RunJobs();

            hub.TransparencyLevelHint.ShouldBe([WindowTransparencyLevel.None]);
            ((ISolidColorBrush)hub.Background!).Color.ShouldBe(Colors.Black);

            app.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();

            hub.TransparencyLevelHint.ShouldContain(WindowTransparencyLevel.Mica, "back on when high contrast goes off");
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void The_content_runs_into_a_32px_title_bar_with_the_system_caption_buttons()
    {
        var hub = new HubWindow();
        hub.Show();

        hub.ExtendClientAreaToDecorationsHint.ShouldBeTrue();
        hub.ExtendClientAreaTitleBarHeightHint.ShouldBe(32);
        hub.WindowDecorations.ShouldBe(WindowDecorations.Full, "minimise, maximise and close stay Windows' own");

        var bar = hub.GetVisualDescendants().OfType<Control>()
            .Single(c => Avalonia.Controls.Chrome.WindowDecorationProperties.GetElementRole(c) == Avalonia.Input.WindowDecorationsElementRole.TitleBar);
        bar.Bounds.Height.ShouldBe(32);
    }

    [AvaloniaFact]
    public void The_sidebar_is_a_navigation_view_with_an_icon_for_every_page()
    {
        var hub = new HubWindow();
        hub.Show();

        var items = hub.Navigation.MenuItems.Concat(hub.Navigation.FooterMenuItems).Cast<FANavigationViewItem>().ToList();

        items.Select(i => i.Content as string).ShouldBe(["Home", "History", "Dictionary", "Prompts & snippets", "Settings"]);
        items.ShouldAllBe(i => i.IconSource is FAPathIconSource);
        hub.Navigation.FooterMenuItems.Cast<FANavigationViewItem>().Single().Content.ShouldBe("Settings");
    }

    [AvaloniaFact]
    public void Choosing_a_page_in_the_sidebar_shows_it()
    {
        var hub = new HubWindow();
        hub.Show();

        var history = hub.Navigation.MenuItems.Cast<FANavigationViewItem>().Single(i => Equals(i.Content, "History"));
        hub.Navigation.SelectedItem = history;
        Dispatcher.UIThread.RunJobs();

        hub.Section.ShouldBe(HubSection.History);

        hub.ShowSection(HubSection.Settings);
        hub.Navigation.SelectedItem.ShouldBe(hub.Navigation.FooterMenuItems[0]);
    }

    [AvaloniaFact]
    public void The_text_glyphs_the_old_sidebar_used_are_gone()
    {
        var hub = new HubWindow();
        hub.Show();

        var texts = hub.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        foreach (var glyph in new[] { "⌂", "¶", "⚙" }) texts.ShouldNotContain(glyph);
    }
}

/// <summary>Settings as Windows 11 lays them out.</summary>
public sealed class FluentSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-tests", Guid.NewGuid().ToString("N"));

    public FluentSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private AppSettings Settings() => new(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());

    private static T Show<T>(T view) where T : Control
    {
        var window = new Window { Width = 900, Height = 2400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    [AvaloniaFact]
    public void Settings_is_six_groups_of_setting_cards()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));

        view.Groups.Select(g => ((Caption)((StackPanel)g).Children[0]).Text)
            .ShouldBe(["General", "Transcription", "Clean-up", "Prompts & shortcuts", "Privacy & data", "About"]);

        view.GetVisualDescendants().OfType<FASettingsExpander>().Count().ShouldBeGreaterThanOrEqualTo(12);
    }

    [AvaloniaFact]
    public void No_check_box_remains_anywhere_in_settings()
    {
        using var settings = Settings();
        var window = new SettingsWindow(settings);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<CheckBox>().ShouldBeEmpty();
        window.GetVisualDescendants().OfType<ToggleSwitch>().Count().ShouldBeGreaterThanOrEqualTo(7);
    }

    [AvaloniaFact]
    public void The_prompt_editor_uses_a_toggle_switch_too()
    {
        var editor = new SnippetEditorWindow(null);
        editor.Show();

        editor.GetVisualDescendants().OfType<CheckBox>().ShouldBeEmpty();
        editor.WholeUtterance.ShouldBeOfType<ToggleSwitch>();
    }

    [AvaloniaFact]
    public void A_toggle_saves_its_setting()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));

        var fillers = view.GetVisualDescendants().OfType<FASettingsExpander>()
            .Single(e => Equals(e.Header, "Remove filler words"));
        var toggle = (ToggleSwitch)fillers.Footer!;

        toggle.IsChecked = false;
        settings.Data.RemoveFillers.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void The_key_is_a_combo_box_and_Right_Alt_warns_about_AltGr()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));

        var card = view.GetVisualDescendants().OfType<FASettingsExpander>().Single(e => Equals(e.Header, "Push-to-talk key"));
        var combo = (ComboBox)card.Footer!;

        combo.SelectedIndex = Choices.Keys.ToList().FindIndex(k => k.Label == "Right Alt");

        settings.Data.PushToTalkKey.ShouldBe(0xA5);
        view.GetVisualDescendants().OfType<TextBlock>().ShouldContain(t => t.Text == Choices.AltGrWarning && t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Engine_and_cloud_model_are_segmented_controls()
    {
        using var settings = Settings();
        var card = Show(new TranscriptionEngineCard(settings));

        card.ModeSelector.Segments.Count.ShouldBe(3);
        card.ModeSelector.SelectedLabel.ShouldBe("Offline (Parakeet)");
        card.ProfileSelector.Segments.Count.ShouldBe(2);

        card.ModeSelector.Pick(2);
        settings.Data.Engine.ShouldBe(EngineMode.AzureWithParakeetFallback);

        card.ProfileSelector.Pick(1);
        settings.Data.AzureActiveProfile.ShouldBe(CloudProfile.Fast);
    }

    [AvaloniaFact]
    public void Where_your_voice_goes_follows_the_engine()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));

        var card = view.GetVisualDescendants().OfType<FASettingsExpander>().Single(e => Equals(e.Header, "Where your voice goes"));
        card.Description.ShouldNotBeNull().ShouldContain("Nothing leaves this PC");

        settings.Update(settings.Data with { Engine = EngineMode.Azure, AzureEndpoint = "https://contoso.openai.azure.com" });
        Dispatcher.UIThread.RunJobs();

        card.Description.ShouldNotBeNull().ShouldContain("contoso.openai.azure.com");

        // A bare host, as the portal shows it, is where audio really goes: named, not "not set up".
        settings.Update(settings.Data with { AzureEndpoint = "contoso-ai.openai.azure.com" });
        Dispatcher.UIThread.RunJobs();
        card.Description.ShouldNotBeNull().ShouldContain("(contoso-ai.openai.azure.com)");

        settings.Update(settings.Data with { AzureEndpoint = "gateway.contoso.com:443" });
        Dispatcher.UIThread.RunJobs();
        card.Description.ShouldNotBeNull().ShouldContain("(gateway.contoso.com)");
    }

    [AvaloniaFact]
    public void The_standalone_window_hosts_the_same_page_in_the_same_frame()
    {
        using var settings = Settings();
        var window = new SettingsWindow(settings);
        window.Show();

        window.View.ShouldBeOfType<SettingsView>();
        window.TransparencyLevelHint.ShouldContain(WindowTransparencyLevel.Mica);
        window.ExtendClientAreaToDecorationsHint.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void The_about_group_credits_the_icons()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));

        view.GetVisualDescendants().OfType<FASettingsExpanderItem>()
            .ShouldContain(i => i.Description != null && i.Description.Contains("Fluent System Icons", StringComparison.Ordinal));
    }
}

/// <summary>The floating bar's restyle.</summary>
public sealed class FluentBarTests
{
    [AvaloniaFact]
    public void The_dot_changes_colour_at_once_rather_than_fading()
    {
        // A colour fade would paint a fresh in-between colour every frame, and each one would
        // land in the shared brush cache for good.
        var bar = new FlowBarWindow();
        bar.Show();

        bar.ApplyState(BarState.Listening);
        bar.Dot.DotColor.ShouldBe(Tokens.Colors.BarLive);

        bar.ApplyState(BarState.Processing);
        bar.Dot.DotColor.ShouldBe(Tokens.Colors.BarInkMuted);

        bar.ApplyState(BarState.Idle);
        bar.Dot.DotColor.ShouldBe(Tokens.Colors.BarDotIdle);
        bar.Pill.Background.ShouldBeSameAs(BrushCache.Get(Tokens.Colors.BarBody));
    }

    [AvaloniaFact]
    public void Red_on_the_bar_means_recording_and_nothing_else()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        foreach (var state in Enum.GetValues<BarState>())
        {
            bar.ApplyState(state);

            var dot = bar.Dot.DotColor;

            // Hands-free is recording too, with no key held.
            if (state is BarState.Listening or BarState.HandsFree) AccentPolicy.IsRed(dot).ShouldBeTrue();
            else AccentPolicy.IsRed(dot).ShouldBeFalse($"{state} must not be red");
        }
    }

    [AvaloniaFact]
    public void The_bar_body_is_dark_and_just_short_of_opaque()
    {
        // Wispr-style: a dark capsule at about 92 %, so a hint of what is under it shows at the
        // edge — without a blur behind an always-on-top window, which would be re-sampled every
        // time anything under it moved.
        Tokens.Colors.BarBody.A.ShouldBeInRange((byte)0xE6, (byte)0xF0);
        new FlowBarWindow().TransparencyLevelHint.ShouldBe([WindowTransparencyLevel.Transparent]);
    }

    [AvaloniaFact]
    public void A_fresh_bar_is_bottom_centre()
    {
        var bar = new FlowBarWindow();
        bar.Edge.ShouldBe(ScreenEdge.Bottom);
        bar.Offset.ShouldBe(FlowBarPlacement.DefaultOffset);
        FlowBarPlacement.DefaultOffset.ShouldBe(0.5);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "murmur-tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }
}

/// <summary>First-run setup.</summary>
public sealed class OnboardingWindowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-tests", Guid.NewGuid().ToString("N"));

    public OnboardingWindowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private Composition Compose(AppSettings settings) => Composition.ForPreview(
        settings,
        new DictionaryFile(Path.Combine(_directory, "dictionary.txt")),
        new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
        secrets: new InMemorySecretStore());

    [AvaloniaFact]
    public void The_route_follows_the_engine_chosen()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var window = new OnboardingWindow(Compose(settings));
        window.Show();

        window.Route.ShouldBe([OnboardingStep.Welcome, OnboardingStep.Engine, OnboardingStep.Offline, OnboardingStep.Hotkey, OnboardingStep.Done]);

        window.GoTo(OnboardingStep.Engine);
        var azure = window.GetVisualDescendants().OfType<ChoiceCard>().Single(c => c.Title == "Azure OpenAI");
        azure.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        settings.Data.Engine.ShouldBe(EngineMode.Azure);
        azure.IsSelected.ShouldBeTrue();
        window.Route.ShouldContain(OnboardingStep.Azure);
        window.Route.ShouldNotContain(OnboardingStep.Offline);
    }

    [AvaloniaFact]
    public void The_Azure_page_saves_the_fields_and_the_key_but_never_the_key_in_settings()
    {
        const string key = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var composition = Compose(settings);
        var window = new OnboardingWindow(composition);
        window.Show();
        window.GoTo(OnboardingStep.Azure);

        var fields = window.GetVisualDescendants().OfType<TextBox>().ToList();
        fields[0].Text = "https://contoso.openai.azure.com";
        fields[1].Text = "transcribe";
        fields[3].Text = key;

        window.SaveAzureFields().ShouldBeTrue();

        settings.Data.AzureEndpoint.ShouldBe("https://contoso.openai.azure.com");
        settings.Data.AzureDeploymentAccurate.ShouldBe("transcribe");
        composition.Cloud.Key.HasKey.ShouldBeTrue();
        fields[3].Text.ShouldBeEmpty("the field does not keep the key once it is saved");
        settings.Data.ToString().ShouldNotContain(key);
    }

    [AvaloniaFact]
    public void Next_stays_on_the_Azure_page_when_the_key_is_refused()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        settings.Update(settings.Data with { Engine = EngineMode.Azure });
        var composition = Compose(settings);
        var window = new OnboardingWindow(composition);
        window.Show();
        window.GoTo(OnboardingStep.Azure);

        var fields = window.GetVisualDescendants().OfType<TextBox>().ToList();
        fields[3].Text = "two keys";

        window.GoNext();

        window.Step.ShouldBe(OnboardingStep.Azure, "the reason the key was refused is on this page");
        composition.Cloud.Key.HasKey.ShouldBeFalse();

        fields[3].Text = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        window.GoNext();

        window.Step.ShouldBe(OnboardingStep.Hotkey);
        composition.Cloud.Key.HasKey.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void The_setup_page_keeps_its_place_when_the_engine_changes_underneath_it()
    {
        // The tray and Settings write the same settings: the Azure page can be on show while the
        // engine is Parakeet, and the route no longer contains it.
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var window = new OnboardingWindow(Compose(settings));
        window.Show();

        window.GoTo(OnboardingStep.Azure);
        window.Route.ShouldNotContain(OnboardingStep.Azure);

        window.GoNext();
        window.Step.ShouldBe(OnboardingStep.Hotkey);

        window.GoTo(OnboardingStep.Azure);
        window.GoBack();
        window.Step.ShouldBe(OnboardingStep.Engine);
    }

    [AvaloniaFact]
    public void Skipping_or_closing_from_the_Azure_page_keeps_what_was_typed()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var skipped = new OnboardingWindow(Compose(settings));
        skipped.Show();
        skipped.GoTo(OnboardingStep.Azure);
        skipped.GetVisualDescendants().OfType<TextBox>().First().Text = "https://contoso.openai.azure.com";
        skipped.Finish();

        settings.Data.AzureEndpoint.ShouldBe("https://contoso.openai.azure.com");

        var closed = new OnboardingWindow(Compose(settings));
        closed.Show();
        closed.GoTo(OnboardingStep.Azure);
        closed.GetVisualDescendants().OfType<TextBox>().First().Text = "https://fabrikam.openai.azure.com";
        closed.Close();

        settings.Data.AzureEndpoint.ShouldBe("https://fabrikam.openai.azure.com");
    }

    [AvaloniaFact]
    public void Typing_into_the_try_box_by_hand_is_not_a_pass()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var window = new OnboardingWindow(Compose(settings));
        window.Show();
        window.GoTo(OnboardingStep.Hotkey);

        window.GetVisualDescendants().OfType<TextBox>().Single().Text = "hello";

        window.CheckPassed.ShouldBeFalse("only a dictation the engine completed proves the chain works");
    }

    [AvaloniaFact]
    public void The_live_check_says_when_the_key_is_held_and_when_it_heard_you()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var window = new OnboardingWindow(Compose(settings));
        window.Show();
        window.GoTo(OnboardingStep.Hotkey);

        window.CheckPassed.ShouldBeFalse();

        window.OnEngineState(DictationState.Recording);
        window.CheckStatus.ShouldContain("Listening");

        window.Heard("Hello.");
        window.CheckPassed.ShouldBeTrue();
        window.CheckStatus.ShouldContain("heard you say hello");
    }

    [AvaloniaFact]
    public void Silence_is_reported_rather_than_passed()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var window = new OnboardingWindow(Compose(settings));
        window.Show();
        window.GoTo(OnboardingStep.Hotkey);

        window.Heard("  ");
        window.CheckPassed.ShouldBeFalse();
        window.CheckStatus.ShouldContain("Nothing was heard");
    }

    [AvaloniaFact]
    public void Finishing_or_skipping_records_that_setup_is_done_and_closing_early_does_not()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        Onboarding.IsNeeded(settings.Data).ShouldBeTrue();

        var early = new OnboardingWindow(Compose(settings));
        early.Show();
        early.Close();
        Onboarding.IsNeeded(settings.Data).ShouldBeTrue("closing the window is not finishing it");

        var window = new OnboardingWindow(Compose(settings));
        var finished = false;
        window.Finished += (_, _) => finished = true;
        window.Show();
        window.Finish();

        finished.ShouldBeTrue();
        Onboarding.IsNeeded(settings.Data).ShouldBeFalse();
        settings.Data.SettingsVersion.ShouldBe(SettingsData.CurrentVersion);
    }

    [AvaloniaFact]
    public void Every_step_builds_in_every_theme()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());

        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeResources.HighContrast })
            {
                app.RequestedThemeVariant = theme;
                var window = new OnboardingWindow(Compose(settings));
                window.Show();

                foreach (var step in Enum.GetValues<OnboardingStep>())
                {
                    window.GoTo(step);
                    window.Step.ShouldBe(step);
                }

                window.Close();
            }
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }
}
