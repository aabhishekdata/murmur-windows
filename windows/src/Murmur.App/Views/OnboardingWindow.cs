using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Cloud;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>The pages of first-run setup, in the order they can appear.</summary>
public enum OnboardingStep
{
    /// <summary>What Murmur does, in one sentence.</summary>
    Welcome,

    /// <summary>On this PC, or Azure.</summary>
    Engine,

    /// <summary>Endpoint, deployments, key, and a connection test.</summary>
    Azure,

    /// <summary>Whether the Parakeet model is on disk, and how to get it.</summary>
    Offline,

    /// <summary>The push-to-talk key, and a live "hold it and say hello".</summary>
    Hotkey,

    /// <summary>A summary, and where to find things.</summary>
    Done,
}

/// <summary>
/// First-run setup: welcome, engine, its setup, the key, done.
/// </summary>
/// <remarks>
/// <para>
/// Shown once, to new installs only — <see cref="Onboarding.IsNeeded"/> decides, from the settings
/// version, and anyone with a settings file from before onboarding existed never sees it.
/// Closing it early is allowed and does not count as finishing; it simply comes back next launch
/// until it is finished or skipped.
/// </para>
/// <para>
/// Every choice is written to settings as it is made, through the same records Settings uses, so
/// there is no "apply at the end" step to lose and nothing here that Settings cannot undo. The
/// Azure fields are the exception that is typed rather than clicked: they are written when the
/// page is left by any route — Next, Back, Skip, or closing the window.
/// </para>
/// <para>
/// The hotkey page is the one that matters: it asks the user to hold the key and say hello into a
/// box on the page, which exercises everything at once — the hook, the microphone, the engine and
/// typing into a focused field. "It works" there is a real result, not a promise: it comes from the
/// engine's own completed dictation, never from what happens to be typed into the box.
/// </para>
/// </remarks>
public sealed class OnboardingWindow : Window
{
    private readonly Composition? _composition;
    private readonly AppSettings _settings;
    private readonly ContentControl _page = new();
    private readonly TextBlock _progress;
    private readonly SoftButton _back;
    private readonly SoftButton _next;
    private readonly SoftButton _skip;

    private readonly Dictionary<OnboardingStep, Control> _pages = [];

    // Engine page
    private ChoiceCard? _offlineChoice;
    private ChoiceCard? _azureChoice;
    private ToggleSwitch? _fallback;
    private Control? _fallbackRow;

    // Azure page
    private TextBox? _endpoint;
    private TextBox? _accurate;
    private TextBox? _fast;
    private TextBox? _key;
    private TextBlock? _azureResult;

    // Offline page
    private TextBlock? _modelStatus;
    private StatusDot? _modelDot;
    private StackPanel? _modelHowTo;

    // Hotkey page
    private TextBlock? _checkStatus;
    private StatusDot? _checkDot;
    private TextBox? _tryBox;
    private Border? _keyWarning;

    /// <summary>Builds the window over <paramref name="composition"/>.</summary>
    /// <param name="composition">The object graph.</param>
    /// <param name="settings">
    /// Settings to write to when there is no composition — previews only. Ignored otherwise.
    /// </param>
    public OnboardingWindow(Composition? composition, AppSettings? settings = null)
    {
        _composition = composition;
        _settings = composition?.Settings ?? settings
            ?? throw new ArgumentNullException(nameof(settings), "Onboarding needs settings to write to.");

        Title = "Welcome to Murmur";
        Width = 680;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowChrome.Install(this);

        _progress = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0),
            [!TextBlock.ForegroundProperty] = Tokens.Live.InkMuted,
        };

        _back = Panels.OutlineButton("Back");
        _back.Click += (_, _) => GoBack();

        _next = Panels.PrimaryButton("Next");
        _next.MinWidth = 120;
        _next.Click += (_, _) => GoNext();

        _skip = Panels.QuietButton("Skip setup");
        _skip.Click += (_, _) => Finish();

        var footer = new Border
        {
            // The quiet "Skip setup" carries its own padding; this lines its text up with the
            // page's left edge.
            Padding = new Thickness(Tokens.Space.Page - Tokens.Space.Snug, Tokens.Space.Roomy, Tokens.Space.Page, Tokens.Space.Roomy),
            BorderThickness = new Thickness(0, Tokens.Border.Hairline, 0, 0),
            [!Border.BorderBrushProperty] = Tokens.Live.Line,
            [!Border.BackgroundProperty] = Tokens.Live.Layer,
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    Panels.Docked(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = Tokens.Space.Snug,
                        Children = { _back, _next },
                    }, Dock.Right),
                    Panels.Docked(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = Tokens.Space.Base,
                        Children = { _progress, _skip },
                    }, Dock.Left),
                },
            },
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(WindowChrome.TitleBar("Murmur setup"), Dock.Top),
                Panels.Docked(footer, Dock.Bottom),
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new Border
                    {
                        Padding = new Thickness(Tokens.Space.Page, Tokens.Space.Wide, Tokens.Space.Page, Tokens.Space.Wide),
                        Child = _page,
                    },
                },
            },
        };

        GoTo(OnboardingStep.Welcome);
    }

    /// <summary>The page on show. Exposed for headless tests.</summary>
    public OnboardingStep Step { get; private set; }

    /// <summary>Raised when setup is finished or skipped. Not raised when the window is closed early.</summary>
    public event EventHandler? Finished;

    /// <summary>The steps this user will see, in order, given the engine chosen so far.</summary>
    public IReadOnlyList<OnboardingStep> Route
    {
        get
        {
            var setup = EngineStatus.UsesCloud(_settings.Data.Engine) ? OnboardingStep.Azure : OnboardingStep.Offline;
            return [OnboardingStep.Welcome, OnboardingStep.Engine, setup, OnboardingStep.Hotkey, OnboardingStep.Done];
        }
    }

    /// <summary>Shows <paramref name="step"/>.</summary>
    /// <param name="step">The page.</param>
    public void GoTo(OnboardingStep step)
    {
        Step = step;

        if (!_pages.TryGetValue(step, out var page))
        {
            page = Build(step);
            _pages[step] = page;
        }

        if (step == OnboardingStep.Offline) RefreshModel();
        if (step == OnboardingStep.Done) _pages[step] = page = BuildDone();

        _page.Content = page;

        var route = Route;
        var index = PositionOf(step, route);
        _progress.Text = step is OnboardingStep.Welcome or OnboardingStep.Done
            ? string.Empty
            : $"Step {index} of {route.Count - 2}";
        _progress.IsVisible = !string.IsNullOrEmpty(_progress.Text);

        _back.IsVisible = step is not (OnboardingStep.Welcome or OnboardingStep.Done);
        _skip.IsVisible = step != OnboardingStep.Done;
        _next.Content = step switch
        {
            OnboardingStep.Welcome => "Get started",
            OnboardingStep.Done => "Finish",
            _ => "Next",
        };
    }

    /// <summary>Moves on one page. Stays on the Azure page if what was typed there was refused.</summary>
    internal void GoNext()
    {
        // A refused key leaves its reason on the page; moving on would hide it and leave the user
        // believing Azure is set up.
        if (Step == OnboardingStep.Azure && !SaveAzureFields()) return;
        if (Step == OnboardingStep.Done)
        {
            Finish();
            return;
        }

        var route = Route;
        GoTo(route[Math.Min(PositionOf(Step, route) + 1, route.Count - 1)]);
    }

    /// <summary>Moves back one page.</summary>
    internal void GoBack()
    {
        if (Step == OnboardingStep.Azure) SaveAzureFields();

        var route = Route;
        GoTo(route[Math.Max(PositionOf(Step, route) - 1, 0)]);
    }

    /// <summary>Where <paramref name="step"/> sits in <paramref name="route"/>.</summary>
    /// <remarks>
    /// The route is derived from the engine in settings, which the tray and Settings can change
    /// while a setup page is showing. Azure and Offline are then both "the setup page": whichever
    /// is on show keeps the setup page's place rather than falling off the route.
    /// </remarks>
    private static int PositionOf(OnboardingStep step, IReadOnlyList<OnboardingStep> route) =>
        step is OnboardingStep.Azure or OnboardingStep.Offline
            ? SetupPosition
            : Math.Max(route.ToList().IndexOf(step), 0);

    /// <summary>The setup page's place in every route: after Welcome and Engine.</summary>
    private const int SetupPosition = 2;

    /// <summary>Marks setup as done and closes.</summary>
    public void Finish()
    {
        // Skipping from the Azure page keeps what was typed there, as Next would have.
        if (Step == OnboardingStep.Azure) SaveAzureFields();

        Onboarding.Complete(_settings);
        Finished?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private StackPanel Build(OnboardingStep step) => step switch
    {
        OnboardingStep.Engine => BuildEngine(),
        OnboardingStep.Azure => BuildAzure(),
        OnboardingStep.Offline => BuildOffline(),
        OnboardingStep.Hotkey => BuildHotkey(),
        OnboardingStep.Done => BuildDone(),
        _ => BuildWelcome(),
    };

    // ---- Pages ----

    private static StackPanel Heading(string icon, string title, string lead)
    {
        var lines = Panels.Body(lead);
        lines.Bind(TextBlock.ForegroundProperty, Tokens.Live.InkMuted);

        return new StackPanel
        {
            Spacing = Tokens.Space.Base,
            Margin = new Thickness(0, 0, 0, Tokens.Space.Wide),
            Children =
            {
                new Border
                {
                    Width = Tokens.Space.Page,
                    Height = Tokens.Space.Page,
                    CornerRadius = new CornerRadius(Tokens.Radius.Card),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, Tokens.Space.Tight),
                    [!Border.BackgroundProperty] = Tokens.Live.AccentSoft,
                    Child = new FluentIcon
                    {
                        Data = icon,
                        Width = Tokens.Space.Wide,
                        Height = Tokens.Space.Wide,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        [!FluentIcon.ForegroundProperty] = Tokens.Live.Accent,
                    },
                },
                Panels.Title(title),
                lines,
            },
        };
    }

    private static StackPanel Page(params Control[] children)
    {
        var page = new StackPanel { Spacing = Tokens.Space.Base };
        foreach (var child in children) page.Children.Add(child);
        return page;
    }

    private static StackPanel BuildWelcome() => Page(
        Heading(Icons.HandWave, "Welcome to Murmur",
            "Hold a key, say what you want to write, and let go. Murmur types it wherever your cursor is — "
            + "an email, a chat, a document, a prompt."),
        Bullet(Icons.Mic, "Push to talk", "Nothing is heard unless you are holding the key."),
        Bullet(Icons.CleanUp, "Cleaned up as it's typed", "Fillers out, corrections honoured, lists formatted — on this PC."),
        Bullet(Icons.Privacy, "Private by default", "The standard engine runs entirely on this PC. The cloud is your choice."),
        Panels.Note("Setup takes about two minutes. Everything here can be changed later in Settings."));

    private static DockPanel Bullet(string icon, string title, string detail) => new DockPanel
    {
        LastChildFill = true,
        Children =
        {
            Panels.Docked(new FluentIcon
            {
                Data = icon,
                Width = Tokens.Chrome.CardIcon,
                Height = Tokens.Chrome.CardIcon,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, Tokens.Space.Hair, Tokens.Space.Roomy, 0),
                [!FluentIcon.ForegroundProperty] = Tokens.Live.Accent,
            }, Dock.Left),
            new StackPanel
            {
                Spacing = Tokens.Space.Hair,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontFamily = Tokens.Fonts.Ui,
                        FontSize = Tokens.Fonts.Body,
                        FontWeight = FontWeight.SemiBold,
                        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                    },
                    Panels.Note(detail),
                },
            },
        },
    };

    private StackPanel BuildEngine()
    {
        _offlineChoice = new ChoiceCard(Icons.Laptop, "On this PC",
            "Parakeet. Private, free, and works offline. Needs a one-time 661 MB download and a reasonably quick PC.");
        _azureChoice = new ChoiceCard(Icons.Cloud, "Azure OpenAI",
            "gpt-4o-transcribe with your own Azure key. Light on any laptop. Your audio goes to your resource, nowhere else.");

        _fallback = Panels.Toggle(
            _settings.Data.Engine == EngineMode.AzureWithParakeetFallback,
            on =>
            {
                if (EngineStatus.UsesCloud(_settings.Data.Engine)) SetEngine(on ? EngineMode.AzureWithParakeetFallback : EngineMode.Azure);
            });

        _offlineChoice.Click += (_, _) => SetEngine(EngineMode.Parakeet);
        _azureChoice.Click += (_, _) => SetEngine(_fallback.IsChecked == true ? EngineMode.AzureWithParakeetFallback : EngineMode.Azure);

        _fallbackRow = Panels.Setting(Icons.Laptop, "Fall back to this PC",
            "When Azure can't be reached, Parakeet answers instead (needs the model too).", _fallback);

        RefreshEngine();

        return Page(
            Heading(Icons.Mic, "Where should your voice be transcribed?",
                "Pick one now; you can switch any time from Settings or the tray."),
            _offlineChoice,
            _azureChoice,
            _fallbackRow);
    }

    private void SetEngine(EngineMode mode)
    {
        if (_settings.Data.Engine != mode) _settings.Update(_settings.Data with { Engine = mode });
        RefreshEngine();
    }

    private void RefreshEngine()
    {
        var cloud = EngineStatus.UsesCloud(_settings.Data.Engine);
        if (_offlineChoice is not null) _offlineChoice.IsSelected = !cloud;
        if (_azureChoice is not null) _azureChoice.IsSelected = cloud;
        if (_fallbackRow is not null) _fallbackRow.IsEnabled = cloud;
    }

    private StackPanel BuildAzure()
    {
        var data = _settings.Data;
        _endpoint = Panels.Field("https://your-resource.openai.azure.com", data.AzureEndpoint ?? string.Empty);
        _accurate = Panels.Field(AzureProfiles.ModelFor(CloudProfile.Accurate), data.AzureDeploymentAccurate ?? string.Empty);
        _fast = Panels.Field(AzureProfiles.ModelFor(CloudProfile.Fast), data.AzureDeploymentFast ?? string.Empty);
        _key = Panels.Field(
            _composition?.Cloud.Key.HasKey == true ? $"{_composition.Cloud.Key.Masked} saved — paste to replace" : "Key 1, from Keys and Endpoint",
            string.Empty);
        _key.PasswordChar = '•';
        _azureResult = Panels.Note(string.Empty);

        var test = new SoftButton { Content = "Test connection", Icon = Icons.PlugConnected };
        test.Click += async (_, _) => await TestAzureAsync();

        var deployments = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Children =
            {
                Panels.Labelled("Accurate deployment", _accurate),
                Column(Panels.Labelled("Fast deployment (optional)", _fast), 1),
            },
        };
        _fast.Margin = new Thickness(0);
        ((Control)deployments.Children[1]).Margin = new Thickness(Tokens.Space.Base, 0, 0, 0);

        return Page(
            Heading(Icons.Cloud, "Connect your Azure OpenAI resource",
                "In the Azure portal, open your resource's Keys and Endpoint page, and the deployment names "
                + "from Azure AI Foundry."),
            Panels.Labelled("Endpoint", _endpoint),
            deployments,
            Panels.Labelled("API key", _key),
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Base,
                Margin = new Thickness(0, Tokens.Space.Tight, 0, 0),
                Children = { test },
            },
            _azureResult,
            Panels.Note("The key is encrypted for your Windows account and never written to settings.json."));
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    /// <summary>Writes the Azure fields to settings, and the key to its encrypted file.</summary>
    /// <returns>Whether the key, if one was typed, was accepted.</returns>
    internal bool SaveAzureFields()
    {
        if (_endpoint is null || _accurate is null || _fast is null || _key is null) return true;

        _settings.Update(_settings.Data with
        {
            AzureEndpoint = Blank(_endpoint.Text),
            AzureDeploymentAccurate = Blank(_accurate.Text),
            AzureDeploymentFast = Blank(_fast.Text),
        });

        if (string.IsNullOrWhiteSpace(_key.Text) || _composition is null) return true;

        try
        {
            _composition.Cloud.Key.Save(_key.Text);
            _key.Text = string.Empty;
            _key.PlaceholderText = $"{_composition.Cloud.Key.Masked} saved — paste to replace";
            return true;
        }
        catch (ArgumentException ex)
        {
            if (_azureResult is not null) _azureResult.Text = ex.Message.Split(" (Parameter", 2)[0];
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            CrashLog.Write("Saving the API key failed", ex);
            if (_azureResult is not null) _azureResult.Text = "The key could not be saved on this PC.";
            return false;
        }
    }

    private async Task TestAzureAsync()
    {
        if (_azureResult is null) return;
        if (!SaveAzureFields()) return;

        if (_composition is null)
        {
            _azureResult.Text = "Testing is not available here.";
            return;
        }

        if (!AzureEndpoint.TryNormalize(_settings.Data.AzureEndpoint ?? string.Empty, out _, out var problem))
        {
            _azureResult.Text = problem ?? "That endpoint doesn't look right.";
            return;
        }

        _azureResult.Text = "Testing…";
        try
        {
            var result = await _composition.Cloud.TestAsync(AzureProfiles.EffectiveProfile(_settings.Data));
            _azureResult.Text = (result.Succeeded ? "✓ " : "✕ ") + result.Message;
        }
        catch (Exception ex)
        {
            // An async void click handler sits underneath this; nothing may escape it.
            CrashLog.Write("Onboarding connection test failed", ex);
            _azureResult.Text = "The test could not be run.";
        }
    }

    private StackPanel BuildOffline()
    {
        _modelDot = new StatusDot { IsLit = true, VerticalAlignment = VerticalAlignment.Center };
        _modelStatus = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Body,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
        };

        var check = Panels.OutlineButton("Check again");
        check.Click += (_, _) => RefreshModel();

        var command = new TextBox
        {
            Text = Command,
            IsReadOnly = true,
            FontFamily = Tokens.Fonts.Mono,
            FontSize = Tokens.Fonts.Label,
            TextWrapping = TextWrapping.Wrap,
        };

        return Page(
            Heading(Icons.ArrowDownload, "Get the offline model",
                "Windows has no speech engine Murmur can use, so it brings its own: NVIDIA's Parakeet. "
                + "It is downloaded once (about 661 MB) and never leaves this PC."),
            Panels.Card(new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    Panels.Docked(check, Dock.Right),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = Tokens.Space.Snug,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { _modelDot, _modelStatus },
                    },
                },
            }),
            _modelHowTo = new StackPanel
            {
                Spacing = Tokens.Space.Base,
                Children =
                {
                    Panels.Note("Right-click get-model.ps1 (in the folder Murmur.exe is in) and choose Run with PowerShell — "
                              + "or paste this into PowerShell there:"),
                    command,
                    Panels.Note("It downloads to %LOCALAPPDATA%\\Murmur\\models\\parakeet-v2. You can carry on while it runs; "
                              + "Murmur picks the model up the moment it appears."),
                },
            });
    }

    /// <summary>The download command, as the packaging README gives it.</summary>
    internal const string Command = "powershell -ExecutionPolicy Bypass -File .\\get-model.ps1";

    private void RefreshModel()
    {
        if (_modelStatus is null || _modelDot is null) return;

        var found = _composition?.Models.Refresh() ?? false;
        _modelStatus.Text = found ? "Parakeet is installed and ready — nothing to download" : "Not installed yet";
        _modelDot.Token = found ? ColorToken.Accent : ColorToken.Notice;

        // How to get it is noise once it is there.
        if (_modelHowTo is not null) _modelHowTo.IsVisible = !found;
    }

    private StackPanel BuildHotkey()
    {
        var key = Panels.Choice(Choices.Keys, _settings.Data.PushToTalkKey, SelectKey);
        key.MinWidth = 180;

        _keyWarning = Panels.Notice(Choices.AltGrWarning);
        _keyWarning.IsVisible = Choices.WarningFor(_settings.Data.PushToTalkKey) is not null;

        _checkDot = new StatusDot { IsLit = false, Token = ColorToken.InkFaint, VerticalAlignment = VerticalAlignment.Center };
        _checkStatus = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
        };

        _tryBox = new TextBox
        {
            PlaceholderText = "Click here, then hold the key and say hello",
            MinHeight = 64,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
        };
        // Deliberately not watched: anything can be typed here by hand, and an injector types
        // a character at a time. The pass comes from the engine's completed dictation instead.

        ResetCheck();

        return Page(
            Heading(Icons.Keyboard, "Choose your push-to-talk key",
                "Hold it to talk, let go to type. Right Ctrl suits most keyboards: it types nothing on its own."),
            Panels.Setting(Icons.Keyboard, "Push-to-talk key", null, key),
            _keyWarning,
            Panels.Card(new StackPanel
            {
                Spacing = Tokens.Space.Base,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Try it",
                        FontFamily = Tokens.Fonts.Ui,
                        FontSize = Tokens.Fonts.Body,
                        FontWeight = FontWeight.SemiBold,
                        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                    },
                    _tryBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = Tokens.Space.Snug,
                        Children = { _checkDot, _checkStatus },
                    },
                },
            }));
    }

    private void SelectKey(int key)
    {
        if (_keyWarning is not null) _keyWarning.IsVisible = Choices.WarningFor(key) is not null;
        if (_settings.Data.PushToTalkKey != key) _settings.Update(_settings.Data with { PushToTalkKey = key });
        ResetCheck();
    }

    /// <summary>What the live check says now. Exposed for headless tests.</summary>
    internal string CheckStatus => _checkStatus?.Text ?? string.Empty;

    /// <summary>Whether the live check has heard something.</summary>
    internal bool CheckPassed { get; private set; }

    private void ResetCheck()
    {
        if (_checkStatus is null || _checkDot is null) return;

        CheckPassed = false;
        var key = Choices.KeyLabel(_settings.Data.PushToTalkKey);
        _checkStatus.Text = _composition?.Engine is null
            ? $"Hold {key} and say “hello” — the live check needs the running app."
            : $"Waiting — hold {key} and say “hello”.";
        _checkDot.Token = ColorToken.InkFaint;
        _checkDot.IsLit = false;
    }

    /// <summary>The engine moved: show that the key was seen, before any words arrive.</summary>
    internal void OnEngineState(DictationState state)
    {
        if (_checkStatus is null || _checkDot is null || CheckPassed) return;

        switch (state)
        {
            case DictationState.Recording:
                _checkStatus.Text = "Listening… say hello, then let go.";
                _checkDot.Token = ColorToken.Live;
                _checkDot.IsLit = true;
                break;
            case DictationState.Transcribing:
                _checkStatus.Text = "Transcribing…";
                _checkDot.Token = ColorToken.InkMuted;
                _checkDot.IsLit = true;
                break;
        }
    }

    /// <summary>A dictation came back: the whole chain works.</summary>
    /// <param name="text">What was heard.</param>
    internal void Heard(string text)
    {
        if (_checkStatus is null || _checkDot is null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            _checkStatus.Text = "Nothing was heard. Check the microphone in Settings, then try again.";
            _checkDot.Token = ColorToken.Notice;
            _checkDot.IsLit = true;
            return;
        }

        CheckPassed = true;
        _checkStatus.Text = text.Contains("hello", StringComparison.OrdinalIgnoreCase)
            ? "It works — Murmur heard you say hello."
            : $"It works — Murmur heard “{text.Trim()}”.";
        _checkDot.Token = ColorToken.Accent;
        _checkDot.IsLit = true;
    }

    private StackPanel BuildDone()
    {
        var data = _settings.Data;
        var key = Choices.KeyLabel(data.PushToTalkKey);

        return Page(
            Heading(Icons.CheckmarkCircleFilled, "You're all set",
                $"Hold {key} anywhere, speak, and let go."),
            Panels.Setting(EngineStatus.UsesCloud(data.Engine) ? Icons.Cloud : Icons.Laptop,
                "Engine", Choices.EngineLabel(data.Engine), null),
            Panels.Setting(Icons.Keyboard, "Push-to-talk key", key, null),
            Panels.Setting(Icons.Laptop, "The floating bar",
                "Bottom centre of your screen. It shows when Murmur is listening; right-click it for options.", null),
            Panels.Note("Murmur keeps running in the tray when its windows are closed. Settings are in the tray menu too."));
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_composition?.Engine is { } engine)
        {
            engine.StateChanged += OnEngineStateChanged;
            engine.Completed += OnEngineCompleted;
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Closing early does not finish setup, but it does not throw away a typed endpoint either.
        if (Step == OnboardingStep.Azure && !e.Cancel) SaveAzureFields();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (_composition?.Engine is { } engine)
        {
            engine.StateChanged -= OnEngineStateChanged;
            engine.Completed -= OnEngineCompleted;
        }

        base.OnClosed(e);
    }

    // Both raised on the engine's thread; the page is touched only on the UI thread.
    private void OnEngineStateChanged(object? sender, DictationState state) =>
        Dispatcher.UIThread.Post(() => { if (Step == OnboardingStep.Hotkey) OnEngineState(state); });

    private void OnEngineCompleted(object? sender, DictationResult result) =>
        Dispatcher.UIThread.Post(() => { if (Step == OnboardingStep.Hotkey) Heard(result.Text); });
}

/// <summary>
/// A large selectable option — an icon, a title and a sentence — for choices that need explaining.
/// </summary>
/// <remarks>
/// A Fluent button underneath, so it has Windows' hover, press and focus states and can be
/// reached with Tab and pressed with Space. Selected, it takes the accent edge and wash and a
/// check mark.
/// </remarks>
public sealed class ChoiceCard : Button
{
    /// <summary>Whether this option is the chosen one.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<ChoiceCard, bool>(nameof(IsSelected));

    private readonly FluentIcon _check;

    /// <summary>Builds a card.</summary>
    /// <param name="icon">From <see cref="Icons"/>.</param>
    /// <param name="title">The option.</param>
    /// <param name="detail">What choosing it means.</param>
    public ChoiceCard(string icon, string title, string detail)
    {
        Title = title;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(Tokens.Space.Roomy);
        CornerRadius = new CornerRadius(Tokens.Radius.Card);

        _check = new FluentIcon
        {
            Data = Icons.CheckmarkCircleFilled,
            Width = Tokens.Chrome.CardIcon,
            Height = Tokens.Chrome.CardIcon,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Tokens.Space.Base, 0, 0, 0),
            IsVisible = false,
            [!FluentIcon.ForegroundProperty] = Tokens.Live.Accent,
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(new FluentIcon
                {
                    Data = icon,
                    Width = Tokens.Space.Wide,
                    Height = Tokens.Space.Wide,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, Tokens.Space.Roomy, 0),
                    [!FluentIcon.ForegroundProperty] = Tokens.Live.Accent,
                }, Dock.Left),
                Panels.Docked(_check, Dock.Right),
                new StackPanel
                {
                    Spacing = Tokens.Space.Tight,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontFamily = Tokens.Fonts.Ui,
                            FontSize = Tokens.Fonts.Subtitle,
                            FontWeight = FontWeight.SemiBold,
                            [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                        },
                        Panels.Note(detail),
                    },
                },
            },
        };

        Restyle();
    }

    /// <summary>The option's name.</summary>
    public string Title { get; }

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSelectedProperty) Restyle();
    }

    private void Restyle()
    {
        _check.IsVisible = IsSelected;

        if (IsSelected)
        {
            Bind(BorderBrushProperty, Tokens.Live.Accent);
            Bind(BackgroundProperty, Tokens.Live.AccentSoft);
        }
        else
        {
            ClearValue(BorderBrushProperty);
            Bind(BackgroundProperty, Tokens.Live.Surface);
        }
    }
}
