using System.Diagnostics.CodeAnalysis;
using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>Timings and thresholds for <see cref="SmartTextInjector"/>.</summary>
/// <remarks>Every delay is a property so tests can set them to zero.</remarks>
public sealed record SmartTextInjectorOptions
{
    /// <summary>Above this many characters, paste instead of typing.</summary>
    /// <remarks>
    /// Some terminals and Electron apps drop characters when thousands of synthetic keystrokes
    /// arrive back to back; one paste cannot be half-delivered.
    /// </remarks>
    public int PasteThreshold { get; init; } = 200;

    /// <summary>UTF-16 code units per typed batch.</summary>
    public int ChunkSize { get; init; } = 40;

    /// <summary>Pause between typed batches, so slower targets keep up.</summary>
    public TimeSpan ChunkGap { get; init; } = TimeSpan.FromMilliseconds(4);

    /// <summary>How long to wait for the user to let go of modifier keys before lifting them.</summary>
    /// <remarks>
    /// A prompt fired by Ctrl+Alt+1 arrives while Ctrl and Alt are still down, and a paste or
    /// typed text under them is a shortcut, not text. Most people have let go well inside this.
    /// </remarks>
    public TimeSpan ModifierReleaseWait { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How often to look while waiting for modifiers.</summary>
    public TimeSpan ModifierPollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Time for the target to see the new clipboard before Ctrl+V arrives. Without it, a fast
    /// paste can pick up the <i>previous</i> contents.
    /// </summary>
    public TimeSpan ClipboardSettle { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>Time for the target to finish reading the clipboard before it is put back.</summary>
    public TimeSpan RestoreDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The line break pasted text uses: Windows' clipboard convention.</summary>
    /// <remarks>
    /// Chromium and Electron accept a bare line feed, but a classic multi-line edit control shows
    /// none at all.
    /// </remarks>
    public string PasteNewline { get; init; } = "\r\n";
}

/// <summary>
/// Decides how text reaches the focused app: typed, or pasted through the clipboard.
/// </summary>
/// <remarks>
/// <para>
/// UI Automation cannot insert text on Windows, so text is synthesised — and <i>how</i> is the
/// logic that used to sit, untestable, in the platform layer. It now lives here, over two thin
/// bindings: <see cref="IKeystrokeSender"/> (one <c>SendInput</c> per member) and
/// <see cref="IClipboardService"/> (the UI framework's clipboard).
/// </para>
/// <para><b>The rules:</b></para>
/// <list type="number">
/// <item><b>Modifiers first.</b> Wait up to <see cref="SmartTextInjectorOptions.ModifierReleaseWait"/>
/// for Shift, Ctrl, Alt and Windows to come up; lift any still down, and press them again
/// afterwards so the user's hand and the system agree. SendInput does not reset keyboard
/// state, so a resting Shift would otherwise turn Ctrl+V into Ctrl+Shift+V.</item>
/// <item><b>Short single-line text is typed</b> as Unicode packets, leaving the clipboard alone.</item>
/// <item><b>Multi-line, long or verbatim text is pasted</b>: the clipboard is set, Ctrl+V sent,
/// and — after <see cref="SmartTextInjectorOptions.RestoreDelay"/>, and only if the clipboard
/// still holds what was pasted — the previous text is put back. The restore runs even when the
/// injection is cancelled half-way. If the clipboard held no text (an image, files, or a read
/// that failed), the pasted text is left there: emptying it would destroy what it did hold.</item>
/// <item><b>If the clipboard cannot be written</b>, the text is typed instead, with a real Enter
/// for each line break. Typed Unicode line feeds do not reliably make a new line.</item>
/// </list>
/// <para>
/// Injections are one at a time. Dictation and a prompt's shortcut share this instance, and two
/// pastes overlapping on one clipboard would paste each other's text and lose the user's.
/// </para>
/// <para>
/// One known limit: a modifier lifted here and pressed again is pressed again even if the user
/// let go of it in the few milliseconds between. Their next press and release of it clears it.
/// Waiting first is what keeps that rare.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gate's SemaphoreSlim holds no handle unless AvailableWaitHandle is read, which it never is; "
                  + "disposing it under an injection still in flight would only turn its release into a crash.")]
public sealed class SmartTextInjector : ITextInjector
{
    private readonly IKeystrokeSender _keys;
    private readonly IClipboardService? _clipboard;
    private readonly SmartTextInjectorOptions _options;

    /// <summary>Held from the clipboard read to the restore, so injections never overlap.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the injector.</summary>
    /// <param name="keys">Raw keystrokes.</param>
    /// <param name="clipboard">The clipboard, or null to always type.</param>
    /// <param name="options">Timings; defaults to <see cref="SmartTextInjectorOptions"/>' own.</param>
    public SmartTextInjector(
        IKeystrokeSender keys,
        IClipboardService? clipboard,
        SmartTextInjectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(keys);

        _keys = keys;
        _clipboard = clipboard;
        _options = options ?? new SmartTextInjectorOptions();
    }

    /// <inheritdoc />
    public ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken) =>
        InjectAsync(text, verbatim: false, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<bool> InjectAsync(string text, bool verbatim, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return true;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clipboard is null || !WantsPaste(text, verbatim))
            {
                return await DeliverAsync(text, paste: false, cancellationToken).ConfigureAwait(false);
            }

            var clipboardText = WithPasteNewlines(text);
            var previous = await TryGetAsync(cancellationToken).ConfigureAwait(false);

            if (!await TrySetAsync(clipboardText, cancellationToken).ConfigureAwait(false))
            {
                return await DeliverAsync(text, paste: false, cancellationToken).ConfigureAwait(false);
            }

            // From here the clipboard holds our text, so it is put back however this ends,
            // a cancellation included.
            try
            {
                await Task.Delay(_options.ClipboardSettle, cancellationToken).ConfigureAwait(false);
                return await DeliverAsync(text, paste: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (previous is not null) await RestoreAsync(previous, clipboardText, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unchanged from before this class existed: any modifier the user is resting on is lifted
    /// for the keystroke and pressed again after it, with no wait. Shift+Enter is "new line
    /// without sending" in most chat apps, the opposite of what "press enter" asked for.
    /// </remarks>
    public async ValueTask<bool> PressEnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            var held = _keys.HeldModifiers();
            if (held.Count > 0) _keys.Release(held);

            try
            {
                return _keys.Enter();
            }
            finally
            {
                if (held.Count > 0) _keys.Press(held);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends the paste chord or types the text, with the user's modifiers out of the way.</summary>
    private async Task<bool> DeliverAsync(string text, bool paste, CancellationToken cancellationToken)
    {
        var held = await WaitForModifiersAsync(cancellationToken).ConfigureAwait(false);
        if (held.Count > 0) _keys.Release(held);

        try
        {
            return paste
                ? _keys.SendPasteChord()
                : await TypeAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (held.Count > 0) _keys.Press(held);
        }
    }

    private bool WantsPaste(string text, bool verbatim) =>
        verbatim || text.Length > _options.PasteThreshold || text.Contains('\n', StringComparison.Ordinal);

    private string WithPasteNewlines(string text) =>
        text.ReplaceLineEndings(_options.PasteNewline);

    /// <summary>Waits for the modifiers to come up, and returns any that did not.</summary>
    private async Task<IReadOnlyList<int>> WaitForModifiersAsync(CancellationToken cancellationToken)
    {
        var poll = _options.ModifierPollInterval;
        var polls = poll > TimeSpan.Zero ? (int)Math.Ceiling(_options.ModifierReleaseWait / poll) : 0;

        for (var i = 0; ; i++)
        {
            var held = _keys.HeldModifiers();
            if (held.Count == 0 || i >= polls) return held;

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Types text in batches, with a real Enter for each line break.</summary>
    private async Task<bool> TypeAsync(string text, CancellationToken cancellationToken)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        for (var line = 0; line < lines.Length; line++)
        {
            if (line > 0 && !_keys.Enter()) return false;

            var current = lines[line];
            for (var offset = 0; offset < current.Length;)
            {
                var length = Math.Min(Math.Max(1, _options.ChunkSize), current.Length - offset);

                // Never split a surrogate pair across two batches.
                if (offset + length < current.Length && char.IsHighSurrogate(current[offset + length - 1])) length++;

                if (offset > 0) await Task.Delay(_options.ChunkGap, cancellationToken).ConfigureAwait(false);
                if (!_keys.TypeUnicode(current.Substring(offset, length))) return false;

                offset += length;
            }
        }

        return true;
    }

    /// <summary>Puts the previous clipboard text back, unless something else has replaced ours.</summary>
    /// <remarks>
    /// Runs to the end even when the injection is being cancelled: leaving the user's clipboard
    /// holding a prompt they never copied is the one outcome worth avoiding here. A cancellation
    /// only skips the wait, which costs nothing when Ctrl+V was never sent.
    /// </remarks>
    private async Task RestoreAsync(string previous, string pasted, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.RestoreDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Restore now rather than not at all.
        }

        var current = await TryGetAsync(CancellationToken.None).ConfigureAwait(false);
        if (!string.Equals(current, pasted, StringComparison.Ordinal)) return;

        await TrySetAsync(previous, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<string?> TryGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _clipboard!.GetTextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Another process holding the clipboard open is routine on Windows. Not knowing the
            // previous text only means there is nothing to put back.
            return null;
        }
    }

    private async Task<bool> TrySetAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            return await _clipboard!.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
