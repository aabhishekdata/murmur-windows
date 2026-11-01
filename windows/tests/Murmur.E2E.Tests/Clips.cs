using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Murmur.Abstractions;

namespace Murmur.E2E.Tests;

/// <summary>One spoken clip and what was said in it.</summary>
/// <param name="Name">The file name without <c>.wav</c>, such as <c>hello</c>.</param>
/// <param name="Spoken">The text that was spoken, from <c>expected.tsv</c>.</param>
/// <param name="Samples">16 kHz mono float.</param>
internal sealed record Clip(string Name, string Spoken, float[] Samples)
{
    /// <summary>How long the clip is.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / AudioChunk.SampleRate);

    /// <summary>Every clip name in <c>expected.tsv</c>, for a theory's data.</summary>
    public static IEnumerable<string> Names => ["hello", "sentence", "trigger", "trigger-enter", "filler", "long"];

    private static readonly Dictionary<string, Clip> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>Loads a clip from <see cref="E2E.AudioDirectory"/>.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The clip.</returns>
    public static Clip Load(string name)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;

            var spoken = File.ReadAllLines(Path.Combine(E2E.AudioDirectory, "expected.tsv"), Encoding.UTF8)
                .Select(line => line.Split('\t', 2))
                .Where(parts => parts.Length == 2 && parts[0] == name)
                .Select(parts => parts[1].Trim())
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"{name} is not listed in expected.tsv.");

            var clip = new Clip(name, spoken, Wav.Read(File.ReadAllBytes(Path.Combine(E2E.AudioDirectory, name + ".wav"))));
            Cache[name] = clip;
            return clip;
        }
    }
}

/// <summary>Reads PCM16 16 kHz mono WAV, the one format both the clips and the cloud request use.</summary>
internal static class Wav
{
    /// <summary>The format a WAV file declared.</summary>
    /// <param name="Format">1 for PCM.</param>
    /// <param name="Channels">Channel count.</param>
    /// <param name="SampleRate">Samples per second.</param>
    /// <param name="BitsPerSample">Bits per sample.</param>
    /// <param name="DataBytes">The length of the <c>data</c> chunk.</param>
    public readonly record struct Header(int Format, int Channels, int SampleRate, int BitsPerSample, int DataBytes)
    {
        /// <summary>Whether this is what Murmur sends and the clips are recorded in.</summary>
        public bool IsPcm16Mono16k => Format == 1 && Channels == 1 && SampleRate == 16000 && BitsPerSample == 16;

        /// <inheritdoc />
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"format {Format}, {Channels} ch, {SampleRate} Hz, {BitsPerSample} bit, {DataBytes} bytes");
    }

    /// <summary>Walks the chunks of a RIFF/WAVE file.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="data">The <c>data</c> chunk's bytes.</param>
    /// <returns>The declared format.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a WAV file with a format and data chunk.</exception>
    public static Header Parse(ReadOnlySpan<byte> bytes, out ReadOnlySpan<byte> data)
    {
        if (bytes.Length < 12
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        Header? format = null;
        data = default;
        var foundData = false;
        var offset = 12;

        // The fmt chunk is 16 bytes from Murmur's encoder and 18 from the clips' recorder, so
        // walk the chunks rather than assume a 44-byte header.
        while (offset + 8 <= bytes.Length)
        {
            var id = bytes.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var body = offset + 8;
            if (size < 0 || body + size > bytes.Length) size = bytes.Length - body;

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                var chunk = bytes.Slice(body, size);
                format = new Header(
                    BinaryPrimitives.ReadInt16LittleEndian(chunk),
                    BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]),
                    BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]),
                    BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]),
                    0);
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = bytes.Slice(body, size);
                foundData = true;
            }

            offset = body + size + (size & 1);
        }

        if (format is not { } header || !foundData) throw new InvalidDataException("No fmt or data chunk.");
        return header with { DataBytes = data.Length };
    }

    /// <summary>Decodes PCM16 16 kHz mono into floats in [-1, 1].</summary>
    /// <param name="bytes">The whole file.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="InvalidDataException">Any other format.</exception>
    public static float[] Read(ReadOnlySpan<byte> bytes)
    {
        var header = Parse(bytes, out var data);
        if (!header.IsPcm16Mono16k) throw new InvalidDataException($"Expected PCM16 16 kHz mono, found {header}.");

        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2)) / 32768f;
        }

        return samples;
    }
}

/// <summary>A microphone that plays a WAV file, in 50 ms chunks, then stays open and silent.</summary>
/// <remarks>
/// <para>
/// Like a real microphone it keeps going until recording is cancelled: the key is released after
/// <see cref="Drained"/>, exactly as a person lets go after they stop speaking. Chunks arrive in
/// real time, one every 50 ms, so whatever the app does while the key is held — loading Parakeet
/// on key-down, above all — has the clip's length to do it in, as it would with a person talking.
/// </para>
/// <para>
/// One capture for the life of a <see cref="Pipeline"/>, as in the app; <see cref="Load"/> sets
/// what the next recording plays.
/// </para>
/// </remarks>
internal sealed class WavFileAudioCapture : IAudioCapture
{
    /// <summary>50 ms at 16 kHz.</summary>
    public const int ChunkSamples = AudioChunk.SampleRate / 20;

    private float[] _next = [];
    private TaskCompletionSource _drained = NewSignal();

    /// <summary>Completes once every sample of the loaded clip has been taken by the engine.</summary>
    public Task Drained => _drained.Task;

    /// <inheritdoc />
    public bool IsCapturing { get; private set; }

    /// <summary>Sets what the next recording plays.</summary>
    /// <param name="samples">16 kHz mono float.</param>
    public void Load(float[] samples)
    {
        _next = samples;
        _drained = NewSignal();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioChunk> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var samples = _next;
        var drained = _drained;
        IsCapturing = true;
        using var clock = new PeriodicTimer(TimeSpan.FromSeconds((double)ChunkSamples / AudioChunk.SampleRate));
        try
        {
            for (var offset = 0; offset < samples.Length; offset += ChunkSamples)
            {
                await clock.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);

                // A fresh array per chunk, as a real capture must hand over.
                var chunk = samples.AsSpan(offset, Math.Min(ChunkSamples, samples.Length - offset)).ToArray();
                yield return new AudioChunk(chunk);
            }

            // Reached only once the engine has asked for the chunk after the last, so it has
            // appended them all.
            drained.TrySetResult();

            // The microphone stays open, hearing nothing, until the key comes up.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
