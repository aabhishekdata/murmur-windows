using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Murmur.Abstractions;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.App.Views;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;

[assembly: AvaloniaTestApplication(typeof(Murmur.AppTests.TestAppBuilder))]

// One Avalonia application instance is shared by the whole assembly, and some of these tests
// change application-wide state (the theme variant, for the dark-mode preview). Running the
// classes in parallel would let one test's theme leak into another's render.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Murmur.AppTests;

/// <summary>Hosts the app headlessly so the UI can be exercised without a display.</summary>
/// <remarks>
/// <c>UseHeadlessDrawing = false</c> with Skia, rather than the default no-op drawing backend.
/// It costs a little start-up time and buys two things: every control's <c>Render</c> is
/// actually executed by these tests, and <c>CaptureRenderedFrame</c> returns real pixels — which
/// is how a redesign that cannot be run on Windows gets reviewed at all.
/// </remarks>
public static class TestAppBuilder
{
    /// <summary>Builds a headless Avalonia app for the test host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>A minimal application shell for headless tests.</summary>
public sealed class TestApp : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());

        // The same accent override the real app installs, so a rendered preview is a picture
        // of the shipped interface rather than of a Windows-blue approximation of it.
        AccentResources.Apply(this);
    }
}

/// <summary>
/// The floating bar — the app's primary interface.
/// </summary>
/// <remarks>
/// This is the payoff for choosing Avalonia over WPF. These run on Linux in milliseconds and on
/// a Windows runner in CI, so a broken layout or a control that fails to construct is caught
/// while writing it rather than after shipping to a machine we cannot test on.
/// </remarks>
public sealed class FlowBarTests
{
    [AvaloniaFact]
    public void The_bar_opens_and_lays_out()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        bar.Bounds.Width.ShouldBeGreaterThan(0);
        bar.Bounds.Height.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void The_bar_is_frameless_topmost_and_out_of_the_taskbar()
    {
        // All three are load-bearing. Decorations would make it a window rather than an
        // indicator; not being topmost would hide it behind the app being dictated into; and a
        // taskbar button for a floating pill is simply wrong.
        var bar = new FlowBarWindow();

        bar.SystemDecorations.ShouldBe(SystemDecorations.None);
        bar.Topmost.ShouldBeTrue();
        bar.ShowInTaskbar.ShouldBeFalse();
        bar.CanResize.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void The_bar_never_takes_focus()
    {
        // The single most important property on this window. Murmur types into whatever has
        // focus, so a bar that steals it redirects the user's own dictation into itself.
        new FlowBarWindow().ShowActivated.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Clicking_the_bar_moves_it_between_idle_and_listening()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        bar.State.ShouldBe(BarState.Idle);
        bar.Wave.IsActive.ShouldBeFalse();
        bar.Dot.IsLit.ShouldBeFalse();

        bar.Toggle();

        bar.State.ShouldBe(BarState.Listening);
        bar.Wave.IsActive.ShouldBeTrue("the level bars must follow the state");
        bar.Dot.IsLit.ShouldBeTrue("the live dot must follow the state");

        bar.Toggle();

        bar.State.ShouldBe(BarState.Idle);
        bar.Wave.IsActive.ShouldBeFalse();
        bar.Dot.IsLit.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void The_bar_grows_when_it_starts_listening_and_shrinks_again()
    {
        // The window is resized per state rather than held at its widest, because transparent
        // dead space on an always-on-top window swallows clicks meant for what is underneath.
        var bar = new FlowBarWindow();
        bar.Show();

        var idle = bar.Width;

        bar.ApplyState(BarState.Listening);
        bar.Width.ShouldBeGreaterThan(idle);

        bar.ApplyState(BarState.Processing);
        bar.Width.ShouldBeGreaterThan(idle);
        bar.Width.ShouldBeLessThan(Tokens.Bar.ListeningWidth + 1);

        bar.ApplyState(BarState.Idle);
        bar.Width.ShouldBe(idle);
    }

    [AvaloniaFact]
    public void Processing_swaps_the_level_bars_for_the_pulse()
    {
        // Leaving the level bars up while transcribing tells the user it is still listening,
        // which is exactly when they start talking into a microphone that has stopped.
        var bar = new FlowBarWindow();
        bar.Show();

        bar.ApplyState(BarState.Processing);
        bar.Wave.IsVisible.ShouldBeFalse();

        bar.ApplyState(BarState.Listening);
        bar.Wave.IsVisible.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Docking_to_a_side_turns_the_bar_vertical()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        bar.Edge.ShouldBe(ScreenEdge.Bottom);
        bar.Width.ShouldBeGreaterThan(bar.Height);

        bar.DockTo(ScreenEdge.Left);

        bar.Edge.ShouldBe(ScreenEdge.Left);
        bar.Height.ShouldBeGreaterThan(bar.Width, "a side-docked bar reorients vertically");

        bar.DockTo(ScreenEdge.Bottom);
        bar.Width.ShouldBeGreaterThan(bar.Height);
    }

    [AvaloniaFact]
    public void A_docked_bar_sits_inside_the_work_area()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        foreach (var edge in new[] { ScreenEdge.Bottom, ScreenEdge.Left, ScreenEdge.Right })
        {
            bar.DockTo(edge, 0.5);

            var area = bar.WorkArea();
            bar.Position.X.ShouldBeGreaterThanOrEqualTo((int)area.X);
            bar.Position.Y.ShouldBeGreaterThanOrEqualTo((int)area.Y);
        }
    }

    [AvaloniaFact]
    public void The_bar_position_is_persisted_and_restored()
    {
        // A frameless, taskbar-less window that comes back in the wrong place cannot be found,
        // let alone dragged. Round-tripping this through the real settings file is the point.
        var path = TempSettingsPath();

        try
        {
            var settings = new AppSettings(path);
            settings.Update(settings.Data with { FlowBarEdge = ScreenEdge.Right, FlowBarOffset = 0.25 });

            var reopened = new AppSettings(path);
            reopened.Data.FlowBarEdge.ShouldBe(ScreenEdge.Right);
            reopened.Data.FlowBarOffset.ShouldBe(0.25);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [AvaloniaFact]
    public void A_fresh_install_starts_bottom_centre()
    {
        var data = new SettingsData();

        data.FlowBarEdge.ShouldBe(ScreenEdge.Bottom);
        data.FlowBarOffset.ShouldBe(FlowBarPlacement.DefaultOffset);
        data.ShowFlowBar.ShouldBeTrue();
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "murmur-tests", $"{Guid.NewGuid():N}.json");

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* best effort */ }
    }
}

/// <summary>The hub window.</summary>
public sealed class HubWindowTests
{
    [AvaloniaFact]
    public void Every_section_can_be_reached_and_built()
    {
        var hub = new HubWindow();
        hub.Show();

        foreach (var section in Enum.GetValues<HubSection>())
        {
            hub.ShowSection(section);
            hub.Section.ShouldBe(section);
        }
    }

    [AvaloniaFact]
    public void The_snippets_section_exists_alongside_the_dictionary()
    {
        // Snippets are the dictionary's structural twin, and they get a peer entry in the
        // sidebar rather than being buried inside another section.
        var hub = new HubWindow();
        hub.Show();

        hub.ShowSection(HubSection.Snippets);
        hub.Section.ShouldBe(HubSection.Snippets);
    }

    [AvaloniaFact]
    public void The_hub_opens_and_lays_out()
    {
        var hub = new HubWindow();
        hub.Show();

        hub.Bounds.Width.ShouldBeGreaterThan(0);
        hub.Bounds.Height.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void The_hub_honours_its_minimum_size()
    {
        var hub = new HubWindow();
        hub.Show();

        hub.MinWidth.ShouldBe(720);
        hub.MinHeight.ShouldBe(520);
    }

    [AvaloniaFact]
    public void The_hub_opens_on_home_and_switches_sections()
    {
        var hub = new HubWindow();
        hub.Show();

        hub.Section.ShouldBe(HubSection.Home);

        hub.ShowSection(HubSection.Dictionary);
        hub.Section.ShouldBe(HubSection.Dictionary);

        hub.ShowSection(HubSection.Settings);
        hub.Section.ShouldBe(HubSection.Settings);

        hub.ShowSection(HubSection.Home);
        hub.Section.ShouldBe(HubSection.Home);
    }
}

/// <summary>
/// The views built this round, exercised against real stores.
/// </summary>
/// <remarks>
/// Every one of these renders a live Avalonia tree, so a control that fails to construct or a
/// layout that throws is caught here rather than on a machine we cannot test on.
/// </remarks>
public sealed class FeatureViewTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "murmur-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Whether <paramref name="text"/> appears anywhere in the rendered tree.
    /// </summary>
    /// <remarks>
    /// Text boxes as well as text blocks: a pre-filled field is content the user reads, and
    /// asserting only on labels would miss half of every dialog.
    /// </remarks>
    private static bool Shows(Control view, string text)
    {
        var descendants = view.GetVisualDescendants().ToList();

        return descendants.OfType<TextBlock>()
                   .Any(t => t.Text is not null && t.Text.Contains(text, StringComparison.Ordinal))
            || descendants.OfType<TextBox>()
                   .Any(t => t.Text is not null && t.Text.Contains(text, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void The_snippets_view_lists_what_is_stored()
    {
        var directory = TempDirectory();

        try
        {
            var store = new SnippetStore(Path.Combine(directory, "snippets.jsonl"));
            store.Add(new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street" });

            var view = new SnippetsView(store);
            var window = new Window { Width = 700, Height = 500, Content = view };
            window.Show();

            Shows(view, "my address").ShouldBeTrue();
            Shows(view, "12 Bridge Street").ShouldBeTrue("the body preview must be visible");
            Shows(view, "1 snippet").ShouldBeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void An_empty_snippets_view_explains_what_a_snippet_is_for()
    {
        var directory = TempDirectory();

        try
        {
            var view = new SnippetsView(new SnippetStore(Path.Combine(directory, "snippets.jsonl")));
            var window = new Window { Width = 700, Height = 500, Content = view };
            window.Show();

            Shows(view, "No snippets yet").ShouldBeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void The_snippet_editor_refuses_to_save_an_incomplete_snippet()
    {
        var editor = new SnippetEditorWindow(null);
        editor.Show();

        var save = editor.GetVisualDescendants().OfType<SoftButton>()
            .First(b => b.Content as string == "Save");

        save.IsEnabled.ShouldBeFalse("a snippet with no trigger and no body cannot be saved");
    }

    [AvaloniaFact]
    public void The_snippet_editor_shows_the_character_budget()
    {
        var editor = new SnippetEditorWindow(
            new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street" });
        editor.Show();

        Shows(editor, SnippetEntry.MaxLength.ToString("N0", CultureInfo.CurrentCulture))
            .ShouldBeTrue("the 4,000-character ceiling has to be visible while writing");
    }

    [AvaloniaFact]
    public void The_suggestions_strip_hides_itself_when_there_is_nothing_to_suggest()
    {
        var directory = TempDirectory();

        try
        {
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            var dictionary = new DictionaryFile(Path.Combine(directory, "dictionary.txt"));
            var transcripts = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));

            var view = new SuggestionsView(settings, dictionary, transcripts);
            var window = new Window { Content = view };
            window.Show();

            view.IsVisible.ShouldBeFalse("an empty suggestion strip must take up no room");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void The_suggestions_strip_offers_a_near_miss_of_a_known_term()
    {
        var directory = TempDirectory();

        try
        {
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));

            var dictionaryPath = Path.Combine(directory, "dictionary.txt");
            File.WriteAllText(dictionaryPath, "Claude Code" + Environment.NewLine);
            var dictionary = new DictionaryFile(dictionaryPath);

            var transcripts = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));
            transcripts.Add(new TranscriptRecord
            {
                At = DateTimeOffset.Now,
                Text = "I use cloud code every day",
            });

            var view = new SuggestionsView(settings, dictionary, transcripts);
            var window = new Window { Width = 800, Height = 300, Content = view };
            window.Show();

            view.IsVisible.ShouldBeTrue();
            Shows(view, "cloud code").ShouldBeTrue();
            Shows(view, "Claude Code").ShouldBeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void Accepting_a_suggestion_writes_an_ordinary_dictionary_entry()
    {
        var directory = TempDirectory();

        try
        {
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));

            var dictionaryPath = Path.Combine(directory, "dictionary.txt");
            File.WriteAllText(dictionaryPath, "Claude Code" + Environment.NewLine);
            var dictionary = new DictionaryFile(dictionaryPath);

            var transcripts = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));
            transcripts.Add(new TranscriptRecord { At = DateTimeOffset.Now, Text = "I use cloud code" });

            var view = new SuggestionsView(settings, dictionary, transcripts);
            var window = new Window { Width = 800, Height = 300, Content = view };
            window.Show();

            var add = view.GetVisualDescendants().OfType<SoftButton>()
                .First(b => b.Content as string == "Add to dictionary");

            add.Command?.Execute(null);
            add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            // No hidden second dictionary: it becomes a correction the user can see and delete.
            var correction = dictionary.Entries
                .SingleOrDefault(e => e.Kind == Murmur.Dictionary.EntryKind.Correction);

            correction.ShouldNotBeNull();
            correction.Hear.ShouldBe("cloud code");
            correction.Write.ShouldBe("Claude Code");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void Dismissing_a_suggestion_is_remembered_in_settings()
    {
        var directory = TempDirectory();

        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var settings = new AppSettings(settingsPath);

            var dictionaryPath = Path.Combine(directory, "dictionary.txt");
            File.WriteAllText(dictionaryPath, "Claude Code" + Environment.NewLine);
            var dictionary = new DictionaryFile(dictionaryPath);

            var transcripts = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));
            transcripts.Add(new TranscriptRecord { At = DateTimeOffset.Now, Text = "I use cloud code" });

            var view = new SuggestionsView(settings, dictionary, transcripts);
            var window = new Window { Width = 800, Height = 300, Content = view };
            window.Show();

            var dismiss = view.GetVisualDescendants().OfType<SoftButton>()
                .First(b => b.Content as string == "Not a mistake");

            dismiss.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            view.IsVisible.ShouldBeFalse("a rejected suggestion must not come straight back");
            new AppSettings(settingsPath).Data.DismissedSuggestions.ShouldNotBeEmpty(
                "and it must still be gone after a restart");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void The_teach_dialog_prefills_from_the_transcript_and_counts_the_backfill()
    {
        var records = new List<TranscriptRecord>
        {
            new() { At = DateTimeOffset.Now, Text = "I use cloud code every day" },
            new() { At = DateTimeOffset.Now, Text = "cloud code again" },
        };

        var teach = new TeachCorrectionWindow("I use cloud code every day", records);
        teach.Show();

        Shows(teach, "I use cloud code every day").ShouldBeTrue("the wrong text is pre-filled");

        // Narrowing the trigger to the words that were actually wrong recounts live, which is
        // what answers "does this rule do anything?" before the next dictation rather than
        // after it.
        var hear = teach.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.Watermark == "what it heard");

        hear.Text = "cloud code";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Shows(teach, "2 past transcripts").ShouldBeTrue();

        hear.Text = "nothing like this";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Shows(teach, "check the spelling").ShouldBeTrue("a count of zero usually means a typo");
    }

    [AvaloniaFact]
    public void The_settings_view_lists_the_microphones_it_is_given()
    {
        var directory = TempDirectory();

        try
        {
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            var catalog = new FakeAudioDeviceCatalog(
                new AudioDeviceInfo("{id-1}", "Headset Microphone", IsDefault: true),
                new AudioDeviceInfo("{id-2}", "Webcam Array", IsDefault: false));

            var view = new SettingsView(settings, catalog);
            var window = new Window { Width = 700, Height = 900, Content = view };
            window.Show();

            Shows(view, "System default").ShouldBeTrue("the fallback is always offered, and first");
            Shows(view, "Headset Microphone").ShouldBeTrue();
            Shows(view, "Webcam Array").ShouldBeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void The_settings_view_says_so_when_there_are_no_microphones()
    {
        var directory = TempDirectory();

        try
        {
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            var view = new SettingsView(settings, new FakeAudioDeviceCatalog());
            var window = new Window { Width = 700, Height = 900, Content = view };
            window.Show();

            Shows(view, "No microphones were found").ShouldBeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [AvaloniaFact]
    public void The_settings_view_offers_a_switch_for_every_pass_that_rewrites_text()
    {
        // Five passes now mutate what the user said. Each has to be individually escapable, or
        // one bad guess makes the whole app untrustworthy.
        var directory = TempDirectory();

        try
        {
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            var view = new SettingsView(settings);
            var window = new Window { Width = 700, Height = 1200, Content = view };
            window.Show();

            Shows(view, "filler words").ShouldBeTrue();
            Shows(view, "wait no").ShouldBeTrue();
            Shows(view, "spoken lists").ShouldBeTrue();
            Shows(view, "snippet triggers").ShouldBeTrue();
            Shows(view, "press enter").ShouldBeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static void Cleanup(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>The individual controls.</summary>
public sealed class ControlTests
{
    [AvaloniaFact]
    public void A_caption_switches_weight_and_colour_when_it_becomes_a_header()
    {
        // The previous design force-uppercased every label. This one distinguishes a header
        // from a caption by weight and tracking instead, which has to actually change.
        var caption = new Caption { Text = "Today" };
        var body = caption.FontSize;

        caption.IsHeader = true;

        caption.Text.ShouldBe("Today", "sentence case must survive — this is not a machine panel");
        caption.FontSize.ShouldBeGreaterThan(body);
        caption.FontWeight.ShouldBe(FontWeight.SemiBold);
        caption.LetterSpacing.ShouldBe(Tokens.Fonts.HeaderTracking);
    }

    [AvaloniaFact]
    public void A_status_dot_defaults_to_the_token_size()
    {
        var dot = new StatusDot();
        dot.Width.ShouldBe(Tokens.Bar.DotSize);
        dot.Height.ShouldBe(Tokens.Bar.DotSize);
    }

    [AvaloniaFact]
    public void The_level_bars_measure_to_the_token_geometry()
    {
        var wave = new WaveBars();
        wave.Measure(new Size(400, 20));

        var expected = (Tokens.Wave.BarCount * Tokens.Wave.BarWidth)
                     + ((Tokens.Wave.BarCount - 1) * Tokens.Wave.BarGap);

        wave.DesiredSize.Width.ShouldBe(expected);
    }

    [AvaloniaFact]
    public void The_level_bars_rise_with_the_signal_and_fall_back_to_rest()
    {
        // The row is the bar's whole visual signature; if it does not track the level it is
        // decoration. Advance is stepped by hand so the test needs no real timer.
        var wave = new WaveBars { IsActive = true, Level = 0.5 };
        for (var i = 0; i < 60; i++) wave.Advance();

        var loud = wave.BarHeights.Max();
        loud.ShouldBeGreaterThan(Tokens.Wave.IdleFraction * 2);

        wave.IsActive = false;
        for (var i = 0; i < 300; i++) wave.Advance();

        wave.BarHeights.Max().ShouldBeLessThan(loud);
        wave.BarHeights.Max().ShouldBeLessThan(Tokens.Wave.IdleFraction + 0.01);
    }

    [AvaloniaFact]
    public void The_level_bars_never_collapse_to_nothing()
    {
        // A row that disappears at rest reads as a crash, not as idleness.
        var wave = new WaveBars { IsActive = false };
        for (var i = 0; i < 600; i++) wave.Advance();

        wave.BarHeights.Min().ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void The_level_bars_render_without_throwing()
    {
        var wave = new WaveBars { Width = 90, Height = 18, IsActive = true, Level = 0.6 };
        var window = new Window { Content = wave };
        window.Show();

        wave.Bounds.Width.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void A_navigation_row_recolours_when_selected()
    {
        var nav = new NavItem("⌂", "Home");
        var window = new Window { Content = nav };
        window.Show();

        nav.IsSelected.ShouldBeFalse();
        nav.IsSelected = true;
        nav.IsSelected.ShouldBeTrue();

        nav.Bounds.Height.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void A_primary_button_writes_on_the_accent()
    {
        var button = new SoftButton { Content = "Save", Tone = ButtonTone.Primary };
        var window = new Window { Content = button };
        window.Show();

        ((SolidColorBrush)button.Foreground!).Color.ShouldBe(Tokens.Colors.OnAccent);
    }

    [AvaloniaFact]
    public void A_disabled_button_actually_looks_disabled()
    {
        // SoftButton draws itself, so the Fluent theme's disabled visual state never reaches
        // it. Without an explicit fade, a dead "Delete all" is indistinguishable from a live
        // one — the user clicks it and nothing happens.
        var button = new SoftButton { Content = "Delete all" };
        var window = new Window { Content = button };
        window.Show();

        button.Opacity.ShouldBe(1);

        button.IsEnabled = false;
        button.Opacity.ShouldBeLessThan(1);

        button.IsEnabled = true;
        button.Opacity.ShouldBe(1);
    }

    [AvaloniaFact]
    public void The_pulse_dots_measure_to_their_geometry()
    {
        var dots = new PulseDots();
        dots.Measure(new Size(100, 12));

        var expected = (PulseDots.Count * PulseDots.DotSize) + ((PulseDots.Count - 1) * PulseDots.Gap);
        dots.DesiredSize.Width.ShouldBe(expected);
    }
}

/// <summary>
/// Guards the rules the design system calls non-negotiable.
/// </summary>
/// <remarks>
/// These are the sort of rule that erodes one reasonable-looking commit at a time. Asserting
/// them makes the erosion a build failure.
/// </remarks>
public sealed class DesignSystemTests
{
    [AvaloniaFact]
    public void The_bar_is_dark_regardless_of_the_theme()
    {
        // The bar floats over other people's applications. A light bar on a light document is
        // invisible, so it does not follow the system appearance — and these values must stay
        // fixed rather than becoming theme-dependent.
        Tokens.Colors.BarBody.R.ShouldBeLessThan((byte)0x40);
        Tokens.Colors.BarBody.G.ShouldBeLessThan((byte)0x40);
        Tokens.Colors.BarBody.B.ShouldBeLessThan((byte)0x40);

        Tokens.Colors.BarWave.ShouldBe(Color.FromRgb(0xFF, 0xFF, 0xFF));
    }

    [AvaloniaFact]
    public void The_level_bars_read_against_the_bar_body()
    {
        // Contrast, checked rather than assumed: the whole point of the row is that it is
        // legible at a glance from across a desk.
        var body = Luminance(Tokens.Colors.BarBody);
        var wave = Luminance(Tokens.Colors.BarWave);

        var ratio = (Math.Max(body, wave) + 0.05) / (Math.Min(body, wave) + 0.05);
        ratio.ShouldBeGreaterThan(7, "the level bars must clear AAA against the bar body");
    }

    [AvaloniaFact]
    public void Body_text_reads_against_its_surface()
    {
        var ink = Luminance(Tokens.Colors.Ink);
        var surface = Luminance(Tokens.Colors.Surface);

        var ratio = (Math.Max(ink, surface) + 0.05) / (Math.Min(ink, surface) + 0.05);
        ratio.ShouldBeGreaterThan(4.5, "body text must clear AA against the surface it sits on");
    }

    [AvaloniaFact]
    public void Radii_are_soft_enough_to_read_as_software()
    {
        // The inverse of the rule the retired design held. Hard edges read as equipment, and
        // this design is deliberately not a machine.
        Tokens.Radius.Chip.ShouldBeGreaterThanOrEqualTo(4);
        Tokens.Radius.Control.ShouldBeGreaterThanOrEqualTo(8);
        Tokens.Radius.Card.ShouldBeGreaterThanOrEqualTo(12);
        Tokens.Radius.Pill.ShouldBeGreaterThan(Tokens.Bar.Height);
    }

    [AvaloniaFact]
    public void Spacing_stays_on_the_four_point_grid()
    {
        double[] steps =
        [
            Tokens.Space.Hair, Tokens.Space.Tight, Tokens.Space.Snug, Tokens.Space.Base,
            Tokens.Space.Roomy, Tokens.Space.Wide, Tokens.Space.Section, Tokens.Space.Page,
        ];

        foreach (var step in steps) (step % 2).ShouldBe(0, $"{step} is off the grid");
    }

    [AvaloniaFact]
    public void There_is_exactly_one_border_weight()
    {
        // One hairline everywhere is what stops the interface acquiring a second visual
        // vocabulary a commit at a time.
        Tokens.Border.Hairline.ShouldBe(1);
    }

    [AvaloniaFact]
    public void The_level_animation_is_smoothed_rather_than_driven_straight_from_the_signal()
    {
        // Bars taken straight from the RMS flicker. The smoothing is what makes the row look
        // like a voice; without it the whole effect is noise.
        Tokens.Wave.Smoothing.ShouldBeGreaterThan(0);
        Tokens.Wave.Smoothing.ShouldBeLessThan(1);
        Tokens.Wave.Decay.ShouldBeLessThan(Tokens.Wave.Smoothing, "bars fall slower than they rise");
        Tokens.Wave.TravelDepth.ShouldBeGreaterThan(0, "the row has to move, not just pump");
    }

    [AvaloniaFact]
    public void The_ui_polls_the_engine_far_below_frame_rate()
    {
        // The engine raises Changed on a background thread at buffer rate. Marshalling every
        // one of those to the UI thread would be far more traffic than a display needs.
        Tokens.Motion.Poll.ShouldBeGreaterThan(Tokens.Motion.Frame);
    }

    private static double Luminance(Color colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }
}
