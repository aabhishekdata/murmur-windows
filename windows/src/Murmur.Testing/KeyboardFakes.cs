using Murmur.Abstractions;

namespace Murmur.Testing;

/// <summary>
/// Records every keystroke and clipboard call, in one shared log, so ordering can be asserted.
/// </summary>
/// <remarks>
/// Entries look like <c>release:A2</c>, <c>press:A2</c>, <c>paste</c>, <c>type:hello</c>,
/// <c>enter</c>, <c>get</c> and <c>set:text</c> (or <c>set:null</c>).
/// </remarks>
public sealed class KeyboardLog
{
    /// <summary>Everything, in order.</summary>
    public List<string> Entries { get; } = [];

    /// <summary>Appends an entry.</summary>
    /// <param name="entry">What happened.</param>
    public void Add(string entry)
    {
        lock (Entries) Entries.Add(entry);
    }
}

/// <summary>A keystroke sender that records instead of typing.</summary>
public sealed class FakeKeystrokeSender(KeyboardLog? log = null) : IKeystrokeSender
{
    private readonly Queue<IReadOnlyList<int>> _held = new();

    /// <summary>The shared log.</summary>
    public KeyboardLog Log { get; } = log ?? new KeyboardLog();

    /// <summary>Modifiers reported once the scripted readings run out.</summary>
    public IReadOnlyList<int> HeldAfterScript { get; set; } = [];

    /// <summary>How many times the modifiers were read.</summary>
    public int HeldReads { get; private set; }

    /// <summary>What <see cref="TypeUnicode"/> answers.</summary>
    public bool TypeSucceeds { get; set; } = true;

    /// <summary>Each batch typed, in order.</summary>
    public List<string> Typed { get; } = [];

    /// <summary>Scripts the next reading of the held modifiers.</summary>
    /// <param name="keys">Virtual-key codes reported as down.</param>
    /// <returns>This, for chaining.</returns>
    public FakeKeystrokeSender ThenHeld(params int[] keys)
    {
        _held.Enqueue(keys);
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyList<int> HeldModifiers()
    {
        HeldReads++;
        return _held.Count > 0 ? _held.Dequeue() : HeldAfterScript;
    }

    /// <inheritdoc />
    public bool Release(IReadOnlyList<int> virtualKeys)
    {
        foreach (var key in virtualKeys) Log.Add($"release:{key:X2}");
        return true;
    }

    /// <inheritdoc />
    public bool Press(IReadOnlyList<int> virtualKeys)
    {
        foreach (var key in virtualKeys) Log.Add($"press:{key:X2}");
        return true;
    }

    /// <inheritdoc />
    public bool SendPasteChord()
    {
        Log.Add("paste");
        return true;
    }

    /// <inheritdoc />
    public bool TypeUnicode(string text)
    {
        Typed.Add(text);
        Log.Add("type:" + text);
        return TypeSucceeds;
    }

    /// <inheritdoc />
    public bool Enter()
    {
        Log.Add("enter");
        return true;
    }
}

/// <summary>A clipboard held in a field.</summary>
public sealed class FakeClipboard(KeyboardLog? log = null) : IClipboardService
{
    /// <summary>The shared log.</summary>
    public KeyboardLog Log { get; } = log ?? new KeyboardLog();

    /// <summary>The clipboard's text.</summary>
    public string? Text { get; set; }

    /// <summary>When true, every write fails.</summary>
    public bool RefuseWrites { get; set; }

    /// <summary>When true, every write throws.</summary>
    public bool ThrowOnWrite { get; set; }

    /// <summary>
    /// Called before each read with the number of reads so far; lets a test change the
    /// clipboard behind the injector's back.
    /// </summary>
    public Action<FakeClipboard, int>? BeforeRead { get; set; }

    private int _reads;

    /// <inheritdoc />
    public Task<string?> GetTextAsync(CancellationToken cancellationToken)
    {
        BeforeRead?.Invoke(this, _reads++);
        Log.Add("get");
        return Task.FromResult(Text);
    }

    /// <inheritdoc />
    public Task<bool> SetTextAsync(string? text, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite) throw new InvalidOperationException("The clipboard is held by another process.");

        Log.Add("set:" + (text ?? "null"));
        if (RefuseWrites) return Task.FromResult(false);

        Text = text;
        return Task.FromResult(true);
    }
}

/// <summary>Global hotkeys kept in a dictionary, with combinations you can mark as taken.</summary>
public sealed class FakeGlobalHotkeys : IGlobalHotkeys
{
    /// <summary>What is registered now, by id.</summary>
    public Dictionary<int, HotkeyChord> Registered { get; } = [];

    /// <summary>Combinations "another program" holds; registering one fails.</summary>
    public HashSet<HotkeyChord> Taken { get; } = [];

    /// <summary>How many registrations were attempted.</summary>
    public int RegisterCalls { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> was called.</summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public event EventHandler<int>? Pressed;

    /// <inheritdoc />
    public bool Register(int id, HotkeyChord chord)
    {
        RegisterCalls++;
        if (Taken.Contains(chord) || Registered.Any(r => r.Key != id && r.Value == chord)) return false;

        Registered[id] = chord;
        return true;
    }

    /// <inheritdoc />
    public void Unregister(int id) => Registered.Remove(id);

    /// <summary>Presses a combination, as the user would.</summary>
    /// <param name="chord">The combination.</param>
    /// <returns>False when nothing has it registered, so nothing fired.</returns>
    public bool Press(HotkeyChord chord)
    {
        foreach (var (id, registered) in Registered)
        {
            if (registered != chord) continue;

            Pressed?.Invoke(this, id);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IsDisposed = true;
        Registered.Clear();
    }
}
