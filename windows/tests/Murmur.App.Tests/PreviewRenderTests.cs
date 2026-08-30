using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.Abstractions;
using Murmur.App;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.App.Views;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;

namespace Murmur.AppTests;

/// <summary>
/// Renders the redesigned interface to PNG files so it can actually be looked at.
/// </summary>
/// <remarks>
/// <para>
/// This app is written on Linux and runs on Windows, and the one thing a unit test cannot
/// assert about a redesign is whether it looks right. Skia rasterises the real visual tree
/// headlessly, so every screen and every state can be captured on the machine that wrote it —
/// including states that are awkward to reach by hand, like the missing-model banner or a
/// history full of transcripts.
/// </para>
/// <para>
/// They are tests rather than a separate tool on purpose: a preview generator that is not part
/// of the suite stops being run, and then stops compiling. These also assert what they capture
/// is non-empty, so a screen that renders blank fails the build rather than producing a white
/// rectangle nobody notices.
/// </para>
/// <para>
/// Output goes to <c>artifacts/ui-preview/</c> beside the solution.
/// </para>
/// </remarks>
public sealed class PreviewRenderTests
{
    [AvaloniaFact]
    public void The_floating_bar_is_captured_in_every_state()
    {
        foreach (var (state, name) in new[]
        {
            (BarState.Idle, "bar-1-idle"),
            (BarState.Listening, "bar-2-listening"),
            (BarState.Processing, "bar-3-processing"),
            (BarState.Unavailable, "bar-4-no-model"),
        })
        {
            var bar = new FlowBarWindow();
            bar.ApplyState(state);

            if (state == BarState.Listening)
            {
                // Wind the animation forward so the capture shows the bars mid-motion rather
                // than flat at their starting height.
                bar.Wave.Level = 0.22;
                for (var i = 0; i < 40; i++) bar.Wave.Advance();
            }

            CaptureContent(OnBackdrop(bar.Pill, bar.Width, bar.Height), name, 520, 180);
        }
    }

    [AvaloniaFact]
    public void The_side_docked_bar_is_captured()
    {
        var bar = new FlowBarWindow();
        bar.DockTo(ScreenEdge.Right);
        bar.ApplyState(BarState.Listening);

        bar.Wave.Level = 0.24;
        for (var i = 0; i < 40; i++) bar.Wave.Advance();

        CaptureContent(OnBackdrop(bar.Pill, bar.Width, bar.Height), "bar-5-docked-right", 320, 380);
    }

    [AvaloniaFact]
    public void The_hub_is_captured_with_a_populated_history()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose(Seed());

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Home);

        CaptureWindow(hub, "hub-1-home");
    }

    [AvaloniaFact]
    public void The_empty_hub_is_captured()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Home);

        CaptureWindow(hub, "hub-2-home-empty");
    }

    [AvaloniaFact]
    public void The_dictionary_section_is_captured()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([], dictionary:
        [
            "Anthropic",
            "Vercel",
            "cloud code -> Claude Code",
            "whisper flow -> Wispr Flow",
            "# off: para keet -> Parakeet",
        ]);

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Dictionary);

        CaptureWindow(hub, "hub-3-dictionary");
    }

    [AvaloniaFact]
    public void The_snippets_section_is_captured()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([], snippets:
        [
            new SnippetEntry
            {
                Trigger = "my address",
                Text = "12 Bridge Street\nManchester M1 2AB\nUnited Kingdom",
            },
            new SnippetEntry
            {
                Trigger = "sign off",
                Text = "Thanks for reading — shout if anything here needs another pass.\n\nPat",
            },
            new SnippetEntry
            {
                Trigger = "stand up",
                Text = "Yesterday: shipped the floating bar. Today: snippets and the "
                     + "microphone picker. Blockers: none.",
            },
            new SnippetEntry
            {
                Trigger = "office hours",
                Text = "I'm around 9 to 5 UK time, Monday to Thursday.",
                IsEnabled = false,
            },
        ]);

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Snippets);

        CaptureWindow(hub, "hub-7-snippets");
    }

    [AvaloniaFact]
    public void The_snippet_editor_is_captured()
    {
        var editor = new SnippetEditorWindow(new SnippetEntry
        {
            Trigger = "my address",
            Text = "12 Bridge Street\nManchester M1 2AB\nUnited Kingdom",
        });

        CaptureWindow(editor, "hub-8-snippet-editor");
    }

    [AvaloniaFact]
    public void The_home_screen_with_a_learning_suggestion_is_captured()
    {
        // The honest half of "the dictionary learns by itself": a near miss of a term the user
        // already told Murmur about, offered rather than applied.
        using var scratch = new Scratch();

        var composition = scratch.Compose(
        [
            new TranscriptRecord
            {
                At = DateTimeOffset.Now.AddMinutes(-20),
                AudioSeconds = 6.4,
                ProcessingSeconds = 0.44,
                Text = "I have been using cloud code all week and it is holding up well.",
            },
            new TranscriptRecord
            {
                At = DateTimeOffset.Now.AddMinutes(-6),
                AudioSeconds = 4.1,
                ProcessingSeconds = 0.31,
                Text = "Ask the cloud code team about the Windows build.",
            },
        ],
        dictionary: ["Claude Code", "Anthropic", "Vercel"]);

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Home);

        CaptureWindow(hub, "hub-9-learning-suggestion");
    }

    [AvaloniaFact]
    public void The_teach_a_correction_dialog_is_captured()
    {
        var records = new List<TranscriptRecord>
        {
            new() { At = DateTimeOffset.Now, Text = "I use cloud code every day" },
            new() { At = DateTimeOffset.Now, Text = "the cloud code team shipped it" },
        };

        var teach = new TeachCorrectionWindow("cloud code", records);
        CaptureWindow(teach, "hub-10-teach-correction");
    }

    [AvaloniaFact]
    public void The_microphone_picker_is_captured()
    {
        using var scratch = new Scratch();

        var composition = scratch.Compose([], microphones: new FakeAudioDeviceCatalog(
            new AudioDeviceInfo("{headset}", "Headset Microphone (Jabra Evolve2)", IsDefault: true),
            new AudioDeviceInfo("{array}", "Microphone Array (Realtek Audio)", IsDefault: false),
            new AudioDeviceInfo("{webcam}", "Webcam Microphone (Logitech Brio)", IsDefault: false)));

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Settings);

        CaptureWindow(hub, "hub-11-microphone-picker");
    }

    [AvaloniaFact]
    public void The_speech_clean_up_switches_are_captured()
    {
        // Five passes now rewrite what the user said. The switches are the reason that is safe,
        // so they are worth looking at.
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);

        var view = new SettingsView(composition.Settings, composition.Microphones);
        var window = new Window
        {
            Width = 640,
            Height = 760,
            Background = Tokens.Brushes.Canvas,
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Scroll to the clean-up card rather than capturing the top of the page again.
        foreach (var scroller in view.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scroller.Offset = new Vector(0, 640);
        }

        CaptureWindow(window, "hub-12-speech-cleanup");
    }

    [AvaloniaFact]
    public void The_text_transformations_are_captured_side_by_side()
    {
        // Five of this round's features rewrite text before it is typed. Rendering each one's
        // before and after is the only way to review them without a Windows machine.
        var samples = new (string Label, string Spoken)[]
        {
            ("Filler words", "So, um, I think we should uh ship it."),
            ("Spoken correction", "Let's meet Tuesday, wait no, Friday."),
            ("Spoken list", "One, milk. Two, eggs. Three, bread."),
            ("Comma list", "Here's what I need: milk, eggs, bread"),
            ("Press Enter", "Tell Dave I am running late. Press enter."),
            ("Left alone", "The first time I met her, I only had one question."),
        };

        var panel = new StackPanel
        {
            Spacing = Tokens.Space.Base,
            Margin = new Thickness(Tokens.Space.Section),
            Children = { Panels.Title("What Murmur does to your words") },
        };

        panel.Children.Add(Panels.Note(
            "Spoken on the left, typed on the right. All pattern matching — no language model."));

        foreach (var (label, spoken) in samples)
        {
            var processed = TextPipeline.Run(spoken, new TextPipelineOptions());
            var typed = processed.Text.Length == 0 ? "(nothing)" : processed.Text;

            if (processed.Submit) typed += "\n⏎ then Enter";

            panel.Children.Add(Panels.Card(new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    new Caption { Text = label, IsHeader = true },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
                        Children =
                        {
                            Column(Panels.Note(spoken), 0),
                            Column(new TextBlock
                            {
                                Text = "→",
                                FontFamily = Tokens.Fonts.Ui,
                                FontSize = Tokens.Fonts.Body,
                                Foreground = Tokens.Brushes.InkFaint,
                                Margin = new Thickness(Tokens.Space.Base, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                            }, 1),
                            Column(Panels.Body(typed), 2),
                        },
                    },
                },
            }));
        }

        var window = new Window
        {
            Width = 760,
            Height = 880,
            Background = Tokens.Brushes.Canvas,
            Content = new ScrollViewer { Content = panel },
        };

        CaptureWindow(window, "text-2-transformations");
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    [AvaloniaFact]
    public void The_settings_section_is_captured()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Settings);

        CaptureWindow(hub, "hub-4-settings");
    }

    [AvaloniaFact]
    public void The_hub_is_captured_in_dark_mode()
    {
        // The hub follows the system appearance and the bar does not, so the dark palette is a
        // whole second set of token values that nothing else exercises.
        using var scratch = new Scratch();
        var composition = scratch.Compose(Seed());

        var previous = Application.Current!.RequestedThemeVariant;

        try
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

            var hub = new HubWindow(composition);
            hub.ShowSection(HubSection.Home);

            CaptureWindow(hub, "hub-6-home-dark");
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previous;
        }
    }

    [AvaloniaFact]
    public void The_dictionary_editor_is_captured()
    {
        var editor = new DictionaryEditorWindow(
            Murmur.Dictionary.DictionaryEntry.Correction("cloud code", "Claude Code"));

        CaptureWindow(editor, "hub-5-entry-editor");
    }

    [AvaloniaFact]
    public void The_list_formatting_before_and_after_is_captured()
    {
        // Not chrome — this is the text fix, shown as the user sees it. Rendering it beside the
        // raw transcript is the only way to review the feature without a Windows machine.
        const string spoken = "one apples two bananas three oranges";

        var panel = new StackPanel
        {
            Spacing = Tokens.Space.Roomy,
            Margin = new Thickness(Tokens.Space.Section),
            Children =
            {
                Panels.Title("Dictated lists"),
                Panels.Note("What the recogniser produces, and what Murmur types."),
                Panels.Card(new StackPanel
                {
                    Spacing = Tokens.Space.Snug,
                    Children =
                    {
                        new Caption { Text = "Transcribed", IsHeader = true },
                        Panels.Body(spoken),
                    },
                }),
                Panels.Card(new StackPanel
                {
                    Spacing = Tokens.Space.Snug,
                    Children =
                    {
                        new Caption { Text = "Typed", IsHeader = true },
                        Panels.Body(ListFormatter.Format(spoken)),
                    },
                }),
                Panels.Card(new StackPanel
                {
                    Spacing = Tokens.Space.Snug,
                    Children =
                    {
                        new Caption { Text = "Left alone", IsHeader = true },
                        Panels.Body(ListFormatter.Format("the first time I met her was in Paris")),
                    },
                }),
            },
        };

        var window = new Window
        {
            Width = 620,
            Height = 560,
            Background = Tokens.Brushes.Canvas,
            Content = panel,
        };

        CaptureWindow(window, "text-1-list-formatting");
    }

    // ---- Plumbing ----

    /// <summary>Puts a transparent-windowed pill on a plausible desktop backdrop.</summary>
    /// <remarks>
    /// The bar's own window is transparent, so capturing it directly produces a pill floating
    /// on a checkerboard of nothing. Compositing it over a neutral panel is what makes the
    /// screenshot show what the user will actually see.
    /// </remarks>
    private static Border OnBackdrop(Control pill, double width, double height)
    {
        // The pill belongs to the bar's own window; take it out before re-parenting it.
        if (pill.Parent is Panel owner) owner.Children.Remove(pill);

        return new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xEC, 0xEC, 0xE8), 0),
                    new GradientStop(Color.FromRgb(0xD8, 0xD8, 0xDE), 1),
                },
            },
            Padding = new Thickness(Tokens.Space.Section),
            Child = new Border
            {
                Width = width,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = pill,
            },
        };
    }

    /// <summary>Captures a window as it stands.</summary>
    private static void CaptureWindow(Window window, string name)
    {
        window.Show();
        Save(window, name);
    }

    /// <summary>
    /// Captures a loose control inside a plain host window.
    /// </summary>
    /// <remarks>
    /// The floating bar's own window is frameless and transparent, so capturing it directly
    /// gives a pill on a field of nothing. Hosting it lets the preview show what the user
    /// actually sees: the bar sitting on top of something else.
    /// </remarks>
    private static void CaptureContent(Control content, string name, double width, double height)
    {
        var host = new Window
        {
            Width = width,
            Height = height,
            SystemDecorations = SystemDecorations.None,
            Background = Tokens.Brushes.Canvas,
            Content = content,
        };

        host.Show();
        Save(host, name);
    }

    private static void Save(Window window, string name)
    {
        // Let layout and any posted refresh settle before the frame is taken; a capture of a
        // half-measured tree is worse than no capture at all.
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        frame.ShouldNotBeNull($"{name} rendered nothing at all");

        frame!.PixelSize.Width.ShouldBeGreaterThan(0, $"{name} has no width");
        frame.PixelSize.Height.ShouldBeGreaterThan(0, $"{name} has no height");

        var file = Path.Combine(OutputDirectory, name + ".png");
        frame.Save(file);

        // A screen that renders blank must fail the build rather than leave a white rectangle
        // nobody looks at twice.
        new FileInfo(file).Length.ShouldBeGreaterThan(1024, $"{name}.png looks empty");
    }

    /// <summary>Where the previews land: <c>artifacts/ui-preview</c> beside the solution.</summary>
    private static string OutputDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Murmur.sln")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName ?? AppContext.BaseDirectory;
            var output = Path.Combine(root, "artifacts", "ui-preview");

            Directory.CreateDirectory(output);
            return output;
        }
    }

    private static IReadOnlyList<TranscriptRecord> Seed()
    {
        var now = DateTimeOffset.Now;

        // Oldest first, which is the order the app itself appends them in — the store puts new
        // records at the head, so seeding newest-first would build the list upside down.
        return
        [
            new TranscriptRecord
            {
                At = now.AddDays(-4),
                AudioSeconds = 12.4,
                ProcessingSeconds = 0.88,
                Text = "The whole point of push to talk is that it never gets in the way. "
                     + "Hold the key, say the thing, let go, and it is already typed.",
            },
            new TranscriptRecord
            {
                At = now.AddHours(-27),
                AudioSeconds = 3.1,
                ProcessingSeconds = 0.22,
                Text = "Remind me to look at the microphone selection menu tomorrow.",
            },
            new TranscriptRecord
            {
                At = now.AddMinutes(-38),
                AudioSeconds = 9.8,
                ProcessingSeconds = 0.67,
                Text = "Let's ship the Windows build this week — the floating bar is the last "
                     + "piece and everything else is already on disk.",
                Corrections = [new Murmur.Dictionary.AppliedCorrection("cloud code", "Claude Code", 1)],
            },
            new TranscriptRecord
            {
                At = now.AddMinutes(-4),
                AudioSeconds = 5.2,
                ProcessingSeconds = 0.41,
                Text = "1. Apples\n2. Bananas\n3. Oranges",
            },
        ];
    }

    /// <summary>A throwaway %LocalAppData% for one preview, so nothing touches the real one.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "murmur-preview", Guid.NewGuid().ToString("N"));

        public Composition Compose(
            IReadOnlyList<TranscriptRecord> records,
            string[]? dictionary = null,
            SnippetEntry[]? snippets = null,
            IAudioDeviceCatalog? microphones = null)
        {
            Directory.CreateDirectory(_directory);

            var transcripts = new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl"));
            foreach (var record in records) transcripts.Add(record);

            var dictionaryPath = Path.Combine(_directory, "dictionary.txt");
            if (dictionary is { Length: > 0 })
            {
                File.WriteAllText(dictionaryPath, string.Join(Environment.NewLine, dictionary));
            }

            var snippetStore = new SnippetStore(Path.Combine(_directory, "snippets.jsonl"));
            foreach (var snippet in snippets ?? []) snippetStore.Add(snippet);

            return Composition.ForPreview(
                new AppSettings(Path.Combine(_directory, "settings.json")),
                new DictionaryFile(dictionaryPath),
                transcripts,
                snippetStore,
                microphones);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
