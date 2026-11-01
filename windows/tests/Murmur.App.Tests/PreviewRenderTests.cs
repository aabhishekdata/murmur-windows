using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
using Xunit;

namespace Murmur.AppTests;

/// <summary>
/// Renders the interface to PNG files so it can actually be looked at — every screen in the
/// light, dark and high-contrast palettes.
/// </summary>
/// <remarks>
/// <para>
/// The one thing a unit test cannot assert about a redesign is whether it looks right. Skia
/// rasterises the real visual tree headlessly, so every screen and every state can be captured
/// on the machine that wrote it — including states that are awkward to reach by hand, like the
/// missing-model notice or each step of first-run setup.
/// </para>
/// <para>
/// A headless window has no compositor, so it is never granted Mica: these show the solid
/// fallback a window gets when transparency is refused, which is also exactly what high contrast
/// draws. The <c>mica</c> captures stand a flat wallpaper-tinted colour behind a transparent
/// window to show the material case.
/// </para>
/// <para>
/// They are tests rather than a separate tool on purpose: a preview generator that is not part
/// of the suite stops being run, and then stops compiling. Each also asserts what it captured is
/// non-empty, so a screen that renders blank fails the build rather than producing a white
/// rectangle nobody notices. Output goes to <c>artifacts/ui-preview/</c> beside the solution.
/// </para>
/// </remarks>
public sealed class PreviewRenderTests
{
    // ---- The hub ----

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("hc")]
    public void The_hub_pages_are_captured(string theme) => InTheme(theme, () =>
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose(Seed(), dictionary: DictionarySeed(), snippets: SnippetSeed());

        foreach (var (section, name) in new[]
        {
            (HubSection.Home, "hub-1-home"),
            (HubSection.History, "hub-2-history"),
            (HubSection.Dictionary, "hub-3-dictionary"),
            (HubSection.Snippets, "hub-4-prompts"),
        })
        {
            var hub = new HubWindow(composition);
            hub.ShowSection(section);
            CaptureWindow(hub, $"{name}-{theme}");
            hub.Close();
        }
    });

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("hc")]
    public void The_empty_hub_is_captured(string theme) => InTheme(theme, () =>
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Home);
        CaptureWindow(hub, $"hub-5-home-empty-{theme}");
    });

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_hub_is_captured_over_a_material(string theme) => InTheme(theme, () =>
    {
        // Mica is the wallpaper, heavily blurred and tinted by the theme. A flat tint behind a
        // transparent window is a fair stand-in for reviewing how the layer and the pane sit on it.
        using var scratch = new Scratch();
        var composition = scratch.Compose(Seed(), dictionary: DictionarySeed());

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.Home);
        hub.Show();
        hub.Background = new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x20, 0x22, 0x2B) : Color.FromRgb(0xEC, 0xEE, 0xF5));

        Save(hub, $"hub-6-home-mica-{theme}");
    });

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("hc")]
    public void Every_settings_group_is_captured(string theme) => InTheme(theme, () =>
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([], microphones: new FakeAudioDeviceCatalog(
            new AudioDeviceInfo("{headset}", "Headset Microphone (Jabra Evolve2)", IsDefault: true),
            new AudioDeviceInfo("{array}", "Microphone Array (Realtek Audio)", IsDefault: false)));
        composition.Settings.Update(composition.Settings.Data with
        {
            AzureEndpoint = "https://contoso-speech.openai.azure.com",
            AzureDeploymentAccurate = "gpt-4o-transcribe",
        });

        var hub = new HubWindow(composition) { Height = 820 };
        hub.ShowSection(HubSection.Settings);
        hub.Show();
        Settle();

        var view = hub.GetVisualDescendants().OfType<SettingsView>().Single();
        var scroller = hub.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.GetVisualDescendants().Contains(view));

        for (var i = 0; i < view.Groups.Count; i++)
        {
            var group = view.Groups[i];
            var top = group.TranslatePoint(new Point(0, 0), view) ?? default;
            scroller.Offset = new Vector(0, Math.Max(0, top.Y - Tokens.Space.Base));

            Save(hub, $"settings-{i + 1}-{GroupNames[i]}-{theme}");
        }
    });

    private static readonly string[] GroupNames = ["general", "transcription", "cleanup", "prompts", "privacy", "about"];

    [AvaloniaFact]
    public void The_standalone_settings_window_is_captured()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);

        var window = new SettingsWindow(composition.Settings, composition.Microphones, composition.Cloud, composition.Models);
        CaptureWindow(window, "settings-7-window-light");
    }

    // ---- Onboarding ----

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("hc")]
    public void Every_onboarding_step_is_captured(string theme) => InTheme(theme, () =>
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);

        foreach (var step in Enum.GetValues<OnboardingStep>())
        {
            if (step == OnboardingStep.Azure)
            {
                composition.Settings.Update(composition.Settings.Data with { Engine = EngineMode.AzureWithParakeetFallback });
            }
            else if (step == OnboardingStep.Offline)
            {
                composition.Settings.Update(composition.Settings.Data with { Engine = EngineMode.Parakeet });
            }

            var window = new OnboardingWindow(composition);
            window.GoTo(step);

            if (step == OnboardingStep.Hotkey) window.Heard("Hello.");

            CaptureWindow(window, $"onboarding-{(int)step + 1}-{step.ToString().ToLowerInvariant()}-{theme}");
            window.Close();
        }
    });

    // ---- The floating bar ----

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("hc")]
    public void The_floating_bar_is_captured_in_every_state(string theme) => InTheme(theme, () =>
    {
        foreach (var (state, name) in new[]
        {
            (BarState.Idle, "bar-1-idle"),
            (BarState.Listening, "bar-2-recording"),
            (BarState.Processing, "bar-3-transcribing"),
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

            CaptureContent(OnDesktop(bar.Pill, bar.Width, bar.Height, theme), $"{name}-{theme}", 360, 140);
        }
    });

    [AvaloniaFact]
    public void The_side_docked_bar_is_captured()
    {
        var bar = new FlowBarWindow();
        bar.DockTo(ScreenEdge.Right);
        bar.ApplyState(BarState.Listening);

        bar.Wave.Level = 0.24;
        for (var i = 0; i < 40; i++) bar.Wave.Advance();

        CaptureContent(OnDesktop(bar.Pill, bar.Width, bar.Height, "light"), "bar-5-docked-right-light", 200, 300);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("hc")]
    public void The_bars_hands_free_and_notice_states_are_captured(string theme) => InTheme(theme, () =>
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);
        composition.Settings.Update(composition.Settings.Data with
        {
            Engine = EngineMode.AzureWithParakeetFallback,
            AzureEndpoint = "https://contoso-speech.openai.azure.com",
            AzureDeploymentAccurate = "gpt-4o-transcribe",
        });
        composition.Cloud.Key.Save("a1b2c3d4e5f60718293a4b5c6d7e8f90");

        var handsFree = new FlowBarWindow(composition);
        handsFree.ApplyState(BarState.HandsFree);
        handsFree.Wave.Level = 0.2;
        for (var i = 0; i < 40; i++) handsFree.Wave.Advance();
        CaptureContent(OnDesktop(handsFree.Pill, handsFree.Width, handsFree.Height, theme), $"bar-7-hands-free-{theme}", 460, 140);

        var fallback = new FlowBarWindow(composition);
        fallback.ShowOutcome(TranscriberKind.Parakeet, fellBack: true);
        fallback.ShowNotice(BarNotices.ForFallback(new TranscriptionFallback(
            TranscriberKind.Azure, TranscriberKind.Parakeet, new HttpRequestException("offline"))), alert: false);
        CaptureContent(OnDesktop(fallback.Pill, fallback.Width, fallback.Height, theme), $"bar-8-fallback-notice-{theme}", 460, 140);

        var fault = new FlowBarWindow(composition);
        fault.ShowNotice(BarNotices.ForFault(new TranscriptionException(
            TranscriptionFailure.Unauthorized, "401")), alert: true);
        CaptureContent(OnDesktop(fault.Pill, fault.Width, fault.Height, theme), $"bar-9-fault-notice-{theme}", 460, 140);

        var badge = new FlowBarWindow(composition);
        badge.ShowOutcome(TranscriberKind.Parakeet, fellBack: true);
        CaptureContent(OnDesktop(badge.Pill, badge.Width, badge.Height, theme), $"bar-10-idle-fallback-badge-{theme}", 360, 140);
    });

    [AvaloniaFact]
    public void History_with_its_row_actions_showing_is_captured()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose(Seed(), engines: kind => new ScriptedTranscriber(kind));
        foreach (var record in composition.Transcripts.Records.Take(2)) composition.Audio.Add(record.Id, new float[1600]);

        var hub = new HubWindow(composition);
        hub.ShowSection(HubSection.History);
        hub.Show();
        Settle();

        // Hover reveals a row's actions; the first row stands in for the one under the pointer.
        var paste = hub.GetVisualDescendants().OfType<SoftButton>().First(b => b.Label == "Paste");
        if (paste.Parent is Control actions) actions.Opacity = 1;

        Save(hub, "hub-7-history-actions-light");
    }

    [AvaloniaFact]
    public void The_bar_with_the_cloud_badge_is_captured()
    {
        using var scratch = new Scratch();
        var composition = scratch.Compose([]);
        composition.Settings.Update(composition.Settings.Data with { Engine = EngineMode.Azure });

        var bar = new FlowBarWindow(composition);
        bar.ApplyState(BarState.Idle);

        CaptureContent(OnDesktop(bar.Pill, bar.Width, bar.Height, "light"), "bar-6-idle-cloud-light", 360, 140);
    }

    // ---- Dialogs and text ----

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_editors_are_captured(string theme) => InTheme(theme, () =>
    {
        CaptureWindow(new SnippetEditorWindow(new SnippetEntry
        {
            Trigger = "my address",
            Text = "12 Bridge Street\nManchester M1 2AB\nUnited Kingdom",
        }), $"dialog-1-snippet-editor-{theme}");

        CaptureWindow(new DictionaryEditorWindow(
            Murmur.Dictionary.DictionaryEntry.Correction("cloud code", "Claude Code")), $"dialog-2-entry-editor-{theme}");

        CaptureWindow(new TeachCorrectionWindow("cloud code",
        [
            new() { At = DateTimeOffset.Now, Text = "I use cloud code every day" },
            new() { At = DateTimeOffset.Now, Text = "the cloud code team shipped it" },
        ]), $"dialog-3-teach-correction-{theme}");
    });

    [AvaloniaFact]
    public void The_text_transformations_are_captured_side_by_side()
    {
        // Five features rewrite text before it is typed. Rendering each one's before and after
        // is the only way to review them without a Windows machine.
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
                            Column(new FluentIcon
                            {
                                Data = Icons.ArrowRight,
                                Margin = new Thickness(Tokens.Space.Base, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                [!FluentIcon.ForegroundProperty] = Tokens.Live.InkFaint,
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
            Height = 900,
            [!Window.BackgroundProperty] = Tokens.Live.Canvas,
            Content = new ScrollViewer { Content = panel },
        };

        CaptureWindow(window, "text-1-transformations-light");
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    // ---- Plumbing ----

    /// <summary>Runs <paramref name="capture"/> with the application in <paramref name="theme"/>.</summary>
    private static void InTheme(string theme, Action capture)
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;

        try
        {
            app.RequestedThemeVariant = theme switch
            {
                "dark" => ThemeVariant.Dark,
                "hc" => ThemeResources.HighContrast,
                _ => ThemeVariant.Light,
            };

            capture();
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }

    /// <summary>Puts the bar's capsule on a plausible desktop — a flat document-white, editor-dark or black.</summary>
    /// <remarks>
    /// The bar's own window is transparent, so capturing it directly produces a capsule floating
    /// on nothing. Compositing it over a desktop is what makes the screenshot show what the
    /// user will actually see, shadow included.
    /// </remarks>
    private static Border OnDesktop(Control pill, double width, double height, string theme)
    {
        // The pill belongs to the bar's own window; take it out before re-parenting it.
        if (pill.Parent is Panel owner) owner.Children.Remove(pill);

        var desktop = theme switch
        {
            "dark" => Color.FromRgb(0x1E, 0x1F, 0x24),
            "hc" => Colors.Black,
            _ => Color.FromRgb(0xF4, 0xF4, 0xF2),
        };

        return new Border
        {
            Background = new SolidColorBrush(desktop),
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

    /// <summary>Captures a loose control inside a plain host window.</summary>
    private static void CaptureContent(Control content, string name, double width, double height)
    {
        var host = new Window
        {
            Width = width,
            Height = height,
            WindowDecorations = WindowDecorations.None,
            Content = content,
        };

        host.Show();
        Save(host, name);
        host.Close();
    }

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(Window window, string name)
    {
        // Let layout, posted refreshes and the ≤150 ms fades settle before the frame is taken;
        // a capture of a half-measured tree is worse than no capture at all.
        Settle();

        var frame = window.CaptureRenderedFrame();
        frame.ShouldNotBeNull($"{name} rendered nothing at all");

        frame!.PixelSize.Width.ShouldBeGreaterThan(0, $"{name} has no width");
        frame.PixelSize.Height.ShouldBeGreaterThan(0, $"{name} has no height");

        var file = Path.Combine(OutputDirectory, name + ".png");
        frame.Save(file, PngBitmapEncoderOptions.Default);

        // A screen that renders blank must fail the build rather than leave a white rectangle
        // nobody looks at twice.
        new FileInfo(file).Length.ShouldBeGreaterThan(1024, $"{name}.png looks empty");
    }

    /// <summary>Where the previews land: <c>artifacts/ui-preview</c> beside the solution.</summary>
    internal static string OutputDirectory
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

    private static string[] DictionarySeed() =>
    [
        "Anthropic",
        "Vercel",
        "Claude Code",
        "cloud code -> Claude Code",
        "whisper flow -> Wispr Flow",
        "# off: para keet -> Parakeet",
    ];

    private static SnippetEntry[] SnippetSeed() =>
    [
        new SnippetEntry
        {
            Trigger = "review prompt",
            Text = "Review this change as a senior engineer.\nFlag anything risky first.",
            Match = SnippetMatch.WholeUtterance,
            Hotkey = HotkeyChords.Parse("Ctrl+Alt+1"),
        },
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
            Trigger = "office hours",
            Text = "I'm around 9 to 5 UK time, Monday to Thursday.",
            IsEnabled = false,
        },
    ];

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
                Engine = TranscriberKind.Parakeet,
                Text = "The whole point of push to talk is that it never gets in the way. "
                     + "Hold the key, say the thing, let go, and it is already typed.",
            },
            new TranscriptRecord
            {
                At = now.AddHours(-27),
                AudioSeconds = 3.1,
                ProcessingSeconds = 0.22,
                Engine = TranscriberKind.Parakeet,
                Text = "Remind me to look at the microphone selection menu tomorrow.",
            },
            new TranscriptRecord
            {
                At = now.AddMinutes(-38),
                AudioSeconds = 9.8,
                ProcessingSeconds = 0.67,
                Engine = TranscriberKind.Azure,
                Profile = CloudProfile.Accurate,
                Text = "Let's ship the Windows build this week — the floating bar is the last "
                     + "piece and everything else is already on disk.",
                Corrections = [new Murmur.Dictionary.AppliedCorrection("cloud code", "Claude Code", 1)],
            },
            new TranscriptRecord
            {
                At = now.AddMinutes(-20),
                AudioSeconds = 6.4,
                ProcessingSeconds = 0.44,
                Engine = TranscriberKind.Parakeet,
                FellBack = true,
                Text = "I have been using cloud code all week and it is holding up well.",
            },
            new TranscriptRecord
            {
                At = now.AddMinutes(-4),
                AudioSeconds = 5.2,
                ProcessingSeconds = 0.41,
                Engine = TranscriberKind.Azure,
                Profile = CloudProfile.Fast,
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
            IAudioDeviceCatalog? microphones = null,
            Func<TranscriberKind, ITranscriber?>? engines = null)
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
                microphones,
                engines: engines);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
