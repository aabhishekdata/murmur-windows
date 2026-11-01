using System.Buffers;
using System.Buffers.Binary;

namespace Murmur.Cloud;

/// <summary>Writes 16 kHz mono float audio as a 16-bit PCM WAV file.</summary>
/// <remarks>
/// <para>
/// WAV rather than a compressed format because it needs no codec and no native library: the
/// whole encoder is the 44-byte header below and a loop. At 32 KB per second of audio a
/// 60-second segment is about 1.9 MB, far inside the service's 25 MB limit.
/// </para>
/// <para>
/// Samples are clamped to ±1 before conversion. A capture driver that overshoots by a hair
/// would otherwise wrap round to the opposite extreme — a click in the audio, and a word the
/// model hears wrongly.
/// </para>
/// </remarks>
public static class WavEncoder
{
    /// <summary>The sample rate written into the header.</summary>
    public const int SampleRate = 16000;

    /// <summary>The size of the RIFF/WAVE header that precedes the samples.</summary>
    public const int HeaderSize = 44;

    private const short Channels = 1;
    private const short BitsPerSample = 16;
    private const short BlockAlign = Channels * BitsPerSample / 8;

    /// <summary>How many bytes <paramref name="sampleCount"/> samples encode to, header included.</summary>
    /// <param name="sampleCount">Number of samples.</param>
    /// <returns>The file size in bytes.</returns>
    public static long ByteCount(int sampleCount) => HeaderSize + (long)sampleCount * BlockAlign;

    /// <summary>Encodes <paramref name="samples"/> into <paramref name="destination"/>.</summary>
    /// <param name="samples">16 kHz mono float, nominally in [-1, 1].</param>
    /// <param name="destination">At least <see cref="ByteCount"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Encode(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        var total = checked((int)ByteCount(samples.Length));
        if (destination.Length < total)
        {
            throw new ArgumentException("The destination is too small for the encoded audio.", nameof(destination));
        }

        var dataSize = total - HeaderSize;

        "RIFF"u8.CopyTo(destination);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], total - 8);
        "WAVE"u8.CopyTo(destination[8..]);

        "fmt "u8.CopyTo(destination[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(destination[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(destination[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..], SampleRate * BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(destination[32..], BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(destination[34..], BitsPerSample);

        "data"u8.CopyTo(destination[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[40..], dataSize);

        var body = destination[HeaderSize..];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)MathF.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(body[(i * BlockAlign)..], value);
        }

        return total;
    }

    /// <summary>Encodes into a buffer borrowed from the shared pool.</summary>
    /// <param name="samples">16 kHz mono float.</param>
    /// <returns>The encoded file; dispose it to return the buffer.</returns>
    /// <remarks>
    /// Pooled because a dictation can be a minute long and is encoded once per attempt's worth
    /// of request; a fresh 2 MB array each time is garbage the process did not need to make.
    /// </remarks>
    public static PooledWav EncodePooled(ReadOnlySpan<float> samples)
    {
        var size = checked((int)ByteCount(samples.Length));
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        var written = Encode(samples, buffer);
        return new PooledWav(buffer, written);
    }
}

/// <summary>An encoded WAV file in a pooled buffer.</summary>
/// <remarks>Dispose exactly once, after the last request that reads it has completed.</remarks>
public sealed class PooledWav : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;

    internal PooledWav(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    /// <summary>The file's bytes.</summary>
    public ReadOnlyMemory<byte> Memory =>
        _buffer is { } buffer ? buffer.AsMemory(0, _length) : throw new ObjectDisposedException(nameof(PooledWav));

    /// <summary>The file's size in bytes.</summary>
    public int Length => _length;

    /// <inheritdoc />
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}
