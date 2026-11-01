using Murmur.Abstractions;
using Murmur.Cloud;
using Murmur.Core;

namespace Murmur.App;

/// <summary>What the Settings screen needs to set up the cloud engine: the key, and a test.</summary>
/// <remarks>
/// Gathered here so a view takes one object rather than reaching into the composition for
/// three, and so the "a 401 means the key is rejected" rule is written once for the test
/// button and once for dictation, both in this project.
/// </remarks>
public sealed class CloudServices
{
    private readonly AppSettings _settings;
    private readonly ConnectionTester _tester;

    /// <summary>Gathers the cloud pieces.</summary>
    /// <param name="settings">Where the endpoint and deployments are.</param>
    /// <param name="key">The user's key.</param>
    /// <param name="tester">Sends the test request.</param>
    public CloudServices(AppSettings settings, CloudKey key, ConnectionTester tester)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tester);

        _settings = settings;
        Key = key;
        _tester = tester;
    }

    /// <summary>The user's Azure key.</summary>
    public CloudKey Key { get; }

    /// <summary>Tests one profile's deployment with a second of tone.</summary>
    /// <param name="profile">Which deployment.</param>
    /// <param name="cancellationToken">Abandons the test.</param>
    /// <returns>The result, or a failed one explaining what is missing before a request could be sent.</returns>
    public async Task<ConnectionTestResult> TestAsync(CloudProfile profile, CancellationToken cancellationToken = default)
    {
        var options = AzureProfiles.Resolve(_settings.Data, profile);
        if (options is null)
        {
            return NotSent(profile, AzureEndpoint.TryNormalize(_settings.Data.AzureEndpoint, out _, out var problem)
                ? $"Enter the {AzureProfiles.Label(profile)} deployment name first."
                : problem ?? "Enter the endpoint first.");
        }

        if (Key.Read(out var generation) is not { } key) return NotSent(profile, "Save your API key first.");

        var result = await _tester.TestAsync(options, key, cancellationToken).ConfigureAwait(false);

        // Against the key that was sent: one saved while the test was out is not judged by it.
        if (result.Succeeded) Key.MarkAccepted(generation);
        else if (IsKeyRejection(result.Failure)) Key.MarkRejected(generation);

        return result;
    }

    /// <summary>The profiles that have a deployment filled in, Accurate first.</summary>
    /// <returns>Zero, one or both profiles.</returns>
    public IReadOnlyList<CloudProfile> ConfiguredProfiles() =>
        [.. new[] { CloudProfile.Accurate, CloudProfile.Fast }
            .Where(p => AzureProfiles.DeploymentFor(_settings.Data, p) is not null)];

    /// <summary>Whether <paramref name="failure"/> means the key itself was turned down.</summary>
    /// <param name="failure">The failure, if any.</param>
    /// <returns>True for 401 only.</returns>
    /// <remarks>
    /// A 403 is not the key. Azure answers 403 to a valid key when the resource's network rules
    /// shut this PC out — public access off, a firewall or a private endpoint, which on a company
    /// laptop off the VPN is the usual case. Calling that a rejected key would have the user
    /// replace a key that works and never look at the real cause.
    /// </remarks>
    public static bool IsKeyRejection(TranscriptionFailure? failure) =>
        failure is TranscriptionFailure.Unauthorized;

    private static ConnectionTestResult NotSent(CloudProfile profile, string message) => new(
        AzureProfiles.Label(profile),
        Succeeded: false,
        StatusCode: null,
        TimeSpan.Zero,
        Region: null,
        TranscriptionFailure.NotConfigured,
        message);
}

/// <summary>Keeps the key in memory only, for as long as the process runs.</summary>
/// <remarks>
/// Used where there is no DPAPI — off Windows, and in previews — so the cloud settings can be
/// exercised without a key ever being written to disk unencrypted.
/// </remarks>
internal sealed class SessionSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public string? Read(string name)
    {
        lock (_lock) return _secrets.GetValueOrDefault(name);
    }

    public void Write(string name, string value)
    {
        lock (_lock) _secrets[name] = value;
    }

    public void Remove(string name)
    {
        lock (_lock) _secrets.Remove(name);
    }

    public bool Contains(string name)
    {
        lock (_lock) return _secrets.ContainsKey(name);
    }
}

/// <summary>A handler that refuses every request, for previews: they must never reach a network.</summary>
internal sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("Previews make no network calls."));
}
