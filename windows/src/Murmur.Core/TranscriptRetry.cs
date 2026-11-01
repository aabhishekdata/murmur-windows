using Murmur.Abstractions;
using Murmur.Dictionary;

namespace Murmur.Core;

/// <summary>
/// History's "Retry with the other engine": the same audio, heard again by Parakeet if Azure
/// heard it, or by Azure if Parakeet did.
/// </summary>
/// <remarks>
/// <para>
/// Only the last few dictations can be retried — their audio is in <see cref="RecentAudio"/>,
/// in memory, and nowhere else.
/// </para>
/// <para>
/// <b>The privacy contract holds here too.</b> Audio goes to Azure only while an Azure engine
/// is the one chosen (<see cref="MayUse"/>): a Parakeet-mode user is never offered "retry with
/// Azure", however well set up the cloud is. Retrying with Parakeet is always allowed when the
/// model is installed; it never leaves the machine.
/// </para>
/// <para>
/// The retried text goes through the same text pipeline a dictation does, with today's settings,
/// and replaces the old record in place with a link back to it
/// (<see cref="TranscriptRecord.RetriedFrom"/>). Nothing is typed: the user is in the hub, and can
/// paste or copy the result from there.
/// </para>
/// </remarks>
public sealed class TranscriptRetry
{
    private readonly RecentAudio _audio;
    private readonly Func<TranscriberKind, ITranscriber?> _engineFor;
    private readonly Func<IReadOnlyList<DictionaryEntry>> _dictionary;
    private readonly Func<TextPipelineOptions> _options;
    private readonly Func<CloudProfile> _profile;
    private readonly DictationEngineOptions _limits;

    /// <summary>Creates the service.</summary>
    /// <param name="audio">The audio kept from recent dictations.</param>
    /// <param name="engineFor">
    /// The engine of a kind, or null when it may not or cannot be used right now. Read per retry,
    /// so the answer follows the engine mode and the model on disk.
    /// </param>
    /// <param name="dictionary">The dictionary, read per retry.</param>
    /// <param name="options">The text passes, read per retry.</param>
    /// <param name="profile">The cloud profile in use, recorded against an Azure retry for the cost estimate.</param>
    /// <param name="limits">The time budget; the engine's own by default.</param>
    public TranscriptRetry(
        RecentAudio audio,
        Func<TranscriberKind, ITranscriber?> engineFor,
        Func<IReadOnlyList<DictionaryEntry>> dictionary,
        Func<TextPipelineOptions> options,
        Func<CloudProfile>? profile = null,
        DictationEngineOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(engineFor);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(options);

        _audio = audio;
        _engineFor = engineFor;
        _dictionary = dictionary;
        _options = options;
        _profile = profile ?? (static () => CloudProfile.Accurate);
        _limits = limits ?? new DictationEngineOptions();
    }

    /// <summary>Whether a retry may go to <paramref name="target"/> under the user's settings.</summary>
    /// <param name="target">The engine the retry would use.</param>
    /// <param name="mode">The engine mode chosen.</param>
    /// <param name="offlineInstalled">Whether the Parakeet model is on disk.</param>
    /// <param name="cloudReady">Whether the cloud is set up, with a key that has not been rejected.</param>
    /// <returns>True for Parakeet when installed; for Azure only in an Azure mode, set up.</returns>
    public static bool MayUse(TranscriberKind target, EngineMode mode, bool offlineInstalled, bool cloudReady) => target switch
    {
        TranscriberKind.Parakeet => offlineInstalled,
        TranscriberKind.Azure => EngineStatus.UsesCloud(mode) && cloudReady,
        _ => false,
    };

    /// <summary>The engine <paramref name="record"/> would be retried with, or null when it cannot be.</summary>
    /// <param name="record">A history record.</param>
    /// <returns>
    /// The other engine — when the audio is still held, the record says which engine heard it, and
    /// the other one may be used now.
    /// </returns>
    public TranscriberKind? TargetFor(TranscriptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        TranscriberKind? other = record.Engine switch
        {
            TranscriberKind.Azure => TranscriberKind.Parakeet,
            TranscriberKind.Parakeet => TranscriberKind.Azure,
            _ => null,
        };

        if (other is not { } target || !_audio.Contains(record.Id)) return null;
        return _engineFor(target) is not null ? target : null;
    }

    /// <summary>Transcribes <paramref name="record"/>'s audio again with the other engine.</summary>
    /// <param name="record">The record to retry.</param>
    /// <param name="cancellationToken">Abandons the retry.</param>
    /// <returns>
    /// The replacement record, not yet saved — <see cref="Apply"/> does that, on the thread that owns
    /// the history — or null when there is nothing to retry or the other engine heard nothing.
    /// </returns>
    /// <exception cref="TranscriptionException">The other engine failed, or ran out of time.</exception>
    public async Task<TranscriptRecord?> RetryAsync(TranscriptRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (TargetFor(record) is not { } target) return null;
        if (!_audio.TryGet(record.Id, out var samples)) return null;
        if (_engineFor(target) is not { } engine) return null;

        var duration = TimeSpan.FromSeconds((double)samples.Length / AudioChunk.SampleRate);
        var started = _limits.Time.GetTimestamp();
        var entries = _dictionary();
        var bias = DictionaryCorrector.BiasPhrases(entries);

        using var budget = new CancellationTokenSource(_limits.TranscriptionBudget(duration), _limits.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

        var pieces = new List<string>();
        try
        {
            foreach (var piece in AudioSegmenter.Split(samples))
            {
                var text = await Task
                    .Run(() => engine.TranscribeAsync(piece, bias, linked.Token).AsTask(), linked.Token)
                    .WaitAsync(linked.Token)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(text)) pieces.Add(text.Trim());
            }
        }
        catch (OperationCanceledException ex) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException(TranscriptionFailure.Timeout, "The retry took too long.", innerException: ex);
        }

        var processed = TextPipeline.Run(string.Join(' ', pieces), _options() with { Dictionary = entries });
        if (string.IsNullOrWhiteSpace(processed.Text)) return null;

        return new TranscriptRecord
        {
            At = record.At,
            AudioSeconds = record.AudioSeconds,
            ProcessingSeconds = _limits.Time.GetElapsedTime(started).TotalSeconds,
            Text = processed.Text,
            Corrections = processed.Corrections.Count > 0 ? processed.Corrections : null,
            Engine = target,
            Profile = target == TranscriberKind.Azure ? _profile() : null,
            RetriedFrom = record.Id,
        };
    }

    /// <summary>Saves a retry over the record it came from, and keeps its audio retryable.</summary>
    /// <param name="store">The history. Call on the thread that owns it.</param>
    /// <param name="original">The record that was retried.</param>
    /// <param name="replacement">What <see cref="RetryAsync"/> returned.</param>
    /// <returns>False when the original has gone in the meantime; nothing is saved then.</returns>
    public bool Apply(TranscriptStore store, TranscriptRecord original, TranscriptRecord replacement)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);

        if (!store.Replace(original.Id, replacement)) return false;

        _audio.Rename(original.Id, replacement.Id);
        return true;
    }
}
