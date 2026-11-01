using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.Abstractions;
using Murmur.App;
using Murmur.App.Controls;
using Murmur.App.Views;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.AppTests;

/// <summary>Saved prompts in the UI: the editor's switch, the shortcut recorder and the list.</summary>
public sealed class PromptUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-tests", Guid.NewGuid().ToString("N"));

    public PromptUiTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private SnippetStore Store() => new(Path.Combine(_directory, "snippets.jsonl"));

    private static bool Shows(Control view, string text)
    {
        var descendants = view.GetVisualDescendants().ToList();

        return descendants.OfType<TextBlock>()
                   .Any(t => t.Text is not null && t.Text.Contains(text, StringComparison.Ordinal))
            || descendants.OfType<TextBox>()
                   .Any(t => t.Text is not null && t.Text.Contains(text, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void A_new_entry_starts_as_a_prompt_said_on_its_own()
    {
        var editor = new SnippetEditorWindow(null);
        editor.Show();

        editor.WholeUtterance.IsChecked.ShouldBe(true);
        editor.Draft.Match.ShouldBe(SnippetMatch.WholeUtterance);
        editor.Draft.Hotkey.ShouldBeNull();
        Shows(editor, "Say only this").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void An_existing_inline_snippet_stays_inline()
    {
        var editor = new SnippetEditorWindow(new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street" });
        editor.Show();

        editor.WholeUtterance.IsChecked.ShouldBe(false);
        editor.Draft.Match.ShouldBe(SnippetMatch.Inline);
    }

    [AvaloniaFact]
    public void The_recorder_takes_a_combination_and_Esc_clears_it()
    {
        var recorder = new ShortcutRecorder(null);
        var window = new Window { Content = recorder };
        window.Show();
        var changes = 0;
        recorder.ChordChanged += (_, _) => changes++;

        recorder.Accept(Key.LeftCtrl, KeyModifiers.Control).ShouldBeTrue();
        recorder.Chord.ShouldBeNull("a modifier on its own only previews");
        recorder.DisplayText.ShouldBe("Ctrl+…");

        recorder.Accept(Key.D1, KeyModifiers.Control | KeyModifiers.Alt).ShouldBeTrue();
        recorder.Chord.ShouldBe(HotkeyChords.Parse("Ctrl+Alt+1"));
        recorder.DisplayText.ShouldBe("Ctrl+Alt+1");

        recorder.Accept(Key.Tab, KeyModifiers.None).ShouldBeFalse("Tab still moves focus");

        recorder.Accept(Key.Escape, KeyModifiers.None).ShouldBeTrue();
        recorder.Chord.ShouldBeNull();
        changes.ShouldBe(2);
    }

    [AvaloniaTheory]
    [InlineData(Key.A, 0x41)]
    [InlineData(Key.D9, 0x39)]
    [InlineData(Key.F24, 0x87)]
    [InlineData(Key.NumPad3, 0x63)]
    [InlineData(Key.OemPlus, 0xBB)]
    [InlineData(Key.PageDown, 0x22)]
    public void Avalonia_keys_map_to_virtual_keys(Key key, int virtualKey) =>
        ShortcutRecorder.VirtualKeyOf(key).ShouldBe(virtualKey);

    [AvaloniaFact]
    public void Keys_a_shortcut_cannot_use_are_ignored()
    {
        ShortcutRecorder.VirtualKeyOf(Key.VolumeUp).ShouldBeNull();

        var recorder = new ShortcutRecorder(null);
        recorder.Accept(Key.VolumeUp, KeyModifiers.Control).ShouldBeTrue();
        recorder.Chord.ShouldBeNull();
    }

    [AvaloniaFact]
    public void A_shortcut_that_would_steal_typing_blocks_saving()
    {
        var editor = new SnippetEditorWindow(null);
        editor.Show();
        editor.GetVisualDescendants().OfType<TextBox>().First().Text = "review prompt";
        editor.GetVisualDescendants().OfType<TextBox>().ElementAt(1).Text = "Body";

        editor.Shortcut.Chord = HotkeyChords.Parse("Shift+A");

        Save(editor).IsEnabled.ShouldBeFalse();
        Shows(editor, "Add Ctrl, Alt or the Windows key").ShouldBeTrue();

        editor.Shortcut.Chord = HotkeyChords.Parse("Ctrl+Alt+1");
        Save(editor).IsEnabled.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void The_editor_warns_about_a_combination_another_program_holds()
    {
        var store = Store();
        var hotkeys = new FakeGlobalHotkeys();
        hotkeys.Taken.Add(HotkeyChords.Parse("Ctrl+Alt+9"));
        using var binder = new SnippetHotkeyBinder(store, hotkeys, new RecordingTextInjector());

        var editor = new SnippetEditorWindow(null, binder);
        editor.Show();
        editor.Shortcut.Chord = HotkeyChords.Parse("Ctrl+Alt+9");

        Shows(editor, "Another app or Windows already uses this shortcut").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void The_editor_refuses_a_combination_another_prompt_has()
    {
        var store = Store();
        store.Add(new SnippetEntry
        {
            Trigger = "first prompt",
            Text = "First",
            Match = SnippetMatch.WholeUtterance,
            Hotkey = HotkeyChords.Parse("Ctrl+Alt+1"),
        });
        using var binder = new SnippetHotkeyBinder(store, new FakeGlobalHotkeys(), new RecordingTextInjector());

        var editor = new SnippetEditorWindow(null, binder);
        editor.Show();
        editor.GetVisualDescendants().OfType<TextBox>().First().Text = "second prompt";
        editor.GetVisualDescendants().OfType<TextBox>().ElementAt(1).Text = "Second";
        editor.Shortcut.Chord = HotkeyChords.Parse("Ctrl+Alt+1");

        Save(editor).IsEnabled.ShouldBeFalse();
        Shows(editor, "Another prompt already uses this shortcut").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void The_editor_offers_the_next_free_ctrl_alt_digit()
    {
        var store = Store();
        store.Add(new SnippetEntry
        {
            Trigger = "first prompt",
            Text = "First",
            Match = SnippetMatch.WholeUtterance,
            Hotkey = HotkeyChords.Parse("Ctrl+Alt+1"),
        });
        var hotkeys = new FakeGlobalHotkeys();
        using var binder = new SnippetHotkeyBinder(store, hotkeys, new RecordingTextInjector());

        var editor = new SnippetEditorWindow(null, binder);
        editor.Show();

        editor.SuggestedShortcut.ShouldBe(HotkeyChords.Parse("Ctrl+Alt+2"));
        Shows(editor, "Use Ctrl+Alt+2").ShouldBeTrue();

        editor.Suggestion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        editor.Shortcut.Chord.ShouldBe(HotkeyChords.Parse("Ctrl+Alt+2"));
        editor.SuggestedShortcut.ShouldBeNull("there is a shortcut now");
        editor.Suggestion.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Typing_in_the_editor_does_not_probe_the_shortcut_again()
    {
        var hotkeys = new FakeGlobalHotkeys();
        using var binder = new SnippetHotkeyBinder(Store(), hotkeys, new RecordingTextInjector());
        var editor = new SnippetEditorWindow(null, binder);
        editor.Show();
        editor.Shortcut.Chord = HotkeyChords.Parse("Ctrl+Alt+5");
        var probes = hotkeys.RegisterCalls;

        editor.GetVisualDescendants().OfType<TextBox>().First().Text = "review prompt";
        editor.GetVisualDescendants().OfType<TextBox>().ElementAt(1).Text = "Body";
        Dispatcher.UIThread.RunJobs();

        hotkeys.RegisterCalls.ShouldBe(probes);
        Save(editor).IsEnabled.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Recording_a_shortcut_suspends_the_registered_ones()
    {
        var store = Store();
        store.Add(new SnippetEntry
        {
            Trigger = "first prompt",
            Text = "First",
            Match = SnippetMatch.WholeUtterance,
            Hotkey = HotkeyChords.Parse("Ctrl+Alt+1"),
        });
        var hotkeys = new FakeGlobalHotkeys();
        using var binder = new SnippetHotkeyBinder(store, hotkeys, new RecordingTextInjector());

        var editor = new SnippetEditorWindow(store.Entries[0], binder);
        editor.Show();
        var field = editor.Shortcut.GetVisualDescendants().OfType<TextBox>().First();

        field.Focus();
        hotkeys.Registered.ShouldBeEmpty("Windows would swallow the combination before the recorder saw it");

        editor.GetVisualDescendants().OfType<TextBox>().First().Focus();
        hotkeys.Registered.Count.ShouldBe(1);
    }

    [AvaloniaFact]
    public void The_list_shows_the_kind_the_shortcut_and_any_conflict()
    {
        var store = Store();
        store.Add(new SnippetEntry
        {
            Trigger = "review prompt",
            Text = "Review this.",
            Match = SnippetMatch.WholeUtterance,
            Hotkey = HotkeyChords.Parse("Ctrl+Alt+1"),
        });
        store.Add(new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street" });
        var hotkeys = new FakeGlobalHotkeys();
        hotkeys.Taken.Add(HotkeyChords.Parse("Ctrl+Alt+1"));
        using var binder = new SnippetHotkeyBinder(store, hotkeys, new RecordingTextInjector());

        var view = new SnippetsView(store, binder);
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();

        Shows(view, "Say only").ShouldBeTrue();
        Shows(view, "Ctrl+Alt+1").ShouldBeTrue();
        Shows(view, "Another app or Windows already uses this shortcut").ShouldBeTrue();
        Shows(view, "1 prompt · 1 snippet").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void The_hub_section_is_called_prompts_and_snippets()
    {
        var hub = new HubWindow();
        hub.Show();
        hub.ShowSection(HubSection.Snippets);

        Shows(hub, "Prompts & snippets").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void A_preview_composition_can_carry_prompt_shortcuts()
    {
        var hotkeys = new FakeGlobalHotkeys();
        var store = Store();
        store.Add(new SnippetEntry
        {
            Trigger = "review prompt",
            Text = "Review this.",
            Match = SnippetMatch.WholeUtterance,
            Hotkey = HotkeyChords.Parse("Ctrl+Alt+1"),
        });

        var composition = Composition.ForPreview(
            new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider()),
            new DictionaryFile(Path.Combine(_directory, "dictionary.txt")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            store,
            hotkeys: hotkeys);

        composition.PromptHotkeys.ShouldNotBeNull();

        // The prompt's only: the paste-last shortcut is unbound until the user records one.
        hotkeys.Registered.Select(r => r.Value).ShouldBe([HotkeyChords.Parse("Ctrl+Alt+1")]);
    }

    private static SoftButton Save(Window editor) =>
        editor.GetVisualDescendants().OfType<SoftButton>().First(b => b.Content as string == "Save");
}
