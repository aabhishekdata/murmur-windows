using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Murmur.Abstractions;

namespace Murmur.Platform.Windows;

/// <summary>
/// Global key combinations through <c>RegisterHotKey</c>. Bindings only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated thread:</b> a hotkey registered with no window belongs to the thread that
/// registered it — <c>WM_HOTKEY</c> is posted to that thread's queue, and only that thread may
/// unregister it. The UI thread would do, but its message loop is Avalonia's, and a hotkey that
/// fires while it is busy laying out waits. So one background thread owns every registration
/// and pumps nothing else; <see cref="Register"/> and <see cref="Unregister"/> are marshalled to
/// it and wait for the answer.
/// </para>
/// <para>
/// <c>MOD_NOREPEAT</c> is always set, so holding the combination pastes once rather than once per
/// keyboard repeat. The combination is consumed: the focused app never sees it.
/// </para>
/// <para>
/// Which combinations are registered, and what happens when one fires, is
/// <c>Murmur.Core.SnippetHotkeyBinder</c>'s business.
/// </para>
/// </remarks>
public sealed class RegisterHotKeyGlobalHotkeys : IGlobalHotkeys
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_WORK = 0x8001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint PM_NOREMOVE = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out MSG message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _startLock = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly HashSet<int> _ids = [];
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<int>? Pressed;

    /// <inheritdoc />
    public bool Register(int id, HotkeyChord chord) => Invoke(
        () =>
        {
            // Registering an id twice fails; replacing is what a caller re-registering means.
            if (_ids.Remove(id)) UnregisterHotKey(IntPtr.Zero, id);

            var ok = RegisterHotKey(IntPtr.Zero, id, (uint)chord.Modifiers | MOD_NOREPEAT, (uint)chord.VirtualKey);
            if (ok) _ids.Add(id);
            return ok;
        },
        // The caller was told it failed, so it will not unregister it: a registration left
        // behind would swallow the combination system-wide and do nothing.
        undoIfAbandoned: () =>
        {
            if (_ids.Remove(id)) UnregisterHotKey(IntPtr.Zero, id);
        });

    /// <inheritdoc />
    public void Unregister(int id) => Invoke(() => _ids.Remove(id) && UnregisterHotKey(IntPtr.Zero, id));

    /// <summary>Runs <paramref name="call"/> on the registration thread and waits for it.</summary>
    /// <param name="call">The work; its result is the answer.</param>
    /// <param name="undoIfAbandoned">
    /// Run on the registration thread when <paramref name="call"/> succeeded after the caller
    /// had stopped waiting and been told it failed.
    /// </param>
    private bool Invoke(Func<bool> call, Action? undoIfAbandoned = null)
    {
        if (!EnsureStarted()) return false;
        if (GetCurrentThreadId() == _threadId) return call();

        // Completed by whichever side gets there first: the work with its result, or the
        // caller giving up. That one race decides whether a late success must be undone.
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            var ok = false;
            try
            {
                ok = call();
            }
            finally
            {
                if (!answer.TrySetResult(ok) && ok) undoIfAbandoned?.Invoke();
            }
        });

        if (PostThreadMessage(_threadId, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero) && answer.Task.Wait(CallTimeout))
        {
            return answer.Task.Result;
        }

        // Timed out, or the thread has died. If the work finished just now, its answer stands.
        return !answer.TrySetCanceled() && answer.Task.Result;
    }

    private bool EnsureStarted()
    {
        lock (_startLock)
        {
            if (_disposed) return false;
            if (_thread is not null) return _threadId != 0;

            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                // Creates this thread's message queue, so a PostThreadMessage sent the moment
                // the caller is released is not lost.
                PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
                _threadId = GetCurrentThreadId();

                // ReSharper disable once AccessToDisposedClosure
                ready.Set();
                Pump();
            })
            {
                IsBackground = true,
                Name = "Murmur global hotkeys",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(CallTimeout);
            return _threadId != 0;
        }
    }

    private void Pump()
    {
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == WM_HOTKEY)
            {
                try
                {
                    Pressed?.Invoke(this, message.WParam.ToInt32());
                }
                catch (Exception)
                {
                    // A handler's failure must not end the pump, or every shortcut stops.
                }
            }
            else if (message.Message == WM_APP_WORK)
            {
                while (_work.TryDequeue(out var work)) work();
            }
        }

        // WM_QUIT: give everything back before the thread, which owns the registrations, ends.
        while (_work.TryDequeue(out var work)) work();
        foreach (var id in _ids) UnregisterHotKey(IntPtr.Zero, id);
        _ids.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Thread? thread;
        lock (_startLock)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }

        if (thread is null || _threadId == 0) return;

        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(CallTimeout);
    }
}
