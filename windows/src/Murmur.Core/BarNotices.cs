using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>Where the words of the last dictation were heard, as the bar's privacy badge shows it.</summary>
public enum PrivacyBadge
{
    /// <summary>On this PC: Parakeet. Nothing left the machine.</summary>
    Local,

    /// <summary>In the user's own Azure resource.</summary>
    Cloud,

    /// <summary>Azure was chosen but could not be reached, so Parakeet on this PC answered.</summary>
    OfflineFallback,
}

/// <summary>
/// The short sentences the floating bar shows after something went other than planned, and the
/// badge it wears.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of the view so the wording can be tested and stays one sentence short enough for the
/// bar. None of them is an alarm: a fallback is the app working as designed, and is told in the
/// bar's neutral ink; only a dictation that was lost outright is marked with the notice colour.
/// Red stays reserved for recording.
/// </para>
/// </remarks>
public static class BarNotices
{
    /// <summary>The badge to show when nothing has happened yet, from the engine chosen.</summary>
    /// <param name="mode">The engine mode.</param>
    /// <returns>Cloud for either Azure mode, local otherwise.</returns>
    public static PrivacyBadge BadgeFor(EngineMode mode) =>
        EngineStatus.UsesCloud(mode) ? PrivacyBadge.Cloud : PrivacyBadge.Local;

    /// <summary>The badge after a dictation.</summary>
    /// <param name="engine">The engine that heard it.</param>
    /// <param name="fellBack">Whether that was the fallback.</param>
    /// <param name="mode">The engine mode, for a transcriber that did not say.</param>
    /// <returns>Offline fallback, cloud or local.</returns>
    public static PrivacyBadge BadgeFor(TranscriberKind engine, bool fellBack, EngineMode mode) => engine switch
    {
        _ when fellBack => PrivacyBadge.OfflineFallback,
        TranscriberKind.Azure => PrivacyBadge.Cloud,
        TranscriberKind.Parakeet => PrivacyBadge.Local,
        _ => BadgeFor(mode),
    };

    /// <summary>What the badge says when hovered, and what a screen reader reads.</summary>
    /// <param name="badge">The badge.</param>
    /// <param name="language">The dictation language, which makes a fallback English-only.</param>
    /// <returns>A few words.</returns>
    public static string Describe(PrivacyBadge badge, LanguageMode language = LanguageMode.Auto) => badge switch
    {
        PrivacyBadge.Cloud => "Cloud — your Azure resource",
        PrivacyBadge.OfflineFallback => LanguageModes.NeedsCloud(language) ? "English-only fallback" : "Offline fallback",
        _ => "Local — on this PC",
    };

    /// <summary>The notice after Parakeet answered for Azure.</summary>
    /// <param name="fallback">What happened, from the fallback transcriber.</param>
    /// <param name="language">The dictation language.</param>
    /// <returns>For example "Azure unreachable — used Parakeet".</returns>
    public static string ForFallback(TranscriptionFallback fallback, LanguageMode language = LanguageMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        // Parakeet is English only, so a Hindi or Hinglish dictation that fell back came out in
        // English. Said first, because it is the thing the user will otherwise be puzzled by.
        if (LanguageModes.NeedsCloud(language)) return "English-only fallback — Azure unreachable";

        var why = fallback.Cause switch
        {
            null => "Azure skipped",
            TranscriptionException { Failure: TranscriptionFailure.Timeout } => "Azure too slow",
            TranscriptionException { Failure: TranscriptionFailure.RateLimited } => "Azure busy",
            TranscriptionException { Failure: TranscriptionFailure.ServerError } => "Azure error",
            OperationCanceledException or TimeoutException => "Azure too slow",
            _ => "Azure unreachable",
        };

        return $"{why} — used Parakeet";
    }

    /// <summary>The notice after a dictation could not be transcribed at all.</summary>
    /// <param name="failure">What the engine reported.</param>
    /// <returns>For example "Key rejected — nothing typed".</returns>
    public static string ForFault(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var what = failure is TranscriptionException transcription
            ? transcription.Failure switch
            {
                TranscriptionFailure.Unauthorized => "Key rejected",

                // The key is fine; the resource's network rules turned this PC away.
                TranscriptionFailure.Forbidden => "Azure blocked this network",
                TranscriptionFailure.NotFound => "Deployment not found",
                TranscriptionFailure.NotConfigured => "Azure not set up",
                TranscriptionFailure.NoModel => "No speech model",
                TranscriptionFailure.Timeout => "Took too long",
                TranscriptionFailure.Network => "Azure unreachable",
                TranscriptionFailure.RateLimited => "Azure busy",
                TranscriptionFailure.PayloadTooLarge => "Recording too long",
                TranscriptionFailure.ProxyAuthenticationRequired => "Proxy needs sign-in",
                _ => "Transcription failed",
            }
            : "Transcription failed";

        return $"{what} — nothing typed";
    }
}
