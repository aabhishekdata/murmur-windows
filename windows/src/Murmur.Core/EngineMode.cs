namespace Murmur.Core;

/// <summary>Which speech engine dictation uses.</summary>
/// <remarks>
/// Persisted as a number in <c>settings.json</c>, like every other enum there. An unknown
/// value — a settings file written by a newer build — is treated as <see cref="Parakeet"/>,
/// so the worst a downgrade can do is keep everything on the machine.
/// </remarks>
public enum EngineMode
{
    /// <summary>Parakeet on this machine, loaded at startup. The default; nothing leaves the machine.</summary>
    Parakeet,

    /// <summary>Azure OpenAI only. Parakeet is not loaded at all.</summary>
    Azure,

    /// <summary>Azure OpenAI, with Parakeet taking over when the cloud cannot be reached.</summary>
    AzureWithParakeetFallback,
}

/// <summary>Whether the chosen engine can transcribe right now.</summary>
public enum EngineReadiness
{
    /// <summary>It can.</summary>
    Ready,

    /// <summary>The offline model is not installed.</summary>
    NoModel,

    /// <summary>The cloud engine is set up but has no key.</summary>
    NoApiKey,

    /// <summary>The cloud engine has no endpoint or deployment.</summary>
    NotConfigured,

    /// <summary>The cloud engine's key was turned down (401 or 403) the last time it was used.</summary>
    KeyRejected,
}

/// <summary>Answers "can the chosen engine transcribe?" for the bar and Settings.</summary>
/// <remarks>
/// One function, so the bar's "No model" and Settings' status line can never disagree. It
/// only reports what the user can fix; whether the network is up is a per-dictation question
/// and is answered by the fallback, not here.
/// </remarks>
public static class EngineStatus
{
    /// <summary>Works out readiness from the pieces the user configures.</summary>
    /// <param name="mode">The chosen engine.</param>
    /// <param name="modelInstalled">Whether the Parakeet model is on disk.</param>
    /// <param name="cloudConfigured">Whether an endpoint and a deployment are filled in.</param>
    /// <param name="hasApiKey">Whether a key is stored.</param>
    /// <param name="keyRejected">Whether Azure turned the stored key down when it was last used.</param>
    /// <returns>The first thing standing in the way, or <see cref="EngineReadiness.Ready"/>.</returns>
    /// <remarks>
    /// In fallback mode the cloud is what is being asked for, so its setup is what is
    /// reported. A missing model there only means the fallback has nothing to fall back to,
    /// which is not a reason to call the configured cloud engine unready. A rejected key is
    /// reported in both cloud modes: it is not transient, so the fallback does not cover it.
    /// </remarks>
    public static EngineReadiness Evaluate(
        EngineMode mode, bool modelInstalled, bool cloudConfigured, bool hasApiKey, bool keyRejected = false)
    {
        if (!UsesCloud(mode))
        {
            return modelInstalled ? EngineReadiness.Ready : EngineReadiness.NoModel;
        }

        if (!cloudConfigured) return EngineReadiness.NotConfigured;
        if (!hasApiKey) return EngineReadiness.NoApiKey;
        return keyRejected ? EngineReadiness.KeyRejected : EngineReadiness.Ready;
    }

    /// <summary>What the bar says about <paramref name="readiness"/>, in a word or two.</summary>
    /// <param name="readiness">The engine's readiness.</param>
    /// <returns>The label, or null when there is nothing to say.</returns>
    public static string? ShortLabel(EngineReadiness readiness) => readiness switch
    {
        EngineReadiness.NoModel => "No model",
        EngineReadiness.NoApiKey => "Add API key",
        EngineReadiness.NotConfigured => "Set up Azure",
        EngineReadiness.KeyRejected => "Key rejected",
        _ => null,
    };

    /// <summary>Whether <paramref name="mode"/> sends audio to Azure.</summary>
    /// <param name="mode">The chosen engine.</param>
    /// <returns>True for both cloud modes.</returns>
    public static bool UsesCloud(EngineMode mode) =>
        mode is EngineMode.Azure or EngineMode.AzureWithParakeetFallback;

    /// <summary>Whether <paramref name="mode"/> may need the offline engine.</summary>
    /// <param name="mode">The chosen engine.</param>
    /// <returns>False only for cloud-only mode.</returns>
    public static bool UsesOffline(EngineMode mode) => mode != EngineMode.Azure;

    /// <summary>Whether <paramref name="mode"/> loads Parakeet at startup, as the app always has.</summary>
    /// <param name="mode">The chosen engine.</param>
    /// <returns>True for Parakeet mode, and for any value this build does not know.</returns>
    public static bool LoadsOfflineAtStartup(EngineMode mode) => !UsesCloud(mode);

    /// <summary>Whether a loaded Parakeet should stay loaded however long it sits idle.</summary>
    /// <param name="mode">The chosen engine.</param>
    /// <param name="keepWarmSetting">The "keep the offline engine warm" setting.</param>
    /// <returns>
    /// Always in Parakeet mode, where it is the engine; in fallback mode only if asked; never
    /// in cloud-only mode, where a model left over from before the switch is simply in the way.
    /// </returns>
    public static bool KeepsOfflineLoaded(EngineMode mode, bool keepWarmSetting) =>
        LoadsOfflineAtStartup(mode) || (mode == EngineMode.AzureWithParakeetFallback && keepWarmSetting);
}
