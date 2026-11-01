namespace Murmur.Abstractions;

/// <summary>
/// A block of captured audio: mono, 32-bit float, 16 kHz, samples in [-1, 1].
/// </summary>
/// <remarks>
/// The format is fixed at this boundary on purpose. Every speech model this app will use
/// wants 16 kHz mono float, and resampling is a device concern — so the platform layer deals
/// with whatever the hardware offers and nothing above it ever has to ask.
/// </remarks>
/// <param name="Samples">Owned by the receiver. The producer must not reuse the buffer.</param>
public readonly record struct AudioChunk(ReadOnlyMemory<float> Samples)
{
    /// <summary>The sample rate every implementation must deliver.</summary>
    public const int SampleRate = 16000;

    /// <summary>Duration of this chunk.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>
    /// Root-mean-square amplitude, 0…1. Drives the level meter.
    /// </summary>
    public float Rms()
    {
        var span = Samples.Span;
        if (span.Length == 0) return 0;

        double sum = 0;
        foreach (var sample in span) sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / span.Length);
    }
}

/// <summary>Captures microphone audio.</summary>
/// <remarks>
/// Implementations must deliver <see cref="AudioChunk"/>s already converted to 16 kHz mono
/// float, and must hand over buffers they will not touch again — the single most common
/// audio bug on every platform is a capture callback reusing one array.
/// </remarks>
public interface IAudioCapture : IAsyncDisposable
{
    /// <summary>Whether capture is currently running.</summary>
    bool IsCapturing { get; }

    /// <summary>Starts capture and yields chunks until cancelled.</summary>
    IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Raised when the push-to-talk key goes down or comes up.</summary>
public interface IHotkeySource : IDisposable
{
    /// <summary>The key is held.</summary>
    event EventHandler? Pressed;

    /// <summary>The key was released.</summary>
    event EventHandler? Released;

    /// <summary>
    /// Windows virtual-key code of the key being watched.
    /// </summary>
    /// <remarks>
    /// Settable while listening, so changing the push-to-talk key in Settings takes effect
    /// immediately rather than at the next launch. A plain <see cref="int"/> rather than an
    /// enum: the enum of supported keys lives in the platform layer, which nothing above it
    /// may reference.
    /// </remarks>
    int VirtualKey { get; set; }

    /// <summary>Begins listening.</summary>
    /// <returns>False if the hook could not be installed.</returns>
    bool Start();

    /// <summary>Stops listening.</summary>
    /// <remarks>Not named <c>Stop</c>: that is a reserved word in VB, which CA1716 flags
    /// on any interface member.</remarks>
    void StopListening();
}

/// <summary>Esc as "cancel this dictation", for a hotkey source that can watch it.</summary>
/// <remarks>
/// <para>
/// Optional, and separate from <see cref="IHotkeySource"/> so a fake that has no opinion about
/// Esc need not implement it. When the source implements both, the engine arms it while it is
/// recording or transcribing and disarms it at rest.
/// </para>
/// <para>
/// <b>The one key Murmur swallows.</b> While armed, an Esc press is consumed — its key-down and its
/// key-up, as a pair, decided at the key-down — so cancelling a dictation does not also close the
/// dialog or clear the field the user is dictating into. Disarmed, Esc is never touched. Deciding
/// when to arm is logic, and lives in <c>Murmur.Core</c>; the implementation only obeys the flag.
/// </para>
/// </remarks>
public interface ICancelKeySource
{
    /// <summary>Esc went down while <see cref="CancelArmed"/> was set. Raised on the hook's thread.</summary>
    event EventHandler? CancelPressed;

    /// <summary>Whether Esc is being watched (and swallowed) right now.</summary>
    /// <remarks>Read by the hook on every Esc key-down; written from whichever thread the engine is on.</remarks>
    bool CancelArmed { get; set; }
}

/// <summary>Types text into whatever application currently has focus.</summary>
public interface ITextInjector
{
    /// <summary>Inserts <paramref name="text"/> at the caret of the focused control.</summary>
    /// <returns>False if the text could not be delivered.</returns>
    ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken);

    /// <summary>Inserts <paramref name="text"/>, optionally exactly as stored.</summary>
    /// <param name="text">What to insert.</param>
    /// <param name="verbatim">
    /// True for a saved prompt: it must arrive with every line break where it was written, so an
    /// injector that can paste should, however short the text.
    /// </param>
    /// <param name="cancellationToken">Cancels the insertion.</param>
    /// <returns>False if the text could not be delivered.</returns>
    ValueTask<bool> InjectAsync(string text, bool verbatim, CancellationToken cancellationToken) =>
        InjectAsync(text, cancellationToken);

    /// <summary>
    /// Presses Enter in the focused application.
    /// </summary>
    /// <param name="cancellationToken">Cancels the keystroke.</param>
    /// <returns>False if the keystroke could not be delivered.</returns>
    /// <remarks>
    /// Separate from <see cref="InjectAsync(string, CancellationToken)"/> rather than a newline
    /// inside the text, because the two mean different things. A newline in a chat box is a line break; Enter is send.
    /// This is what the spoken "press enter" command uses, and nothing else calls it.
    /// </remarks>
    ValueTask<bool> PressEnterAsync(CancellationToken cancellationToken);
}

/// <summary>One microphone the machine can capture from.</summary>
/// <param name="Id">
/// An opaque, stable device identifier. Persisted in settings, so it must survive a reboot.
/// </param>
/// <param name="Name">What to show the user.</param>
/// <param name="IsDefault">Whether this is the device capture would pick on its own.</param>
public readonly record struct AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Lists the microphones available for capture.</summary>
/// <remarks>
/// Separate from <see cref="IAudioCapture"/> on purpose. Capture is bound to one device for the
/// life of a recording; enumerating what exists is a question about the machine, asked from the
/// UI thread while nothing is being recorded. Folding it into the capture interface would mean
/// every fake capture in the test suite had to answer a question it has no opinion about.
/// </remarks>
public interface IAudioDeviceCatalog
{
    /// <summary>Every active capture device.</summary>
    /// <returns>
    /// The devices, or an empty list when none can be enumerated. Never throws: a machine with
    /// no microphone is a normal state, not an error.
    /// </returns>
    IReadOnlyList<AudioDeviceInfo> List();
}

/// <summary>Turns audio into text.</summary>
public interface ITranscriber : IAsyncDisposable
{
    /// <summary>Whether the model is loaded and ready.</summary>
    bool IsReady { get; }

    /// <summary>Loads the model. Slow — call once, at startup or first use.</summary>
    ValueTask<bool> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Transcribes one utterance.</summary>
    /// <param name="samples">16 kHz mono float, in [-1, 1].</param>
    /// <param name="biasPhrases">
    /// Dictionary terms to bias the recogniser toward. May be ignored by engines that don't
    /// support it — the correction pass is what actually guarantees spelling.
    /// </param>
    /// <param name="cancellationToken">Cancels a transcription in flight.</param>
    ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken);
}

/// <summary>
/// Wall-clock time, behind an interface so timing logic is testable.
/// </summary>
/// <remarks>
/// Anything that measures a duration takes one of these. A test that depends on the real
/// clock is a test that fails on a slow CI runner.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant.</summary>
    DateTimeOffset Now { get; }
}

/// <inheritdoc cref="IClock"/>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>Which speech engine produced, or will produce, a transcript.</summary>
/// <remarks>
/// Recorded on every dictation, so history can say where the words were heard. That matters
/// more than it looks: once cloud transcription exists, "did this leave the machine?" is a
/// question a user is entitled to an answer to after the fact.
/// </remarks>
public enum TranscriberKind
{
    /// <summary>A transcriber that does not say — a test double, usually.</summary>
    Unknown,

    /// <summary>Parakeet, on this machine. Nothing leaves it.</summary>
    Parakeet,

    /// <summary>Azure OpenAI, with the user's own key.</summary>
    Azure,
}

/// <summary>A transcriber that can say which engine it is.</summary>
/// <remarks>
/// Optional, and separate from <see cref="ITranscriber"/> on purpose: every fake in the test
/// suite would otherwise have to answer a question it has no opinion about. A transcriber that
/// does not implement this is reported as <see cref="TranscriberKind.Unknown"/>.
/// </remarks>
public interface ITranscriberDescriptor
{
    /// <summary>The engine this is, or for a composite one, the engine it would use next.</summary>
    TranscriberKind Kind { get; }

    /// <summary>The engine that answered the most recent call.</summary>
    /// <remarks>
    /// Differs from <see cref="Kind"/> only for a composite transcriber that fell back. Read
    /// right after a call returns; one dictation is transcribed at a time, so it cannot be
    /// overwritten in between.
    /// </remarks>
    TranscriberKind LastUsedKind => Kind;

    /// <summary>Whether the most recent call was answered by a fallback engine.</summary>
    bool LastFellBack => false;
}

/// <summary>Why a transcription failed.</summary>
public enum TranscriptionFailure
{
    /// <summary>Anything not covered below.</summary>
    Unknown,

    /// <summary>The service could not be reached: DNS, a dropped connection, no network.</summary>
    Network,

    /// <summary>The request took longer than it was allowed (HTTP 408, or a local timeout).</summary>
    Timeout,

    /// <summary>The service asked us to slow down (HTTP 429).</summary>
    RateLimited,

    /// <summary>The service failed on its side (HTTP 5xx).</summary>
    ServerError,

    /// <summary>The request itself was wrong (HTTP 400). Retrying sends the same mistake.</summary>
    BadRequest,

    /// <summary>The key was rejected (HTTP 401).</summary>
    Unauthorized,

    /// <summary>The key is valid but may not use this resource (HTTP 403).</summary>
    Forbidden,

    /// <summary>The endpoint or deployment does not exist (HTTP 404).</summary>
    NotFound,

    /// <summary>The engine has not been set up: no endpoint, deployment or key.</summary>
    NotConfigured,

    /// <summary>The offline model is missing or could not be loaded.</summary>
    NoModel,

    /// <summary>The recording is larger than the service accepts (HTTP 413, or caught before sending).</summary>
    PayloadTooLarge,

    /// <summary>A proxy between here and the service wants credentials (HTTP 407).</summary>
    ProxyAuthenticationRequired,
}

/// <summary>A transcription that failed for a reason worth telling the user.</summary>
/// <remarks>
/// <para>
/// The split that matters is <see cref="IsTransient"/>. A transient failure says nothing
/// about the user's setup — the network blinked, the service was busy — so the offline engine
/// may take over. A permanent one (a rejected key, a deployment that does not exist) must be
/// shown, not papered over: falling back silently on a bad key would leave the user believing
/// the cloud engine works while every word was in fact heard by the other one.
/// </para>
/// <para>
/// The message must never contain a key. Implementations build it from the failure and the
/// status code only.
/// </para>
/// </remarks>
public sealed class TranscriptionException : Exception
{
    /// <summary>Creates an exception for an unclassified failure.</summary>
    public TranscriptionException()
        : this(TranscriptionFailure.Unknown, "Transcription failed.")
    {
    }

    /// <summary>Creates an exception for an unclassified failure.</summary>
    /// <param name="message">What went wrong.</param>
    public TranscriptionException(string message)
        : this(TranscriptionFailure.Unknown, message)
    {
    }

    /// <summary>Creates an exception for an unclassified failure.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public TranscriptionException(string message, Exception? innerException)
        : this(TranscriptionFailure.Unknown, message, statusCode: null, innerException)
    {
    }

    /// <summary>Creates an exception for a classified failure.</summary>
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What went wrong, without secrets.</param>
    /// <param name="statusCode">The HTTP status, when there was one.</param>
    /// <param name="innerException">The underlying failure.</param>
    public TranscriptionException(
        TranscriptionFailure failure,
        string message,
        int? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    /// <summary>Why it failed.</summary>
    public TranscriptionFailure Failure { get; }

    /// <summary>The HTTP status, or null when the failure was not an HTTP response.</summary>
    public int? StatusCode { get; }

    /// <summary>Whether trying again — or trying another engine — could succeed.</summary>
    public bool IsTransient => IsTransientFailure(Failure);

    /// <summary>Whether <paramref name="failure"/> is one that could clear up by itself.</summary>
    /// <param name="failure">The failure.</param>
    /// <returns>True for network trouble, timeouts, rate limits and server errors.</returns>
    public static bool IsTransientFailure(TranscriptionFailure failure) => failure is
        TranscriptionFailure.Network or
        TranscriptionFailure.Timeout or
        TranscriptionFailure.RateLimited or
        TranscriptionFailure.ServerError;
}

/// <summary>Encrypts small secrets so that only the current user on this machine can read them.</summary>
/// <remarks>
/// On Windows this is DPAPI, bound to the user's logon credentials. A key is encrypted rather
/// than hashed because it has to be sent to the service, so it must come back out exactly as
/// it went in.
/// </remarks>
public interface IDataProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>.</summary>
    /// <param name="plaintext">The secret's bytes.</param>
    /// <returns>An opaque blob that only <see cref="Unprotect"/>, run as the same user, can read.</returns>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypts what <see cref="Protect"/> produced.</summary>
    /// <param name="protectedData">The blob.</param>
    /// <returns>The secret's bytes.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The blob is corrupt, or was protected by another user or on another machine.
    /// </exception>
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

/// <summary>Keeps named secrets, such as an API key, out of the settings file.</summary>
public interface ISecretStore
{
    /// <summary>Reads a secret.</summary>
    /// <param name="name">Which secret.</param>
    /// <returns>
    /// The secret, or null when none is stored <i>or</i> the stored one cannot be read — a
    /// profile restored onto another machine, say. <see cref="Contains"/> tells the two apart.
    /// </returns>
    string? Read(string name);

    /// <summary>Stores a secret, replacing any previous one.</summary>
    /// <param name="name">Which secret.</param>
    /// <param name="value">The secret.</param>
    void Write(string name, string value);

    /// <summary>Forgets a secret. Does nothing when there is none.</summary>
    /// <param name="name">Which secret.</param>
    void Remove(string name);

    /// <summary>Whether anything is stored under <paramref name="name"/>, readable or not.</summary>
    /// <param name="name">Which secret.</param>
    /// <returns>True when a secret exists, even one that can no longer be decrypted.</returns>
    bool Contains(string name);
}
