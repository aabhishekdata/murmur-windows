using System.Runtime.InteropServices;

namespace Murmur.Core;

/// <summary>
/// The audio of the last few dictations, so one can be heard again by the other engine: in
/// memory, and on disk for a day when the user opts in.
/// </summary>
/// <remarks>
/// <para>
/// <b>In memory by default.</b> Gone when the app quits, when history is cleared, and as soon as
/// newer dictations push it out: the newest <see cref="Capacity"/> utterances are kept, and never
/// more than <see cref="MaxTotal"/> of audio between them, so one very long dictation cannot pin a
/// large buffer for the life of the process.
/// </para>
/// <para>
/// <b>On disk only when asked.</b> With a directory and <c>keepOnDisk</c> answering true, every
/// utterance kept is also written there as raw 16 kHz float, so a retry still works after a
/// restart. Each file lives <see cref="DiskLifetime"/> from when it was written and no longer:
/// <see cref="Purge"/> deletes it, from a timer armed only while files remain, and nothing older
/// is ever read back. Deleting a row, "Delete all" and switching the setting off delete the files.
/// </para>
/// <para>
/// Keyed by the dictation's id, which the history record shares. Called from the engine's
/// processing thread, the UI thread and the purge timer, so it locks. A disk failure never
/// stops a dictation: it is reported through <see cref="DiskFailed"/> and the memory copy stands.
/// </para>
/// </remarks>
public sealed class RecentAudio : IDisposable
{
    /// <summary>How many utterances are kept in memory.</summary>
    public const int Capacity = 5;

    /// <summary>The most audio kept in memory across all of them: ten minutes, about 38 MB.</summary>
    public static readonly TimeSpan MaxTotal = TimeSpan.FromMinutes(10);

    /// <summary>How long an utterance written to disk is kept there.</summary>
    public static readonly TimeSpan DiskLifetime = TimeSpan.FromHours(24);

    private const string Extension = ".f32";

    /// <summary>A file being written, renamed to <see cref="Extension"/> once it is complete.</summary>
    private const string TemporarySuffix = ".tmp";

    /// <summary>
    /// How old a temporary file must be before a purge counts it as left over from a failed write
    /// or a crash, rather than one being written right now.
    /// </summary>
    public static readonly TimeSpan TemporaryGrace = TimeSpan.FromMinutes(1);

    private static readonly long MaxSamples = (long)(MaxTotal.TotalSeconds * Abstractions.AudioChunk.SampleRate);

    private readonly LinkedList<(Guid Id, ReadOnlyMemory<float> Samples)> _entries = new();
    private readonly Lock _lock = new();
    private readonly string? _directory;
    private readonly Func<bool> _keepOnDisk;
    private readonly TimeProvider _time;
    private readonly ITimer? _purgeTimer;

    /// <summary>Memory only.</summary>
    public RecentAudio()
        : this(directory: null, keepOnDisk: null)
    {
    }

    /// <summary>Memory, and <paramref name="directory"/> while <paramref name="keepOnDisk"/> says so.</summary>
    /// <param name="directory">Where the files go; null for memory only.</param>
    /// <param name="keepOnDisk">The opt-in, read on every write and read.</param>
    /// <param name="time">Ages the files and drives the purge timer; replaced in tests.</param>
    public RecentAudio(string? directory, Func<bool>? keepOnDisk, TimeProvider? time = null)
    {
        _directory = directory;
        _keepOnDisk = keepOnDisk ?? (static () => false);
        _time = time ?? TimeProvider.System;

        // Created disarmed. It is armed for the next file to expire, and only while one exists.
        if (_directory is not null)
        {
            _purgeTimer = _time.CreateTimer(
                static state => ((RecentAudio)state!).Purge(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The default directory, beside the history.</summary>
    public static string DefaultDirectory => MurmurPaths.In("recent-audio");

    /// <summary>Writing, reading or deleting a file failed. Raised on the thread that tried.</summary>
    public event EventHandler<Exception>? DiskFailed;

    /// <summary>How many utterances are held in memory right now.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    private bool OnDisk => _directory is not null && _keepOnDisk();

    /// <summary>Keeps <paramref name="samples"/> under <paramref name="id"/>, pushing out the oldest.</summary>
    /// <param name="id">The dictation.</param>
    /// <param name="samples">16 kHz mono float. Not copied: the engine hands over an array it is done with.</param>
    /// <remarks>An utterance longer than <see cref="MaxTotal"/> on its own is not kept at all.</remarks>
    public void Add(Guid id, ReadOnlyMemory<float> samples)
    {
        if (samples.IsEmpty || samples.Length > MaxSamples) return;

        lock (_lock)
        {
            RemoveLocked(id);
            _entries.AddFirst((id, samples));

            var total = _entries.Sum(e => (long)e.Samples.Length);
            while (_entries.Count > Capacity || total > MaxSamples)
            {
                total -= _entries.Last!.Value.Samples.Length;
                _entries.RemoveLast();
            }
        }

        if (OnDisk) Write(id, samples);
    }

    /// <summary>The audio kept for <paramref name="id"/>.</summary>
    /// <param name="id">The dictation.</param>
    /// <param name="samples">The audio, when it is still held.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGet(Guid id, out ReadOnlyMemory<float> samples)
    {
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (entry.Id != id) continue;
                samples = entry.Samples;
                return true;
            }
        }

        if (OnDisk && Read(id) is { } read)
        {
            samples = read;
            return true;
        }

        samples = default;
        return false;
    }

    /// <summary>Whether audio is held for <paramref name="id"/>.</summary>
    /// <param name="id">The dictation.</param>
    /// <returns>True while it can still be retried.</returns>
    public bool Contains(Guid id)
    {
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (entry.Id == id) return true;
            }
        }

        // Without reading the file: a row being drawn only needs to know it is there.
        return OnDisk && IsFresh(PathFor(id));
    }

    /// <summary>Files the audio held for <paramref name="from"/> under <paramref name="to"/> instead.</summary>
    /// <param name="from">The record that was replaced.</param>
    /// <param name="to">The record that replaced it, so it can be retried back.</param>
    public void Rename(Guid from, Guid to)
    {
        lock (_lock)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Id != from) continue;
                node.Value = (to, node.Value.Samples);
                break;
            }
        }

        // The file keeps its time: a retry does not buy the audio another day.
        if (_directory is not null) OnDiskIo(() =>
        {
            var source = PathFor(from);
            if (File.Exists(source)) File.Move(source, PathFor(to), overwrite: true);
        });
    }

    /// <summary>Forgets one utterance, in memory and on disk.</summary>
    /// <param name="id">The dictation.</param>
    public void Remove(Guid id)
    {
        lock (_lock) RemoveLocked(id);
        if (_directory is not null) OnDiskIo(() =>
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        });
    }

    /// <summary>Forgets everything, in memory and on disk.</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
        ForgetOnDisk();
    }

    /// <summary>Deletes every file, leaving memory alone. For the setting being switched off.</summary>
    public void ForgetOnDisk()
    {
        if (_directory is null) return;

        // Half-written files too: a write that failed, or a crash mid-write, leaves one behind.
        // One at a time, so a file something else has open does not stop the rest being deleted.
        foreach (var file in AudioFiles(includeTemporary: true)) OnDiskIo(() => File.Delete(file));

        _purgeTimer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Deletes every file older than <see cref="DiskLifetime"/> — all of them when the setting is
    /// off — and arms the timer for the next one to expire.
    /// </summary>
    /// <remarks>Called at start-up, and by the timer.</remarks>
    public void Purge()
    {
        if (_directory is null) return;
        if (!_keepOnDisk())
        {
            ForgetOnDisk();
            return;
        }

        DateTimeOffset? next = null;
        var now = _time.GetUtcNow();
        foreach (var file in AudioFiles(includeTemporary: true))
        {
            OnDiskIo(() =>
            {
                // A temporary file is a write that never finished: a failed one, or a crash. It is
                // given a minute in case it is being written right now, then deleted.
                var lifetime = file.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase)
                    ? TemporaryGrace
                    : DiskLifetime;
                var expires = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) + lifetime;
                if (expires <= now) File.Delete(file);
                else if (next is null || expires < next) next = expires;
            });
        }

        // Nothing left: disarmed. (Infinite is -1 ms, so it must not go through the clamp below.)
        var due = next is { } at ? Max(at - _time.GetUtcNow(), TimeSpan.Zero) : Timeout.InfiniteTimeSpan;
        _purgeTimer!.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void Dispose() => _purgeTimer?.Dispose();

    private void Write(Guid id, ReadOnlyMemory<float> samples)
    {
        var written = OnDiskIo(() =>
        {
            Directory.CreateDirectory(_directory!);

            var path = PathFor(id);
            var temporary = path + TemporarySuffix;
            try
            {
                using (var stream = File.Create(temporary))
                {
                    stream.Write(MemoryMarshal.AsBytes(samples.Span));
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                // A write or a rename that failed must not leave the user's voice behind in a
                // file nothing else would ever delete. After a successful move it is gone already.
                DeleteQuietly(temporary);
            }

            // Stamped with the provider's clock, so the age the purge reads is the age it measures.
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
        });

        // The newest file expires last, so the timer only needs arming when nothing armed it.
        if (written) Purge();
    }

    private float[]? Read(Guid id)
    {
        var path = PathFor(id);
        if (!IsFresh(path)) return null;

        float[]? samples = null;
        OnDiskIo(() =>
        {
            var bytes = File.ReadAllBytes(path);
            samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, bytes.Length - (bytes.Length % sizeof(float)))).ToArray();
        });

        return samples is { Length: > 0 } ? samples : null;
    }

    private bool IsFresh(string path)
    {
        if (!File.Exists(path)) return false;
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return _time.GetUtcNow() - written < DiskLifetime;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>Deletes <paramref name="path"/> if it is there, leaving a failure to the caller's own report.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked or unreachable: the next purge removes it once its grace is over.
        }
    }

    /// <summary>Every audio file in the directory, listed up front so deleting does not disturb the listing.</summary>
    private List<string> AudioFiles(bool includeTemporary)
    {
        var files = new List<string>();
        OnDiskIo(() =>
        {
            if (!Directory.Exists(_directory)) return;
            files.AddRange(Directory.EnumerateFiles(_directory!, "*" + Extension));
            if (includeTemporary) files.AddRange(Directory.EnumerateFiles(_directory!, "*" + Extension + TemporarySuffix));
        });
        return files;
    }

    private string PathFor(Guid id) => Path.Combine(_directory!, id.ToString("N") + Extension);

    /// <summary>Runs one piece of file work, reporting rather than throwing what the disk refuses.</summary>
    /// <returns>Whether it succeeded.</returns>
    private bool OnDiskIo(Action work)
    {
        try
        {
            work();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiskFailed?.Invoke(this, ex);
            return false;
        }
    }

    private void RemoveLocked(Guid id)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.Id != id) continue;
            _entries.Remove(node);
            return;
        }
    }
}
