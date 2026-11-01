namespace Murmur.Speech;

/// <summary>
/// Knows where the speech model is, and remembers the answer.
/// </summary>
/// <remarks>
/// <para>
/// The floating bar used to ask <see cref="ParakeetTranscriber.Locate"/> on every tick of a
/// 60 ms timer — up to eight <c>File.Exists</c> calls, sixteen times a second, forever, for an
/// answer that changes perhaps once in the life of an installation. This asks once, caches it,
/// and is told to look again (<see cref="Refresh"/>) when something that could change the
/// answer happens: the settings change, or the user opens a menu that shows it.
/// </para>
/// <para>
/// It also honours <c>SettingsData.ModelDirectory</c>, which the old static check ignored —
/// a model in a folder of the user's choosing loaded fine but the bar still said "No model".
/// A configured folder that is incomplete falls back to the default search rather than
/// winning, so a stale setting cannot hide a model that is installed.
/// </para>
/// </remarks>
public sealed class ModelLocator
{
    private readonly Func<string?> _configured;
    private readonly Func<IEnumerable<string>> _searchPaths;
    private readonly Func<string, bool> _isComplete;
    private readonly Lock _lock = new();

    private string? _directory;

    /// <summary>Creates a locator and runs the first search.</summary>
    /// <param name="configured">
    /// The user's chosen model folder, read on every <see cref="Refresh"/>; null for none.
    /// </param>
    /// <param name="searchPaths">Where else to look, in order. Defaults to the standard paths.</param>
    /// <param name="isComplete">
    /// Whether a folder holds a whole model. Defaults to checking for the required files;
    /// replaced in tests so they need no disk.
    /// </param>
    public ModelLocator(
        Func<string?> configured,
        Func<IEnumerable<string>>? searchPaths = null,
        Func<string, bool>? isComplete = null)
    {
        _configured = configured;
        _searchPaths = searchPaths ?? ParakeetTranscriber.DefaultSearchPaths;
        _isComplete = isComplete ?? ParakeetTranscriber.IsComplete;
        _directory = Search();
    }

    /// <summary>The folder holding a complete model, or null. Cached; never touches disk.</summary>
    public string? Directory
    {
        get
        {
            lock (_lock) return _directory;
        }
    }

    /// <summary>Whether a complete model was found at the last search.</summary>
    public bool IsInstalled => Directory is not null;

    /// <summary>
    /// Raised when a <see cref="Refresh"/> finds a different answer. May fire on any thread.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Searches again.</summary>
    /// <returns>Whether a model is installed now.</returns>
    public bool Refresh()
    {
        var found = Search();
        bool moved;

        lock (_lock)
        {
            moved = !string.Equals(found, _directory, StringComparison.OrdinalIgnoreCase);
            _directory = found;
        }

        if (moved) Changed?.Invoke(this, EventArgs.Empty);
        return found is not null;
    }

    private string? Search()
    {
        var configured = _configured();
        if (!string.IsNullOrWhiteSpace(configured) && _isComplete(configured)) return configured;

        return _searchPaths().FirstOrDefault(_isComplete);
    }
}
