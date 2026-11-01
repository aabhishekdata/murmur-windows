namespace Murmur.Core;

/// <summary>What language the user dictates in.</summary>
/// <remarks>
/// Persisted as a number, like every enum in <c>settings.json</c>. An unknown value from a newer
/// build reads as <see cref="Auto"/>.
/// </remarks>
public enum LanguageMode
{
    /// <summary>Let the cloud model detect it. Parakeet hears English whatever this says.</summary>
    Auto,

    /// <summary>English, told to the cloud model as <c>en</c>.</summary>
    English,

    /// <summary>Hindi, told to the cloud model as <c>hi</c>. Written in Devanagari.</summary>
    Hindi,

    /// <summary>
    /// Hindi and English mixed in one sentence, written in the Latin alphabet — the way most
    /// people type it in chat. No language code, plus a prompt asking for romanised Hindi.
    /// </summary>
    Hinglish,
}

/// <summary>Turns <see cref="LanguageMode"/> into what the cloud request carries.</summary>
/// <remarks>
/// <para>
/// <b>Only the cloud engine follows any of this.</b> Parakeet TDT 0.6B v2 is an English model, so
/// Hindi and Hinglish need an Azure engine; in fallback mode a dictation that falls back is heard
/// in English, and the bar says so ("English-only fallback") rather than letting a page of
/// mangled Hindi pass as a transcript.
/// </para>
/// <para>
/// Hinglish sends no language code on purpose: <c>hi</c> makes the model answer in Devanagari,
/// and <c>en</c> makes it translate. Leaving detection on and steering the script with the prompt
/// is what keeps both languages, each spelt the way the speaker would type it.
/// </para>
/// </remarks>
public static class LanguageModes
{
    /// <summary>The prompt sentence sent for <see cref="LanguageMode.Hinglish"/>.</summary>
    public const string HinglishHint =
        "The speaker mixes Hindi and English (Hinglish). Write Hindi words in romanised Hindi "
        + "using the Latin alphabet, not Devanagari, and keep English words in English.";

    /// <summary>The modes, as Settings offers them.</summary>
    public static IReadOnlyList<(LanguageMode Value, string Label)> Choices { get; } =
    [
        (LanguageMode.Auto, "Detect automatically"),
        (LanguageMode.English, "English"),
        (LanguageMode.Hindi, "Hindi"),
        (LanguageMode.Hinglish, "Hinglish (romanised)"),
    ];

    /// <summary>The ISO-639-1 code to send, or null to let the model detect it.</summary>
    /// <param name="mode">The chosen mode.</param>
    /// <param name="legacyCode">
    /// <see cref="SettingsData.CloudLanguage"/>, a code typed into <c>settings.json</c> by hand
    /// before this setting existed. Honoured in <see cref="LanguageMode.Auto"/> only.
    /// </param>
    /// <returns><c>en</c>, <c>hi</c>, the legacy code, or null.</returns>
    public static string? Code(LanguageMode mode, string? legacyCode = null) => mode switch
    {
        LanguageMode.English => "en",
        LanguageMode.Hindi => "hi",
        LanguageMode.Hinglish => null,
        _ => string.IsNullOrWhiteSpace(legacyCode) ? null : legacyCode.Trim(),
    };

    /// <summary>The sentence added to the prompt, or null for none.</summary>
    /// <param name="mode">The chosen mode.</param>
    /// <returns>The Hinglish hint, or null.</returns>
    public static string? Hint(LanguageMode mode) => mode == LanguageMode.Hinglish ? HinglishHint : null;

    /// <summary>Whether Parakeet cannot follow <paramref name="mode"/>.</summary>
    /// <param name="mode">The chosen mode.</param>
    /// <returns>True for Hindi and Hinglish.</returns>
    public static bool NeedsCloud(LanguageMode mode) => mode is LanguageMode.Hindi or LanguageMode.Hinglish;

    /// <summary>The warning Settings shows under the language choice, or null when there is none.</summary>
    /// <param name="mode">The chosen language.</param>
    /// <param name="engine">The chosen engine.</param>
    /// <returns>Why the choice will not (always) be honoured, or null.</returns>
    public static string? Warning(LanguageMode mode, EngineMode engine)
    {
        if (!NeedsCloud(mode)) return null;

        return engine switch
        {
            EngineMode.Azure => null,
            EngineMode.AzureWithParakeetFallback =>
                "Parakeet understands English only. When Azure can't be reached, the fallback types what it hears in English.",
            _ => "Parakeet understands English only. Hindi and Hinglish need an Azure engine — choose one above.",
        };
    }
}
