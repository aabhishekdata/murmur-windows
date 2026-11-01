using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Murmur.Abstractions;
using Murmur.App.Views;
using Murmur.Core;

namespace Murmur.App.Controls;

/// <summary>
/// A field that records a key combination: focus it, press the keys, and that is the shortcut.
/// </summary>
/// <remarks>
/// <para>
/// Built on a read-only text field so it looks, focuses and tabs like every other field in the
/// editor. Keys are caught on the tunnelling pass, before the field could treat them as text.
/// </para>
/// <para>
/// Esc clears. Tab on its own still moves focus — a recorder that traps the keyboard is a trap.
/// Modifier keys pressed alone only preview ("Ctrl+Alt+…"); the combination is taken when a
/// non-modifier key arrives.
/// </para>
/// <para>
/// While focused, it holds a suspension from <see cref="SnippetHotkeyBinder.Suspend"/>, if it was
/// given one: a registered combination is swallowed by Windows before any window sees it, so
/// without this, pressing an existing shortcut here would paste a prompt into the editor.
/// </para>
/// </remarks>
public sealed class ShortcutRecorder : UserControl
{
    private const string Placeholder = "Click, then press keys — e.g. Ctrl+Alt+1";
    private const string Listening = "Press a combination… Esc clears";

    private readonly TextBox _field;
    private readonly Func<IDisposable>? _suspend;
    private IDisposable? _suspension;
    private HotkeyChord? _chord;

    /// <summary>Creates the recorder.</summary>
    /// <param name="chord">The combination it starts with, or null.</param>
    /// <param name="suspend">Gives the registered shortcuts back while focused; null for none.</param>
    public ShortcutRecorder(HotkeyChord? chord, Func<IDisposable>? suspend = null)
    {
        _chord = chord;
        _suspend = suspend;

        _field = Panels.Field(Placeholder, string.Empty);
        _field.IsReadOnly = true;
        _field.CaretBrush = Brushes.Transparent;
        _field.Cursor = new Cursor(StandardCursorType.Hand);

        _field.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _field.GotFocus += (_, _) =>
        {
            _suspension ??= _suspend?.Invoke();
            Show();
        };
        _field.LostFocus += (_, _) =>
        {
            _suspension?.Dispose();
            _suspension = null;
            Show();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _suspension?.Dispose();
            _suspension = null;
        };

        Content = _field;
        Show();
    }

    /// <summary>Raised when the combination changes, including to none.</summary>
    public event EventHandler? ChordChanged;

    /// <summary>The recorded combination, or null for none.</summary>
    public HotkeyChord? Chord
    {
        get => _chord;
        set
        {
            if (_chord == value) return;
            _chord = value;
            Show();
            ChordChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What the field says right now.</summary>
    public string DisplayText => _field.Text ?? string.Empty;

    /// <summary>Handles one key press as the recorder would.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers held with it.</param>
    /// <returns>True when the key was consumed.</returns>
    /// <remarks>Separate from the event handler so tests can drive it without a real keyboard.</remarks>
    public bool Accept(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Escape)
        {
            Chord = null;
            return true;
        }

        if (key == Key.Tab && modifiers == KeyModifiers.None) return false;

        if (IsModifier(key))
        {
            var held = ToModifiers(modifiers);
            _field.Text = held == HotkeyModifiers.None ? Listening : ModifierText(held) + "+…";
            return true;
        }

        if (VirtualKeyOf(key) is not { } virtualKey) return true;

        Chord = new HotkeyChord(ToModifiers(modifiers), virtualKey);
        return true;
    }

    /// <summary>The Windows virtual-key code for an Avalonia key, or null for one a shortcut can't use.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The code, or null.</returns>
    public static int? VirtualKeyOf(Key key)
    {
        int? code = key switch
        {
            >= Key.A and <= Key.Z => 'A' + (key - Key.A),
            >= Key.D0 and <= Key.D9 => '0' + (key - Key.D0),
            >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
            >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
            Key.Space => 0x20,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Pause => 0x13,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            _ => null,
        };

        return code is { } value && HotkeyChords.IsNamedKey(value) ? value : null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Accept(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    private void Show() => _field.Text = _chord is { } chord
        ? HotkeyChords.Format(chord)
        : _field.IsFocused ? Listening : string.Empty;

    /// <summary>"Ctrl+Alt" for a half-pressed combination, in the order a full one is written.</summary>
    private static string ModifierText(HotkeyModifiers held)
    {
        // Formatted with a placeholder key that is then cut off, so the order and the names
        // can never drift from HotkeyChords.Format.
        var full = HotkeyChords.Format(new HotkeyChord(held, 'A'));
        return full[..^"+A".Length];
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static HotkeyModifiers ToModifiers(KeyModifiers modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= HotkeyModifiers.Windows;
        return result;
    }
}
