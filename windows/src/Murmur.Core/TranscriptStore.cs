using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Abstractions;
using Murmur.Dictionary;

namespace Murmur.Core;

/// <summary>One saved dictation.</summary>
public sealed record TranscriptRecord
{
    /// <summary>Stable identity, so one entry can be deleted without matching on its text.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>When the key was released.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>How long the key was held.</summary>
    public double AudioSeconds { get; init; }

    /// <summary>Release to finished text — the wait actually felt.</summary>
    public double ProcessingSeconds { get; init; }

    /// <summary>The final text, after corrections.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Corrections that fired, if any.</summary>
    public IReadOnlyList<AppliedCorrection>? Corrections { get; init; }

    // The fields below arrived later. Each is left out of the line when it holds its default, so
    // a record that says nothing new reads exactly as it was written, and a history written
    // before they existed loads with "unknown engine, did not fall back, not a retry".

    /// <summary>The engine that heard it; <see cref="TranscriberKind.Unknown"/> in older history.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TranscriberKind Engine { get; init; }

    /// <summary>Whether the fallback engine heard it rather than the one chosen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FellBack { get; init; }

    /// <summary>The cloud profile in use when Azure heard it; null for Parakeet and older history.</summary>
    /// <remarks>What Home's cost estimate prices the minutes at.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CloudProfile? Profile { get; init; }

    /// <summary>The record this one replaced by retrying with the other engine, or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RetriedFrom { get; init; }
}

/// <summary>
/// Transcript history, appended to a JSONL file.
/// </summary>
/// <remarks>
/// <para>
/// One JSON object per line rather than one big array: appending a line is cheap and a
/// truncated write costs one record rather than the whole file. Deleting requires a rewrite,
/// which is fine — it is rare and the file is small.
/// </para>
/// <para>
/// Serialization is source-generated (<see cref="TranscriptJsonContext"/>) so this survives
/// trimming and single-file publishing, where reflection-based JSON quietly stops working.
/// </para>
/// </remarks>
public sealed class TranscriptStore
{
    private readonly string _path;
    private readonly List<TranscriptRecord> _records = [];

    /// <summary>Opens (and creates if needed) the history at <paramref name="path"/>.</summary>
    public TranscriptStore(string path)
    {
        _path = path;
        Reload();
    }

    /// <summary>The default location.</summary>
    public static string DefaultPath => MurmurPaths.In("transcripts.jsonl");

    /// <summary>Every record, newest first.</summary>
    public IReadOnlyList<TranscriptRecord> Records => _records;

    /// <summary>The newest record, or null when there are none.</summary>
    public TranscriptRecord? Latest => _records.Count > 0 ? _records[0] : null;

    /// <summary>Raised whenever the history changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads the file.</summary>
    public void Reload()
    {
        _records.Clear();

        if (File.Exists(_path))
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // A single corrupt line must not destroy the whole history — skip it and
                // keep everything else.
                try
                {
                    var record = JsonSerializer.Deserialize(line, TranscriptJsonContext.Default.TranscriptRecord);
                    if (record is not null) _records.Add(record);
                }
                catch (JsonException)
                {
                    // Skip.
                }
            }
        }

        _records.Reverse();   // newest first, which is how the list reads
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Appends a record.</summary>
    public void Add(TranscriptRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var line = JsonSerializer.Serialize(record, TranscriptJsonContext.Default.TranscriptRecord);
        File.AppendAllText(_path, line + Environment.NewLine);

        _records.Insert(0, record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts <paramref name="replacement"/> where the record <paramref name="id"/> was.</summary>
    /// <param name="id">The record being replaced.</param>
    /// <param name="replacement">What takes its place, keeping its position in the list.</param>
    /// <returns>False when there is no such record; nothing is changed then.</returns>
    /// <remarks>
    /// For a retry: the new transcript stands where the old one did, rather than jumping to the top
    /// as a dictation the user never made, and carries the old one's id in
    /// <see cref="TranscriptRecord.RetriedFrom"/>.
    /// </remarks>
    public bool Replace(Guid id, TranscriptRecord replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        var index = _records.FindIndex(r => r.Id == id);
        if (index < 0) return false;

        _records[index] = replacement;
        Rewrite();
        return true;
    }

    /// <summary>Deletes one record.</summary>
    public void Remove(Guid id)
    {
        _records.RemoveAll(r => r.Id == id);
        Rewrite();
    }

    /// <summary>Deletes everything.</summary>
    public void Clear()
    {
        _records.Clear();
        Rewrite();
    }

    /// <summary>Case-insensitive search over transcript text.</summary>
    public IReadOnlyList<TranscriptRecord> Search(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return _records;

        return _records
            .Where(r => r.Text.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void Rewrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Oldest first on disk, so a plain append stays correct next time.
        var lines = _records
            .AsEnumerable()
            .Reverse()
            .Select(r => JsonSerializer.Serialize(r, TranscriptJsonContext.Default.TranscriptRecord));

        File.WriteAllLines(_path, lines);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Source-generated JSON for the transcript store.
/// </summary>
/// <remarks>
/// Reflection-based serialization breaks under trimming and is flagged by the single-file
/// analyzer. Generating it keeps the published binary honest.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TranscriptRecord))]
[JsonSerializable(typeof(AppliedCorrection))]
public sealed partial class TranscriptJsonContext : JsonSerializerContext;
