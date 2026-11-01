using System.Buffers;

namespace Murmur.Core;

/// <summary>
/// Accumulates one utterance's samples in pooled memory.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a <c>List&lt;float&gt;</c> that was copied once more on key release
/// (<c>ToArray</c>) and once more again inside the recogniser. A minute of dictation is a
/// million floats, so each of those copies was a 4 MB allocation straight onto the large
/// object heap — garbage the collector only reclaims in a full collection.
/// </para>
/// <para>
/// Growth rents a larger array from <see cref="ArrayPool{T}.Shared"/> and returns the old one,
/// so a steady stream of dictations reuses the same few buffers. <see cref="ToArray"/> makes the
/// one exact-length copy the recogniser genuinely needs (sherpa-onnx accepts only a whole
/// <c>float[]</c>); the pooled storage goes back on <see cref="Dispose"/>.
/// </para>
/// <para>Not thread-safe. The engine appends from its single capture loop.</para>
/// </remarks>
public sealed class SampleBuffer : IDisposable
{
    /// <summary>Starting capacity: four seconds at 16 kHz, which covers most dictations.</summary>
    private const int InitialCapacity = 4 * 16000;

    private float[] _items;
    private int _count;
    private bool _disposed;

    /// <summary>Creates an empty buffer.</summary>
    public SampleBuffer() => _items = ArrayPool<float>.Shared.Rent(InitialCapacity);

    /// <summary>How many samples have been appended.</summary>
    public int Count => _count;

    /// <summary>The samples so far. Valid until the next append or dispose.</summary>
    public ReadOnlyMemory<float> Memory => new(_items, 0, _count);

    /// <summary>Appends <paramref name="samples"/>, copying them.</summary>
    /// <param name="samples">Copied, not referenced: capture may reuse its buffer.</param>
    public void Append(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.IsEmpty) return;

        var needed = _count + samples.Length;
        if (needed > _items.Length) Grow(needed);

        samples.CopyTo(_items.AsSpan(_count));
        _count = needed;
    }

    /// <summary>An exact-length copy of the samples.</summary>
    /// <returns>A fresh array the caller owns; empty when nothing was appended.</returns>
    public float[] ToArray() => _count == 0 ? [] : _items.AsSpan(0, _count).ToArray();

    private void Grow(int needed)
    {
        var next = ArrayPool<float>.Shared.Rent(Math.Max(needed, _items.Length * 2));
        _items.AsSpan(0, _count).CopyTo(next);

        ArrayPool<float>.Shared.Return(_items);
        _items = next;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ArrayPool<float>.Shared.Return(_items);
        _items = [];
        _count = 0;
    }
}
