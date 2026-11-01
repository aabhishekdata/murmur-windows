using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>What the app knows about the user's Azure key.</summary>
public enum CloudKeyState
{
    /// <summary>No key has been saved.</summary>
    Missing,

    /// <summary>A key is saved and, as far as anyone knows, works.</summary>
    Saved,

    /// <summary>A key file exists but cannot be decrypted here; the user must enter it again.</summary>
    Unreadable,

    /// <summary>A key is saved, and Azure turned it down (401 or 403) the last time it was used.</summary>
    Rejected,
}

/// <summary>
/// The user's Azure API key: entered once, kept encrypted, read when a request needs it.
/// </summary>
/// <remarks>
/// <para>
/// The decrypted key is held in memory after the first read, so a dictation does not pay for a
/// file read and a DPAPI call. It is never written anywhere but the secret store, never logged,
/// and never shown: the UI gets <see cref="Masked"/>, which is the last four characters only —
/// enough to tell which of a resource's two keys is saved, not enough to use.
/// </para>
/// <para>
/// <see cref="State"/> also remembers whether Azure rejected the key, so the bar can say "Key
/// rejected" instead of failing on every dictation with no explanation. Saving a key clears it,
/// and so does any request the key gets through.
/// </para>
/// <para>
/// Every save and removal starts a new <see cref="Generation"/>. A request records the
/// generation of the key it sent, and reports back with it, so a 401 that comes back for a key
/// the user has since replaced cannot mark the new one as rejected.
/// </para>
/// </remarks>
public sealed class CloudKey
{
    /// <summary>The name the key is stored under, which is also its file name.</summary>
    public const string SecretName = "azure-key";

    private readonly ISecretStore _store;
    private readonly Lock _lock = new();

    private bool _loaded;
    private string? _value;
    private bool _unreadable;
    private bool _rejected;
    private long _generation;

    /// <summary>Wraps the store the key lives in.</summary>
    /// <param name="store">Usually a <see cref="ProtectedSecretFile"/>.</param>
    public CloudKey(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Raised when the key is saved or removed, or is found to be rejected or accepted.</summary>
    /// <remarks>May fire on any thread; a view marshals it.</remarks>
    public event EventHandler? Changed;

    /// <summary>The key, or null when none is saved or it cannot be read.</summary>
    public string? Value => Read(out _);

    /// <summary>Which key is current: changes on every save and removal.</summary>
    public long Generation
    {
        get
        {
            lock (_lock) return _generation;
        }
    }

    /// <summary>Whether a usable key is saved.</summary>
    public bool HasKey => Value is not null;

    /// <summary>Where things stand.</summary>
    public CloudKeyState State
    {
        get
        {
            // Whether an unreadable file is there is found out with the first read and kept,
            // so this never touches the disk again: the bar asks on every keystroke in Settings.
            var value = Value;
            lock (_lock)
            {
                if (value is null) return _unreadable ? CloudKeyState.Unreadable : CloudKeyState.Missing;
                return _rejected ? CloudKeyState.Rejected : CloudKeyState.Saved;
            }
        }
    }

    /// <summary>The key, together with the generation to report a request's outcome against.</summary>
    /// <param name="generation">Pass to <see cref="MarkRejected(long)"/> or <see cref="MarkAccepted(long)"/>.</param>
    /// <returns>The key, or null when none is saved or it cannot be read.</returns>
    public string? Read(out long generation)
    {
        lock (_lock)
        {
            if (!_loaded)
            {
                _value = _store.Read(SecretName);
                _unreadable = _value is null && _store.Contains(SecretName);
                _loaded = true;
            }

            generation = _generation;
            return _value;
        }
    }

    /// <summary>"•••• " and the last four characters, or null when there is no key.</summary>
    public string? Masked => Value is { } key
        ? "•••• " + (key.Length > 4 ? key[^4..] : key)
        : null;

    /// <summary>Saves a new key, replacing any old one.</summary>
    /// <param name="key">What the user pasted; surrounding whitespace is ignored.</param>
    /// <exception cref="ArgumentException">The key is empty, or has spaces inside it.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(string key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ArgumentException("The key is empty.", nameof(key));

        // A key never contains whitespace; one that does is two things pasted together, and
        // HttpClient would refuse to send it in a header anyway.
        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("That does not look like a key: it has spaces in it.", nameof(key));
        }

        _store.Write(SecretName, trimmed);

        lock (_lock)
        {
            _value = trimmed;
            _loaded = true;
            _unreadable = false;
            _rejected = false;
            _generation++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the key, here and on disk.</summary>
    public void Remove()
    {
        _store.Remove(SecretName);

        lock (_lock)
        {
            _value = null;
            _loaded = true;
            _unreadable = false;
            _rejected = false;
            _generation++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records that Azure turned the current key down.</summary>
    public void MarkRejected() => MarkRejected(Generation);

    /// <summary>Records that Azure turned down the key a request sent.</summary>
    /// <param name="generation">From <see cref="Read(out long)"/> when the request started.</param>
    /// <remarks>Ignored when the key has been replaced or removed since.</remarks>
    public void MarkRejected(long generation) => SetRejected(true, generation);

    /// <summary>Records that a request with the current key got through.</summary>
    public void MarkAccepted() => MarkAccepted(Generation);

    /// <summary>Records that a request got through with the key it sent.</summary>
    /// <param name="generation">From <see cref="Read(out long)"/> when the request started.</param>
    /// <remarks>Ignored when the key has been replaced or removed since.</remarks>
    public void MarkAccepted(long generation) => SetRejected(false, generation);

    private void SetRejected(bool rejected, long generation)
    {
        lock (_lock)
        {
            if (generation != _generation || _rejected == rejected) return;
            _rejected = rejected;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    /// <remarks>Overridden so that nothing which prints this object can print the key.</remarks>
    public override string ToString() => $"CloudKey({State})";
}
