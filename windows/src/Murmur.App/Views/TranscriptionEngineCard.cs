using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Cloud;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// Settings' transcription cards: which engine — offline Parakeet, Azure with the user's own key,
/// or Azure with Parakeet as the fallback — and everything Azure needs to be set up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parakeet is the default and the card says what leaves the machine.</b> The note under the
/// engine switch changes with the mode, so nobody switches to Azure without reading that their
/// audio will be sent to their own resource.
/// </para>
/// <para>
/// <b>The key is entered once.</b> Once saved it is never shown again — only "•••• abcd saved",
/// the last four characters, enough to tell Key 1 from Key 2. Replace and Remove are the only
/// things that can be done to it. It is never placed in the settings record, so it can never
/// reach <c>settings.json</c>.
/// </para>
/// <para>
/// Two Windows 11 expander cards: the engine, chosen with a segmented control, and the Azure
/// resource, one row per thing to fill in.
/// </para>
/// </remarks>
public sealed class TranscriptionEngineCard : UserControl
{
    private static readonly (EngineMode Mode, string Note)[] Notes =
    [
        (EngineMode.Parakeet, "Parakeet runs on this PC. Nothing you say leaves the machine."),
        (EngineMode.Azure,
            "Your audio is sent to your own Azure OpenAI resource with your key, and nowhere else. "
            + "Parakeet is not loaded."),
        (EngineMode.AzureWithParakeetFallback,
            "Your audio is sent to your own Azure OpenAI resource. When Azure cannot be reached or "
            + "is too slow, Parakeet on this PC takes over. A rejected key is reported, not covered up."),
    ];

    private static readonly CloudProfile[] Profiles = [CloudProfile.Accurate, CloudProfile.Fast];

    private readonly AppSettings _settings;
    private readonly CloudServices? _cloud;

    private readonly FASettingsExpander _engineCard;
    private readonly TextBlock _modeNote = Panels.Note(string.Empty);
    private readonly TextBlock _endpointProblem = Panels.Note(string.Empty);
    private readonly TextBlock _profileNote = Panels.Note(string.Empty);
    private readonly TextBlock _keyStatus;
    private readonly StackPanel _keyEntry = new() { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug };
    private readonly StackPanel _keyActions = new() { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug };
    private readonly TextBlock _keyError;
    private readonly TextBlock _testResult = Panels.Note(string.Empty);
    private readonly SoftButton _testOne = new() { Content = "Test connection", Icon = Icons.PlugConnected };
    private readonly SoftButton _testBoth = Panels.OutlineButton("Test both");

    private bool _replacing;

    /// <summary>Builds the cards.</summary>
    /// <param name="settings">Where the mode, endpoint and deployments are kept.</param>
    /// <param name="cloud">The key and the connection test, or null where there are none (a bare preview).</param>
    public TranscriptionEngineCard(AppSettings settings, CloudServices? cloud = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _cloud = cloud;

        _keyStatus = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Body,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
        };

        _keyError = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Label,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            [!TextBlock.ForegroundProperty] = Tokens.Live.Notice,
        };

        var data = settings.Data;

        EndpointField = Sized(Panels.Field("https://your-resource.openai.azure.com", data.AzureEndpoint ?? string.Empty), FieldWidth);
        ApiVersionField = Sized(Panels.Field(AzureTranscriptionOptions.DefaultApiVersion, data.AzureApiVersion), FieldWidth);
        AccurateField = Sized(Panels.Field(AzureProfiles.ModelFor(CloudProfile.Accurate), data.AzureDeploymentAccurate ?? string.Empty), FieldWidth);
        FastField = Sized(Panels.Field(AzureProfiles.ModelFor(CloudProfile.Fast), data.AzureDeploymentFast ?? string.Empty), FieldWidth);
        KeyField = Sized(Panels.Field("Paste Key 1 from Keys and Endpoint", string.Empty), FieldWidth - KeySaveWidth);
        KeyField.PasswordChar = '•';

        ModeSelector = new SegmentedControl([.. Choices.Engines.Select(e => e.Label)]);
        ModeSelector.SelectionChanged += (_, index) => SelectMode(Choices.Engines[index].Value);

        ProfileSelector = new SegmentedControl([.. Profiles.Select(AzureProfiles.Label)])
        {
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ProfileSelector.SelectionChanged += (_, index) => SelectProfile(Profiles[index]);

        EndpointField.TextChanged += (_, _) => Save(_settings.Data with { AzureEndpoint = Blank(EndpointField.Text) });
        ApiVersionField.TextChanged += (_, _) => Save(_settings.Data with
        {
            AzureApiVersion = Blank(ApiVersionField.Text) ?? AzureTranscriptionOptions.DefaultApiVersion,
        });
        AccurateField.TextChanged += (_, _) => Save(_settings.Data with { AzureDeploymentAccurate = Blank(AccurateField.Text) });
        FastField.TextChanged += (_, _) => Save(_settings.Data with { AzureDeploymentFast = Blank(FastField.Text) });

        _testOne.Click += async (_, _) => await TestAsync(both: false);
        _testBoth.Click += async (_, _) => await TestAsync(both: true);

        _endpointProblem.MaxWidth = FieldWidth;
        _profileNote.MaxWidth = FieldWidth;
        _profileNote.TextAlignment = TextAlignment.Right;
        _keyStatus.MaxWidth = FieldWidth;
        _keyError.MaxWidth = FieldWidth;

        _engineCard = Panels.Expander(Icons.Laptop, "Transcription engine", null, null,
            Panels.Item(new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children = { ModeSelector, _modeNote },
            }));

        var azure = Panels.Expander(Icons.Cloud, "Azure OpenAI",
            "Your own resource and key. Used only while an Azure engine is chosen.",
            null,
            Panels.Item("Endpoint", "Keys and Endpoint, in the Azure portal.", Stacked(EndpointField, _endpointProblem)),
            Panels.Item("API version", null, ApiVersionField),
            Panels.Item("Accurate deployment", AzureProfiles.ModelFor(CloudProfile.Accurate), AccurateField),
            Panels.Item("Fast deployment", AzureProfiles.ModelFor(CloudProfile.Fast), FastField),
            Panels.Item("Cloud model in use", null, Stacked(ProfileSelector, _profileNote)),
            Panels.Item("API key", null, Stacked(_keyStatus, _keyEntry, _keyActions, _keyError)),
            Panels.Item(new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = Tokens.Space.Snug,
                        Children = { _testOne, _testBoth },
                    },
                    _testResult,
                    Panels.Note("The key is encrypted for your Windows account with DPAPI and kept in its "
                              + "own file, never in settings.json. Speaker labelling (diarization) is not "
                              + "supported."),
                },
            }));

        Content = new StackPanel { Spacing = Tokens.Space.Tight, Children = { _engineCard, azure } };

        Refresh();
    }

    /// <summary>Width of the Azure fields, so the column of them lines up.</summary>
    private const double FieldWidth = 320;

    /// <summary>Room for the Save button beside the key field.</summary>
    private const double KeySaveWidth = 72;

    /// <summary>The Offline / Azure / fallback switch. Exposed for headless tests.</summary>
    internal SegmentedControl ModeSelector { get; }

    /// <summary>The Accurate / Fast switch. Exposed for headless tests.</summary>
    internal SegmentedControl ProfileSelector { get; }

    private static TextBox Sized(TextBox field, double width)
    {
        field.Width = width;
        return field;
    }

    private static StackPanel Stacked(params Control[] children)
    {
        var stack = new StackPanel
        {
            Spacing = Tokens.Space.Tight + Tokens.Space.Hair,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        foreach (var child in children)
        {
            child.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(child);
        }

        return stack;
    }

    /// <summary>The endpoint field. Exposed for headless tests.</summary>
    internal TextBox EndpointField { get; }

    /// <summary>The api-version field. Exposed for headless tests.</summary>
    internal TextBox ApiVersionField { get; }

    /// <summary>The Accurate deployment field. Exposed for headless tests.</summary>
    internal TextBox AccurateField { get; }

    /// <summary>The Fast deployment field. Exposed for headless tests.</summary>
    internal TextBox FastField { get; }

    /// <summary>Where a new key is pasted. Exposed for headless tests.</summary>
    internal TextBox KeyField { get; }

    /// <summary>The key's status line. Exposed for headless tests.</summary>
    internal string KeyStatus => _keyStatus.Text ?? string.Empty;

    /// <summary>The last connection test's result. Exposed for headless tests.</summary>
    internal string TestResult => _testResult.Text ?? string.Empty;

    /// <summary>The key error on show, or empty when there is none.</summary>
    internal string KeyError => _keyError.IsVisible ? _keyError.Text ?? string.Empty : string.Empty;

    /// <summary>Saves what is in <see cref="KeyField"/> as the key.</summary>
    /// <returns>Whether it was saved.</returns>
    internal bool SaveKey()
    {
        if (_cloud is null) return false;

        try
        {
            _cloud.Key.Save(KeyField.Text ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            ShowKeyError(ex.Message.Split(" (Parameter", 2)[0]);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            CrashLog.Write("Saving the API key failed", ex);
            ShowKeyError("The key could not be saved on this PC.");
            return false;
        }

        // The field is emptied at once: the key is now only in the encrypted file and in memory.
        KeyField.Text = string.Empty;
        _replacing = false;
        Refresh();
        return true;
    }

    /// <summary>Forgets the key.</summary>
    internal void RemoveKey()
    {
        if (_cloud is null) return;

        try
        {
            _cloud.Key.Remove();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Removing the API key failed", ex);

            // After the refresh, which hides the error line: the key is still saved, and the
            // user needs to see why.
            _replacing = false;
            Refresh();
            ShowKeyError("The key file could not be removed.");
            return;
        }

        _replacing = false;
        Refresh();
    }

    /// <summary>Runs the connection test on the profile in use, or on every configured one.</summary>
    /// <param name="both">Whether to test each filled-in deployment.</param>
    /// <returns>When the result is shown.</returns>
    internal async Task TestAsync(bool both)
    {
        if (_cloud is null)
        {
            _testResult.Text = "Testing is not available here.";
            return;
        }

        var profiles = both ? _cloud.ConfiguredProfiles() : [AzureProfiles.EffectiveProfile(_settings.Data)];
        if (profiles.Count == 0) profiles = [AzureProfiles.EffectiveProfile(_settings.Data)];

        _testOne.IsEnabled = false;
        _testBoth.IsEnabled = false;
        _testResult.Text = "Testing…";

        try
        {
            var lines = new List<string>();
            foreach (var profile in profiles)
            {
                var result = await _cloud.TestAsync(profile);
                var mark = result.Succeeded ? "✓" : "✕";
                lines.Add($"{mark} {AzureProfiles.Label(profile)}: {result.Message}");
            }

            _testResult.Text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            // An async void click handler sits underneath this; nothing may escape it.
            CrashLog.Write("Connection test failed", ex);
            _testResult.Text = "The test could not be run.";
        }
        finally
        {
            _testOne.IsEnabled = true;
            _testBoth.IsEnabled = true;
        }
    }

    private void OnKeyChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_cloud is not null) _cloud.Key.Changed -= OnKeyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_cloud is not null)
        {
            _cloud.Key.Changed -= OnKeyChanged;
            _cloud.Key.Changed += OnKeyChanged;
        }
    }

    private void SelectMode(EngineMode mode)
    {
        if (_settings.Data.Engine != mode) Save(_settings.Data with { Engine = mode });
        Refresh();
    }

    private void SelectProfile(CloudProfile profile)
    {
        AzureProfiles.Select(_settings, profile);
        Refresh();
    }

    private void Save(SettingsData data)
    {
        if (data == _settings.Data) return;

        _settings.Update(data);
        RefreshEndpointAndProfile();
    }

    /// <summary>Brings every part of the card into line with the settings and the key.</summary>
    private void Refresh()
    {
        var mode = _settings.Data.Engine;
        var index = Choices.Engines.ToList().FindIndex(e => e.Value == mode);
        ModeSelector.Select(Math.Max(0, index));

        _modeNote.Text = Notes.FirstOrDefault(m => m.Mode == mode).Note ?? Notes[0].Note;
        _engineCard.Description = EngineStatus.UsesCloud(mode)
            ? "Azure OpenAI, with your own key."
            : "Parakeet, on this PC. The default.";
        _engineCard.IconSource = Panels.IconSource(EngineStatus.UsesCloud(mode) ? Icons.Cloud : Icons.Laptop);

        RefreshEndpointAndProfile();
        RefreshKey();
    }

    private void RefreshEndpointAndProfile()
    {
        var data = _settings.Data;

        if (string.IsNullOrWhiteSpace(data.AzureEndpoint))
        {
            _endpointProblem.Text = string.Empty;
        }
        else if (AzureEndpoint.TryNormalize(data.AzureEndpoint, out var endpoint, out var problem))
        {
            var shown = endpoint.AbsoluteUri.TrimEnd('/');
            _endpointProblem.Text = problem ?? (string.Equals(shown, data.AzureEndpoint.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $"Will use {shown}");
        }
        else
        {
            _endpointProblem.Text = problem ?? string.Empty;
        }

        _endpointProblem.IsVisible = !string.IsNullOrEmpty(_endpointProblem.Text);

        var active = AzureProfiles.EffectiveProfile(data);
        ProfileSelector.Select(Array.IndexOf(Profiles, active));

        _profileNote.Text = active != data.AzureActiveProfile
            ? $"Only the {AzureProfiles.Label(active)} deployment is filled in, so it is the one used."
            : "Also switchable from the tray and the bar's menu. Applies to the next dictation.";
    }

    private void RefreshKey()
    {
        _keyEntry.Children.Clear();
        _keyActions.Children.Clear();
        _keyError.IsVisible = false;

        if (_cloud is null)
        {
            _keyStatus.Text = "Keys cannot be stored here.";
            KeyField.IsEnabled = false;
            _testOne.IsEnabled = false;
            _testBoth.IsEnabled = false;
            return;
        }

        var key = _cloud.Key;
        var state = key.State;

        _keyStatus.Text = state switch
        {
            CloudKeyState.Saved => $"{key.Masked} saved",
            CloudKeyState.Rejected => $"{key.Masked} — Azure rejected this key. Replace it.",
            CloudKeyState.Unreadable => "The saved key cannot be read on this PC. Enter it again.",
            _ => "No key saved.",
        };
        _keyStatus.Bind(TextBlock.ForegroundProperty, state is CloudKeyState.Rejected or CloudKeyState.Unreadable
            ? Tokens.Live.Notice
            : Tokens.Live.Ink);

        var hasKey = state is CloudKeyState.Saved or CloudKeyState.Rejected;
        if (!hasKey || _replacing)
        {
            var save = Panels.PrimaryButton("Save");
            save.Click += (_, _) => SaveKey();
            _keyEntry.Children.Add(KeyField);
            _keyEntry.Children.Add(save);

            if (_replacing)
            {
                var cancel = Panels.QuietButton("Cancel");
                cancel.Click += (_, _) =>
                {
                    KeyField.Text = string.Empty;
                    _replacing = false;
                    Refresh();
                };
                _keyEntry.Children.Add(cancel);
            }
        }

        if (hasKey && !_replacing)
        {
            var replace = Panels.OutlineButton("Replace");
            replace.Click += (_, _) =>
            {
                _replacing = true;
                Refresh();
            };

            var remove = Panels.QuietButton("Remove");
            remove.Click += (_, _) => RemoveKey();

            _keyActions.Children.Add(replace);
            _keyActions.Children.Add(remove);
        }

        _keyEntry.IsVisible = _keyEntry.Children.Count > 0;
        _keyActions.IsVisible = _keyActions.Children.Count > 0;
    }

    private void ShowKeyError(string message)
    {
        _keyError.Text = message;
        _keyError.IsVisible = true;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
