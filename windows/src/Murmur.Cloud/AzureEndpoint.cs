using System.Diagnostics.CodeAnalysis;

namespace Murmur.Cloud;

/// <summary>Turns whatever the user pasted into the endpoint field into a resource URL.</summary>
/// <remarks>
/// <para>
/// People paste what the Azure portal shows them, and the portal shows several things: the bare
/// resource URL, the full "target URI" of a deployment with its path and api-version, or just
/// a host name. All of those name the same resource, so all of them are accepted.
/// </para>
/// <para>
/// <b>https is required.</b> The key travels in a header; over plain http it would cross the
/// network readable by anyone on the path. The app has no exception. The opt-in end-to-end suite
/// reaches its stand-in for the service over plain http on a loopback host (<c>localhost</c>,
/// <c>127.0.0.1</c>, <c>::1</c>), and turns that on for its own process only through
/// <see cref="AllowLoopbackHttp"/>, which nothing in the app sets. It is not offered to users: a
/// loopback port is shared by every logon session on the PC, so another user's process listening
/// on it would receive the key.
/// </para>
/// <para>
/// Azure's own hosts are <c>*.openai.azure.com</c>, <c>*.cognitiveservices.azure.com</c> and
/// <c>*.services.ai.azure.com</c> — the last is what Azure AI Foundry shows, often with a
/// <c>/api/projects/…</c> path that is not part of the OpenAI route, so on all three the path is
/// dropped. Other hosts are allowed with a warning rather than refused. Enterprise tenants commonly front Azure OpenAI
/// with an API gateway on their own domain, and refusing those would lock out the very users
/// who most need their own endpoint. On such a host a path before <c>/openai</c> is kept, since
/// a gateway usually routes on it.
/// </para>
/// </remarks>
public static class AzureEndpoint
{
    private static readonly string[] AzureSuffixes =
        [".openai.azure.com", ".cognitiveservices.azure.com", ".services.ai.azure.com"];

    private static volatile bool _allowLoopbackHttp;

    /// <summary>
    /// Whether <see cref="TryNormalize(string?, out Uri?, out string?)"/> accepts plain http to a
    /// loopback host, with a warning. False unless the end-to-end suite sets it for its process.
    /// </summary>
    internal static bool AllowLoopbackHttp
    {
        get => _allowLoopbackHttp;
        set => _allowLoopbackHttp = value;
    }

    /// <summary>Normalizes a pasted endpoint.</summary>
    /// <param name="text">What the user typed or pasted.</param>
    /// <param name="endpoint">The resource URL, with no query and no trailing slash.</param>
    /// <param name="problem">
    /// When this returns true, a warning worth showing, or null; when it returns false, why the
    /// text was refused.
    /// </param>
    /// <returns>Whether <paramref name="text"/> names a usable endpoint.</returns>
    public static bool TryNormalize(
        string? text,
        [NotNullWhen(true)] out Uri? endpoint,
        out string? problem) =>
        TryNormalizeCore(text, AllowLoopbackHttp, out endpoint, out problem);

    /// <inheritdoc cref="TryNormalize(string?, out Uri?, out string?)"/>
    /// <param name="text">What the user typed or pasted.</param>
    /// <param name="allowLoopbackHttp">Whether plain http to a loopback host is accepted, with a warning.</param>
    /// <param name="endpoint">The resource URL, with no query and no trailing slash.</param>
    /// <param name="problem">
    /// When this returns true, a warning worth showing, or null; when it returns false, why the
    /// text was refused.
    /// </param>
    internal static bool TryNormalizeCore(
        string? text,
        bool allowLoopbackHttp,
        [NotNullWhen(true)] out Uri? endpoint,
        out string? problem)
    {
        endpoint = null;
        problem = null;

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            problem = "Enter the endpoint from the resource's Keys and Endpoint page.";
            return false;
        }

        // A bare host name is what some people copy; it can only mean https.
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            problem = "That is not a URL. It should look like https://your-resource.openai.azure.com.";
            return false;
        }

        var https = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var loopbackHttp = !https
            && allowLoopbackHttp
            && string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && IsLoopbackHost(parsed);
        if (!https && !loopbackHttp)
        {
            problem = "The endpoint must start with https:// — the key would otherwise be sent unencrypted.";
            return false;
        }

        var isAzure = IsAzureHost(parsed.Host);

        // Everything from /openai onwards is the per-request part, added back when a request
        // is built. On an Azure host nothing before it is meaningful either.
        var path = parsed.AbsolutePath;
        var cut = path.IndexOf("/openai", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0) path = path[..cut];
        if (isAzure) path = string.Empty;
        path = path.TrimEnd('/');

        var builder = new UriBuilder(parsed.Scheme, parsed.Host, parsed.Port, path)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };

        // UriBuilder writes :443 back out only when it was not the default, which is the form
        // worth showing the user.
        endpoint = new Uri(builder.Uri.AbsoluteUri.TrimEnd('/'));

        if (loopbackHttp)
        {
            problem = $"Plain http to {parsed.Host} stays on this PC and is meant for a local test "
                    + "server only. A real Azure endpoint needs https://.";
        }
        else if (!isAzure)
        {
            problem = $"{parsed.Host} is not an Azure OpenAI host. That is fine for a company "
                    + "gateway; otherwise check the endpoint.";
        }

        return true;
    }

    /// <summary>Whether <paramref name="uri"/> names this machine's loopback interface.</summary>
    /// <param name="uri">An absolute URI.</param>
    /// <returns>
    /// True for <c>localhost</c> and the loopback addresses only. A name that merely starts with
    /// "localhost" (<c>localhost.example.com</c>) resolves through DNS like any other and is not.
    /// </returns>
    private static bool IsLoopbackHost(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(uri.DnsSafeHost, out var address) && System.Net.IPAddress.IsLoopback(address));

    /// <summary>Whether <paramref name="host"/> is one of Azure OpenAI's own domains.</summary>
    /// <param name="host">A host name.</param>
    /// <returns>
    /// True for <c>*.openai.azure.com</c>, <c>*.cognitiveservices.azure.com</c> and Azure AI
    /// Foundry's <c>*.services.ai.azure.com</c>.
    /// </returns>
    public static bool IsAzureHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return AzureSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
