using System.Net;

namespace Murmur.Cloud;

/// <summary>The one <see cref="HttpClient"/> the app talks to Azure through.</summary>
/// <remarks>
/// <para>
/// One for the life of the process, so the TLS connection is reused from one dictation to the
/// next — the handshake is a noticeable slice of a short utterance's round trip. Connections
/// are recycled every ten minutes all the same, so a DNS change behind the endpoint is
/// eventually noticed, and dropped after two idle minutes, so an idle tray app holds none.
/// </para>
/// <para>
/// The system proxy is used with the signed-in user's credentials, which is what corporate
/// networks expect of a desktop app. A proxy that still refuses comes back as a 407, which the
/// client reports as such rather than as a generic network failure.
/// </para>
/// <para>
/// Redirects are not followed. The key travels in a custom <c>api-key</c> header, which .NET
/// carries on to wherever a redirect points — unlike <c>Authorization</c>, which it drops — so
/// following one would hand the key to any host a gateway names. Azure never redirects this
/// call; a 3xx is reported, asking for the final URL.
/// </para>
/// <para>
/// <see cref="HttpClient.Timeout"/> is infinite on purpose: <see cref="AzureTranscriptionClient"/>
/// enforces its own budget across retries, and a second timeout underneath it would cut a
/// request off with an error that looks like the user cancelled.
/// </para>
/// </remarks>
public static class AzureHttp
{
    /// <summary>How long establishing a connection may take.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates the handler the shared client uses.</summary>
    /// <returns>A configured handler.</returns>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = ConnectTimeout,
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
    };

    /// <summary>Creates the shared client.</summary>
    /// <param name="handler">The handler; defaults to <see cref="CreateHandler"/>'s. Owned by the client.</param>
    /// <returns>A client with no timeout of its own.</returns>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        new(handler ?? CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
}
