using System.Runtime.InteropServices;
using Murmur.Abstractions;

namespace Murmur.Platform.Windows;

/// <summary>
/// Synthetic keystrokes through <c>SendInput</c>. Bindings only.
/// </summary>
/// <remarks>
/// <para>
/// <b>UI Automation is not an option for inserting text.</b> <c>TextPattern</c> is documented as
/// providing no means to insert or modify text, and <c>ValuePattern</c> replaces an entire field
/// rather than inserting at the caret. Microsoft's own sample code says plainly: "text input
/// must be simulated." So <c>SendInput</c> is the primary path, not a fallback.
/// </para>
/// <para>
/// Whether to type or paste, how long to wait for modifiers and when to restore the clipboard
/// is decided by <c>Murmur.Core.SmartTextInjector</c>, where it can be tested. Each member here
/// is one <c>SendInput</c> call or one key-state query.
/// </para>
/// <para>
/// <b>Failure can be silent.</b> <c>SendInput</c> is subject to UIPI, and Microsoft documents
/// that when it is blocked "neither GetLastError nor the return value will indicate the failure
/// was caused by UIPI blocking." Injecting into an elevated window from a normal process can
/// return success and do nothing.
/// </para>
/// </remarks>
public sealed class SendInputKeystrokeSender : IKeystrokeSender
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const int VK_CONTROL = 0x11;
    private const int VK_V = 0x56;
    private const int VK_RETURN = 0x0D;

    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>Left and right Shift, Ctrl and Alt, then both Windows keys.</summary>
    private static readonly int[] Modifiers = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParamL, ParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    /// <inheritdoc />
    public IReadOnlyList<int> HeldModifiers()
    {
        var held = new List<int>();
        foreach (var key in Modifiers)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0) held.Add(key);
        }

        return held;
    }

    /// <inheritdoc />
    public bool Release(IReadOnlyList<int> virtualKeys)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        return Send([.. virtualKeys.Select(key => KeyInput(key, up: true))]);
    }

    /// <inheritdoc />
    public bool Press(IReadOnlyList<int> virtualKeys)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        return Send([.. virtualKeys.Select(key => KeyInput(key, up: false))]);
    }

    /// <inheritdoc />
    public bool SendPasteChord() => Send(
    [
        KeyInput(VK_CONTROL, up: false),
        KeyInput(VK_V, up: false),
        KeyInput(VK_V, up: true),
        KeyInput(VK_CONTROL, up: true),
    ]);

    /// <inheritdoc />
    /// <remarks>
    /// Iterating by UTF-16 code unit is correct and intentional. A non-BMP character is two
    /// surrogate halves, each sent as its own event; Unicode-aware controls recombine them. The
    /// 16-bit <c>ScanCode</c> field cannot carry a full 21-bit code point.
    /// </remarks>
    public bool TypeUnicode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(text[i], up: false);
            inputs[(i * 2) + 1] = UnicodeInput(text[i], up: true);
        }

        return Send(inputs);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A real <c>VK_RETURN</c>, not a Unicode packet for U+000D: many controls treat the two
    /// differently, and a chat box that inserts a line break when you meant to send is exactly
    /// the failure the spoken "press enter" exists to avoid.
    /// </remarks>
    public bool Enter() => Send([KeyInput(VK_RETURN, up: false), KeyInput(VK_RETURN, up: true)]);

    private static bool Send(INPUT[] inputs) =>
        inputs.Length == 0 || SendInput((uint)inputs.Length, inputs, InputSize) == inputs.Length;

    private static INPUT UnicodeInput(ushort codeUnit, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = 0,   // must be 0 for KEYEVENTF_UNICODE
                ScanCode = codeUnit,
                Flags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                Time = 0,
                ExtraInfo = PushToTalkHook.InjectedTag,
            },
        },
    };

    private static INPUT KeyInput(int virtualKey, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Union = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = (ushort)virtualKey,
                    ScanCode = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC),
                    Flags = flags,
                    Time = 0,
                    // Tagged so our own hook ignores it and doesn't re-trigger dictation.
                    ExtraInfo = PushToTalkHook.InjectedTag,
                },
            },
        };
    }

    private static bool IsExtendedKey(int virtualKey) => virtualKey is
        0xA3 or 0xA5 or 0x5B or 0x5C or   // right ctrl/alt, both Windows keys
        0x2D or 0x2E or 0x24 or 0x23 or   // insert, delete, home, end
        0x21 or 0x22 or                   // page up/down
        0x25 or 0x26 or 0x27 or 0x28;     // arrows
}
