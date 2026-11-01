using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>Where a prompt's key combination stands.</summary>
public enum SnippetHotkeyStatus
{
    /// <summary>The prompt has no combination, or is disabled.</summary>
    None,

    /// <summary>Registered: pressing it pastes the prompt.</summary>
    Active,

    /// <summary>Not a combination that can be used — a bare letter, say.</summary>
    Invalid,

    /// <summary>An earlier prompt in the list already has it.</summary>
    Duplicate,

    /// <summary>Another program, or Windows itself, already holds it.</summary>
    InUse,

    /// <summary>Global combinations are not available on this machine.</summary>
    Unsupported,

    /// <summary>Murmur's own paste-last shortcut already has it.</summary>
    PasteLast,
}

/// <summary>Something that holds global key combinations and can give them back for a while.</summary>
/// <remarks>
/// While the user records a new shortcut, every combination Murmur holds must be given back, not
/// only the recorder's own: Windows swallows a registered combination before the recorder could
/// see it, and fires whatever holds it instead.
/// </remarks>
public interface IShortcutOwner
{
    /// <summary>Gives every combination back until the returned handle is disposed.</summary>
    /// <returns>Disposing it registers them again.</returns>
    IDisposable Suspend();
}

/// <summary>Suspends several <see cref="IShortcutOwner"/>s as one.</summary>
public static class ShortcutSuspension
{
    /// <summary>Suspends every owner given, skipping nulls.</summary>
    /// <param name="owners">
    /// The owners, in the order they first registered at start-up. They are resumed in the same
    /// order, so a combination two of them want goes back to the one that had it before.
    /// </param>
    /// <returns>Disposing it resumes them all.</returns>
    public static IDisposable All(params IShortcutOwner?[] owners)
    {
        ArgumentNullException.ThrowIfNull(owners);

        var held = new List<IDisposable>(owners.Length);
        foreach (var owner in owners)
        {
            if (owner is not null) held.Add(owner.Suspend());
        }

        return new Resumer(held);
    }

    private sealed class Resumer(List<IDisposable> held) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var handle in held) handle.Dispose();
        }
    }
}

/// <summary>
/// Keeps every enabled prompt's key combination registered, and pastes the prompt when it fires.
/// </summary>
/// <remarks>
/// <para>
/// All the logic of per-prompt shortcuts, over <see cref="IGlobalHotkeys"/> — which on Windows
/// is <c>RegisterHotKey</c> and nothing more. Registration follows the store: any change
/// unregisters everything and registers the current set again, so an edited, disabled or
/// deleted prompt never leaves a stale combination behind. Prompts are few and registration is
/// cheap; diffing would be a place for bugs to live and save nothing anyone could measure.
/// </para>
/// <para>
/// When a combination fires, a recording in progress is cancelled first — audio discarded,
/// nothing typed — and then the prompt is injected verbatim. Fires are serialised, so pressing
/// two combinations in quick succession cannot interleave two pastes on one clipboard.
/// </para>
/// <para>
/// The push-to-talk key is not involved: it stays on the low-level hook, which observes without
/// swallowing. These combinations are swallowed, which is what a shortcut should do.
/// </para>
/// <para>
/// <b>Threads.</b> <see cref="IGlobalHotkeys.Register"/> and <see cref="IGlobalHotkeys.Unregister"/>
/// wait on the thread that raises <see cref="IGlobalHotkeys.Pressed"/>. So the press handler takes
/// no lock that is held across those calls: it reads an immutable snapshot of the registrations,
/// and fires are chained under a lock of their own that never waits on anything.
/// </para>
/// </remarks>
public sealed class SnippetHotkeyBinder : IShortcutOwner, IDisposable
{
    /// <summary>The id a <see cref="Check"/> probe registers under; never handed to a prompt.</summary>
    private const int ProbeId = 0xBFFF;

    private readonly SnippetStore _store;
    private readonly IGlobalHotkeys? _hotkeys;
    private readonly ITextInjector _injector;
    private readonly Func<Task>? _cancelDictation;
    private readonly TimeSpan _injectionTimeout;

    /// <summary>Serialises registration. Held across platform calls, so never taken by a press.</summary>
    private readonly Lock _bindSync = new();

    /// <summary>Guards <see cref="_pending"/> only; never held across a platform call.</summary>
    private readonly Lock _fireSync = new();

    // Replaced whole, never mutated, so the press handler can read them without a lock.
    private IReadOnlyDictionary<int, Guid> _registered = new Dictionary<int, Guid>();
    private IReadOnlyDictionary<Guid, SnippetHotkeyStatus> _statuses = new Dictionary<Guid, SnippetHotkeyStatus>();
    private Task _pending = Task.CompletedTask;
    private int _suspended;
    private bool _disposed;

    /// <summary>Starts keeping <paramref name="store"/>'s combinations registered.</summary>
    /// <param name="store">The prompts.</param>
    /// <param name="hotkeys">The registration API, or null where there is none.</param>
    /// <param name="injector">Where a fired prompt goes.</param>
    /// <param name="cancelDictation">Abandons a recording in progress; null when there is no engine.</param>
    /// <param name="injectionTimeout">How long one paste may take. Defaults to ten seconds, as dictation.</param>
    public SnippetHotkeyBinder(
        SnippetStore store,
        IGlobalHotkeys? hotkeys,
        ITextInjector injector,
        Func<Task>? cancelDictation = null,
        TimeSpan? injectionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(injector);

        _store = store;
        _hotkeys = hotkeys;
        _injector = injector;
        _cancelDictation = cancelDictation;
        _injectionTimeout = injectionTimeout ?? TimeSpan.FromSeconds(10);

        if (_hotkeys is not null) _hotkeys.Pressed += OnPressed;
        _store.Changed += OnStoreChanged;
        Rebind();
    }

    /// <summary>Raised after the statuses have been recomputed. May fire on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when pasting a fired prompt failed. Fires on a background thread.</summary>
    public event EventHandler<Exception>? FireFaulted;

    /// <summary>Whether combinations can be registered at all here.</summary>
    public bool IsSupported => _hotkeys is not null;

    /// <summary>
    /// The combination the paste-last shortcut holds, so one a prompt cannot have because of it
    /// is reported as Murmur's own rather than blamed on another app. Null where there is none.
    /// </summary>
    public Func<HotkeyChord?>? PasteLastChord { get; set; }

    private bool HeldByPasteLast(HotkeyChord chord) => PasteLastChord?.Invoke() == chord;

    /// <summary>Where <paramref name="snippetId"/>'s combination stands.</summary>
    /// <param name="snippetId">The prompt.</param>
    /// <returns>Its status, or <see cref="SnippetHotkeyStatus.None"/> for a prompt without one.</returns>
    public SnippetHotkeyStatus StatusOf(Guid snippetId) =>
        Volatile.Read(ref _statuses).TryGetValue(snippetId, out var status) ? status : SnippetHotkeyStatus.None;

    /// <summary>Whether <paramref name="chord"/> would work for <paramref name="snippetId"/>.</summary>
    /// <param name="chord">The combination being considered.</param>
    /// <param name="snippetId">The prompt it is for, whose own registration does not count against it.</param>
    /// <returns><see cref="SnippetHotkeyStatus.Active"/> when it would, otherwise why not.</returns>
    /// <remarks>
    /// For the editor, while the user is choosing. A combination this binder already holds for
    /// another prompt is a duplicate; anything else is tried for real and given straight back.
    /// </remarks>
    public SnippetHotkeyStatus Check(HotkeyChord chord, Guid snippetId)
    {
        if (HotkeyChords.Problem(chord) is not null) return SnippetHotkeyStatus.Invalid;

        var usedElsewhere = _store.Entries.Any(e =>
            e.Id != snippetId && e.IsEnabled && e.Hotkey == chord);
        if (usedElsewhere) return SnippetHotkeyStatus.Duplicate;
        if (HeldByPasteLast(chord)) return SnippetHotkeyStatus.PasteLast;

        if (_hotkeys is null) return SnippetHotkeyStatus.Unsupported;

        lock (_bindSync)
        {
            // Held by this very prompt: registering it again would fail against ourselves.
            var held = _registered.Values.Contains(snippetId)
                && _store.Entries.FirstOrDefault(e => e.Id == snippetId)?.Hotkey == chord;
            if (held) return SnippetHotkeyStatus.Active;

            if (!_hotkeys.Register(ProbeId, chord)) return SnippetHotkeyStatus.InUse;
            _hotkeys.Unregister(ProbeId);
            return SnippetHotkeyStatus.Active;
        }
    }

    /// <summary>The first of Ctrl+Alt+1 to Ctrl+Alt+9 that would work for <paramref name="snippetId"/>.</summary>
    /// <param name="snippetId">The prompt it is for.</param>
    /// <returns>The combination, or null when none is free or shortcuts are unavailable.</returns>
    /// <remarks>
    /// Left-hand combinations: reachable with one hand while the other stays on the mouse, and
    /// clear of a right-hand push-to-talk key. Ones another prompt has are skipped without a probe.
    /// </remarks>
    public HotkeyChord? Suggest(Guid snippetId)
    {
        if (_hotkeys is null) return null;

        for (var digit = '1'; digit <= '9'; digit++)
        {
            var chord = new HotkeyChord(HotkeyModifiers.Control | HotkeyModifiers.Alt, digit);
            if (Check(chord, snippetId) == SnippetHotkeyStatus.Active) return chord;
        }

        return null;
    }

    /// <summary>Gives every combination back until the returned handle is disposed.</summary>
    /// <returns>Disposing it registers them again.</returns>
    /// <remarks>
    /// While the user is recording a new combination: a registered one is swallowed before the
    /// recorder could see it, and would paste a prompt into the editor instead.
    /// </remarks>
    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspended);
        Rebind();
        return new Resumer(this);
    }

    /// <summary>Completes when every prompt fired so far has been pasted.</summary>
    /// <returns>The last fire's task.</returns>
    public Task WhenIdleAsync()
    {
        lock (_fireSync) return _pending;
    }

    /// <summary>Pastes a prompt as if its combination had been pressed.</summary>
    /// <param name="snippetId">The prompt.</param>
    /// <returns>Completes once it has been pasted, or skipped.</returns>
    public Task FireAsync(Guid snippetId)
    {
        lock (_fireSync)
        {
            _pending = _pending.ContinueWith(
                _ => RunFireAsync(snippetId),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
            return _pending;
        }
    }

    /// <summary>A short sentence for the editor and the list, or null when there is nothing to say.</summary>
    /// <param name="status">The status.</param>
    /// <param name="chord">The combination, for the invalid case's reason.</param>
    /// <returns>What to show.</returns>
    public static string? Describe(SnippetHotkeyStatus status, HotkeyChord? chord = null) => status switch
    {
        SnippetHotkeyStatus.Invalid => chord is { } c ? HotkeyChords.Problem(c) : "That shortcut can't be used.",
        SnippetHotkeyStatus.Duplicate => "Another prompt already uses this shortcut.",
        SnippetHotkeyStatus.InUse => "Another app or Windows already uses this shortcut. Pick a different one.",
        SnippetHotkeyStatus.Unsupported => "Shortcuts work only in the Windows app.",
        SnippetHotkeyStatus.PasteLast => "Murmur's paste-last shortcut already uses this. Pick a different one.",
        _ => null,
    };

    private async Task RunFireAsync(Guid snippetId)
    {
        var entry = _store.Entries.FirstOrDefault(e => e.Id == snippetId);
        if (entry is null || !entry.IsEnabled || entry.Text.Length == 0) return;

        // Fires are already one at a time: FireAsync chains each onto the last.
        try
        {
            if (_cancelDictation is not null) await _cancelDictation().ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(_injectionTimeout);
            await _injector.InjectAsync(entry.Text, verbatim: true, timeout.Token)
                .AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fired from the hotkey thread with nobody awaiting it; reported rather than lost.
            FireFaulted?.Invoke(this, ex);
        }
    }

    private void OnPressed(object? sender, int id)
    {
        // No _bindSync here: a Rebind holding it may be waiting on this very thread.
        if (!Volatile.Read(ref _registered).TryGetValue(id, out var snippetId)) return;

        // Returns at once: this runs on the registration thread, which must keep pumping.
        _ = FireAsync(snippetId);
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Rebind();

    private void Rebind()
    {
        lock (_bindSync)
        {
            if (_disposed) return;

            foreach (var id in _registered.Keys) _hotkeys?.Unregister(id);
            Volatile.Write(ref _registered, new Dictionary<int, Guid>());

            var registered = new Dictionary<int, Guid>();
            var statuses = new Dictionary<Guid, SnippetHotkeyStatus>();
            var suspended = Volatile.Read(ref _suspended) > 0;
            var seen = new HashSet<HotkeyChord>();
            var nextId = 1;

            foreach (var entry in _store.Entries)
            {
                if (!entry.IsEnabled || entry.Hotkey is not { } chord) continue;

                if (HotkeyChords.Problem(chord) is not null)
                {
                    statuses[entry.Id] = SnippetHotkeyStatus.Invalid;
                }
                else if (!seen.Add(chord))
                {
                    statuses[entry.Id] = SnippetHotkeyStatus.Duplicate;
                }
                else if (_hotkeys is null)
                {
                    statuses[entry.Id] = SnippetHotkeyStatus.Unsupported;
                }
                else if (suspended)
                {
                    // Reported as it will be once resumed; the recorder is open, not the problem.
                    statuses[entry.Id] = SnippetHotkeyStatus.Active;
                }
                else
                {
                    var id = nextId++;
                    if (_hotkeys.Register(id, chord))
                    {
                        registered[id] = entry.Id;
                        statuses[entry.Id] = SnippetHotkeyStatus.Active;
                    }
                    else
                    {
                        statuses[entry.Id] = HeldByPasteLast(chord)
                            ? SnippetHotkeyStatus.PasteLast
                            : SnippetHotkeyStatus.InUse;
                    }
                }
            }

            Volatile.Write(ref _registered, registered);
            Volatile.Write(ref _statuses, statuses);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        if (_hotkeys is not null) _hotkeys.Pressed -= OnPressed;

        lock (_bindSync)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var id in _registered.Keys) _hotkeys?.Unregister(id);
            Volatile.Write(ref _registered, new Dictionary<int, Guid>());
        }
    }

    private sealed class Resumer(SnippetHotkeyBinder owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            Interlocked.Decrement(ref owner._suspended);
            owner.Rebind();
        }
    }
}
