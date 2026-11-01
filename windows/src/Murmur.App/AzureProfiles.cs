using Murmur.Cloud;
using Murmur.Core;

namespace Murmur.App;

/// <summary>
/// Turns the cloud settings into the request options for the deployment in use right now.
/// </summary>
/// <remarks>
/// <para>
/// Called on every request, not once at startup, which is the whole of how the Accurate/Fast
/// switch in the tray and bar menus takes effect on the very next dictation with no restart.
/// It is string handling only, so being called that often costs nothing.
/// </para>
/// <para>
/// Two profiles and no more: Accurate is <c>gpt-4o-transcribe</c> and Fast is
/// <c>gpt-4o-mini-transcribe</c>, each with its own deployment name, because that is what an
/// Azure resource has to have deployed. <c>gpt-4o-transcribe-diarize</c> is not offered: it
/// rejects the prompt that carries the dictionary terms, and is being retired.
/// </para>
/// </remarks>
public static class AzureProfiles
{
    /// <summary>The fewest seconds a cloud request may be given.</summary>
    public const int MinimumTimeoutSeconds = 5;

    /// <summary>The most seconds a cloud request may be given.</summary>
    public const int MaximumTimeoutSeconds = 300;

    /// <summary>The model each profile is meant to be a deployment of.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The Azure model name.</returns>
    public static string ModelFor(CloudProfile profile) =>
        profile == CloudProfile.Fast ? "gpt-4o-mini-transcribe" : "gpt-4o-transcribe";

    /// <summary>What to call <paramref name="profile"/> on screen.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>"Accurate" or "Fast".</returns>
    public static string Label(CloudProfile profile) => profile == CloudProfile.Fast ? "Fast" : "Accurate";

    /// <summary>The deployment name the user gave for <paramref name="profile"/>, trimmed, or null.</summary>
    /// <param name="data">The settings.</param>
    /// <param name="profile">The profile.</param>
    /// <returns>The deployment name, or null when the field is blank.</returns>
    public static string? DeploymentFor(SettingsData data, CloudProfile profile)
    {
        ArgumentNullException.ThrowIfNull(data);

        var value = profile == CloudProfile.Fast ? data.AzureDeploymentFast : data.AzureDeploymentAccurate;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The profile dictation will actually use.</summary>
    /// <param name="data">The settings.</param>
    /// <returns>
    /// The chosen profile — unless only one deployment is filled in, in which case that one. A
    /// user who set up only Fast has made their choice, whatever the default says.
    /// </returns>
    public static CloudProfile EffectiveProfile(SettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var accurate = DeploymentFor(data, CloudProfile.Accurate) is not null;
        var fast = DeploymentFor(data, CloudProfile.Fast) is not null;

        if (fast && !accurate) return CloudProfile.Fast;
        if (accurate && !fast) return CloudProfile.Accurate;
        return data.AzureActiveProfile;
    }

    /// <summary>The options for the profile in use, or null when the cloud is not set up.</summary>
    /// <param name="data">The settings.</param>
    /// <returns>Request options, or null.</returns>
    public static AzureTranscriptionOptions? Resolve(SettingsData data) => Resolve(data, EffectiveProfile(data));

    /// <summary>The options for <paramref name="profile"/>, or null when it is not set up.</summary>
    /// <param name="data">The settings.</param>
    /// <param name="profile">Which deployment.</param>
    /// <returns>Request options, or null when the endpoint is unusable or the deployment blank.</returns>
    public static AzureTranscriptionOptions? Resolve(SettingsData data, CloudProfile profile)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!AzureEndpoint.TryNormalize(data.AzureEndpoint, out var endpoint, out _)) return null;
        if (DeploymentFor(data, profile) is not { } deployment) return null;

        return new AzureTranscriptionOptions
        {
            Endpoint = endpoint,
            Deployment = deployment,
            ApiVersion = string.IsNullOrWhiteSpace(data.AzureApiVersion)
                ? AzureTranscriptionOptions.DefaultApiVersion
                : data.AzureApiVersion.Trim(),
            // Hindi sends hi; Hinglish sends no code and asks for romanised Hindi in the prompt.
            Language = LanguageModes.Code(data.Language, data.CloudLanguage),
            LanguageHint = LanguageModes.Hint(data.Language),
            RequestTimeout = RequestTimeout(data),
        };
    }

    /// <summary>Whether the profile in use has an endpoint and a deployment.</summary>
    /// <param name="data">The settings.</param>
    /// <returns>True when a request could be built; says nothing about the key.</returns>
    public static bool IsConfigured(SettingsData data) => Resolve(data) is not null;

    /// <summary>How long one dictation may spend with the cloud.</summary>
    /// <param name="data">The settings.</param>
    /// <returns>The configured timeout, kept within sane bounds; the default when unset.</returns>
    public static TimeSpan RequestTimeout(SettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var seconds = data.CloudRequestTimeoutSeconds <= 0
            ? (int)AzureTranscriptionOptions.DefaultRequestTimeout.TotalSeconds
            : Math.Clamp(data.CloudRequestTimeoutSeconds, MinimumTimeoutSeconds, MaximumTimeoutSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Makes <paramref name="profile"/> the active one.</summary>
    /// <param name="settings">The settings to change.</param>
    /// <param name="profile">The profile to use from the next dictation on.</param>
    public static void Select(AppSettings settings, CloudProfile profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Data.AzureActiveProfile == profile) return;

        settings.Update(settings.Data with { AzureActiveProfile = profile });
    }
}
