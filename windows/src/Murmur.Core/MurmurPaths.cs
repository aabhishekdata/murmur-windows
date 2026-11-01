namespace Murmur.Core;

/// <summary>Where Murmur keeps its own files: settings, the key, history, snippets, the dictionary and logs.</summary>
/// <remarks>
/// <para>
/// <c>%LocalAppData%\Murmur</c>, unless the <see cref="OverrideVariable"/> environment variable
/// names another folder. The override exists so the published exe can be started and measured
/// with a settings file of its own — in Parakeet mode, then in Azure mode — without touching the
/// real user's settings or key on the same machine. Nothing in the app sets it.
/// </para>
/// <para>
/// The speech model is not covered. It is ~660 MB that every configuration shares, and it is
/// still looked for under <c>%LocalAppData%\Murmur\models</c> (see
/// <c>ParakeetTranscriber.DefaultSearchPaths</c>) whatever this says.
/// </para>
/// </remarks>
public static class MurmurPaths
{
    /// <summary>The environment variable that moves the data folder.</summary>
    public const string OverrideVariable = "MURMUR_DATA_DIR";

    /// <summary>The data folder for this process.</summary>
    /// <remarks>Read on every call, so a test that sets the variable sees it at once.</remarks>
    public static string DataDirectory => Resolve(
        Environment.GetEnvironmentVariable(OverrideVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>A file or folder inside <see cref="DataDirectory"/>.</summary>
    /// <param name="parts">The path below the data folder.</param>
    /// <returns>The combined path.</returns>
    public static string In(params string[] parts) => Path.Combine([DataDirectory, .. parts]);

    /// <summary>Works out the data folder from the override and the local application data folder.</summary>
    /// <param name="overrideDirectory">The variable's value, or null when it is not set.</param>
    /// <param name="localAppData">The user's local application data folder.</param>
    /// <returns>
    /// The override made absolute when it is set and not blank; otherwise <c>Murmur</c> under
    /// <paramref name="localAppData"/>.
    /// </returns>
    public static string Resolve(string? overrideDirectory, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(localAppData);

        return string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(localAppData, "Murmur")
            : Path.GetFullPath(overrideDirectory.Trim());
    }
}
