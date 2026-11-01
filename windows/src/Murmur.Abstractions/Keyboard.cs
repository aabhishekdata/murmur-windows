namespace Murmur.Abstractions;

/// <summary>The modifier keys of a key combination.</summary>
/// <remarks>
/// The values are Win32's <c>MOD_*</c> flags, so the platform layer can hand them to
/// <c>RegisterHotKey</c> as they are. Side-agnostic on purpose: that API cannot tell left Ctrl
/// from right Ctrl, and a combination the user can only press with one hand is not one they
/// would choose.
/// </remarks>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Either Alt key.</summary>
    Alt = 0x1,

    /// <summary>Either Ctrl key.</summary>
    Control = 0x2,

    /// <summary>Either Shift key.</summary>
    Shift = 0x4,

    /// <summary>Either Windows key.</summary>
    Windows = 0x8,
}

/// <summary>A global key combination, such as Ctrl+Alt+1.</summary>
/// <param name="Modifiers">The keys held.</param>
/// <param name="VirtualKey">The Windows virtual-key code of the key pressed with them.</param>
/// <remarks>
/// A plain virtual-key code rather than an enum, for the reason <see cref="IHotkeySource"/>
/// gives. Parsing and formatting live in <c>Murmur.Core</c>, which has the name table.
/// </remarks>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, int VirtualKey);

/// <summary>
/// Raw synthetic keystrokes, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The bindings under text injection. Deciding <i>what</i> to send — type or paste, how long to
/// wait for the user to let go of Ctrl, when to put the clipboard back — is logic, and lives in
/// <c>Murmur.Core</c>'s <c>SmartTextInjector</c> where CI can exercise it. Each member here is
/// one <c>SendInput</c> call or one key-state query.
/// </para>
/// <para>
/// Every event an implementation sends must be tagged so the push-to-talk hook ignores it.
/// </para>
/// </remarks>
public interface IKeystrokeSender
{
    /// <summary>The modifier keys held down right now, as side-specific virtual-key codes.</summary>
    /// <returns>Left/right Shift, Ctrl and Alt and both Windows keys, whichever are down.</returns>
    IReadOnlyList<int> HeldModifiers();

    /// <summary>Sends a key-up for each of <paramref name="virtualKeys"/>.</summary>
    /// <param name="virtualKeys">Side-specific virtual-key codes.</param>
    /// <returns>False if the input was not accepted.</returns>
    bool Release(IReadOnlyList<int> virtualKeys);

    /// <summary>Sends a key-down for each of <paramref name="virtualKeys"/>.</summary>
    /// <param name="virtualKeys">Side-specific virtual-key codes.</param>
    /// <returns>False if the input was not accepted.</returns>
    bool Press(IReadOnlyList<int> virtualKeys);

    /// <summary>Sends Ctrl down, V down, V up, Ctrl up, as one batch.</summary>
    /// <returns>False if the input was not accepted.</returns>
    bool SendPasteChord();

    /// <summary>Types <paramref name="text"/> as Unicode packets, in one batch.</summary>
    /// <param name="text">Single-line text. The caller chunks it.</param>
    /// <returns>False if the input was not accepted.</returns>
    bool TypeUnicode(string text);

    /// <summary>Sends a real Enter key-down and key-up.</summary>
    /// <returns>False if the input was not accepted.</returns>
    bool Enter();
}

/// <summary>Reads and writes the clipboard's text.</summary>
/// <remarks>
/// Clipboard access belongs to the UI framework and its thread, so the app supplies this and
/// marshals each call itself.
/// </remarks>
public interface IClipboardService
{
    /// <summary>The clipboard's text, or null when it holds none.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text, or null.</returns>
    Task<string?> GetTextAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the clipboard's contents.</summary>
    /// <remarks>
    /// What is written here is transient — a paste in progress, or the user's text going back
    /// after it — so implementations keep it out of clipboard history and cloud sync where the
    /// platform allows.
    /// </remarks>
    /// <param name="text">The text to put there, or null to empty it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>False if the clipboard could not be written.</returns>
    Task<bool> SetTextAsync(string? text, CancellationToken cancellationToken);
}

/// <summary>System-wide key combinations, each consumed rather than passed to the focused app.</summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IHotkeySource"/>, which watches one held key for press <i>and</i>
/// release and never swallows it. These are the other kind — "Ctrl+Alt+1 does something" — and
/// on Windows that is <c>RegisterHotKey</c>, which is first come, first served: a combination
/// another program holds cannot be registered, and <see cref="Register"/> says so.
/// </para>
/// <para>
/// <see cref="Pressed"/> is raised on the implementation's own thread. Handlers must return
/// quickly and hand real work off.
/// </para>
/// </remarks>
public interface IGlobalHotkeys : IDisposable
{
    /// <summary>A registered combination was pressed. Carries the id it was registered under.</summary>
    event EventHandler<int>? Pressed;

    /// <summary>Claims <paramref name="chord"/> for this process.</summary>
    /// <param name="id">The caller's name for it, echoed by <see cref="Pressed"/>.</param>
    /// <param name="chord">The combination.</param>
    /// <returns>False when another program, or Windows itself, already holds it.</returns>
    bool Register(int id, HotkeyChord chord);

    /// <summary>Gives a combination back. Does nothing for an id that is not registered.</summary>
    /// <param name="id">What it was registered under.</param>
    void Unregister(int id);
}
