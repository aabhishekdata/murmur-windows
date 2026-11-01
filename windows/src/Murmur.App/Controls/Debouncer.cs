using Avalonia.Threading;

namespace Murmur.App.Controls;

/// <summary>
/// Runs an action once input has paused, on the UI thread.
/// </summary>
/// <remarks>
/// For search boxes: rebuilding a list of a few thousand transcripts on every keystroke makes
/// typing lag behind the keyboard. The timer exists only while a run is pending, so an idle
/// view costs nothing.
/// </remarks>
internal sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private readonly Action _action;

    /// <summary>Creates a debouncer that runs <paramref name="action"/> after <paramref name="delay"/> of quiet.</summary>
    /// <param name="delay">How long input must pause.</param>
    /// <param name="action">What to run.</param>
    public Debouncer(TimeSpan delay, Action action)
    {
        _action = action;
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _action();
        };
    }

    /// <summary>Whether a run is waiting.</summary>
    public bool IsPending => _timer.IsEnabled;

    /// <summary>Starts, or restarts, the wait.</summary>
    public void Trigger()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Runs a pending action now. Does nothing when none is pending.</summary>
    public void Flush()
    {
        if (!_timer.IsEnabled) return;

        _timer.Stop();
        _action();
    }
}
