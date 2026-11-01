using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>
/// Turns a <see cref="HotkeyChord"/> into text such as <c>Ctrl+Alt+1</c> and back.
/// </summary>
/// <remarks>
/// <para>
/// The text form is what is stored in <c>snippets.jsonl</c> and what the user reads, so it is
/// the same string in both places: a file a person can read and fix by hand beats a pair of
/// integers nobody can.
/// </para>
/// <para>
/// Only keys in the table below are accepted. It is deliberately the keys a person would bind a
/// shortcut to — letters, digits, function keys, the number pad, navigation and the punctuation
/// keys of a US layout — rather than every virtual-key code Windows defines.
/// </para>
/// </remarks>
public static class HotkeyChords
{
    private static readonly (HotkeyModifiers Flag, string Name)[] ModifierOrder =
    [
        (HotkeyModifiers.Control, "Ctrl"),
        (HotkeyModifiers.Alt, "Alt"),
        (HotkeyModifiers.Shift, "Shift"),
        (HotkeyModifiers.Windows, "Win"),
    ];

    private static readonly Dictionary<string, HotkeyModifiers> ModifierNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl"] = HotkeyModifiers.Control,
            ["Control"] = HotkeyModifiers.Control,
            ["Alt"] = HotkeyModifiers.Alt,
            ["Shift"] = HotkeyModifiers.Shift,
            ["Win"] = HotkeyModifiers.Windows,
            ["Windows"] = HotkeyModifiers.Windows,
        };

    private static readonly Dictionary<int, string> KeyNames = BuildKeyNames();

    private static readonly Dictionary<string, int> KeysByName =
        KeyNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private const int F1 = 0x70;
    private const int F13 = 0x7C;
    private const int F24 = 0x87;

    /// <summary>Whether <paramref name="virtualKey"/> is a key this table can name.</summary>
    /// <param name="virtualKey">A Windows virtual-key code.</param>
    /// <returns>True for a key that can end a chord.</returns>
    public static bool IsNamedKey(int virtualKey) => KeyNames.ContainsKey(virtualKey);

    /// <summary>Writes <paramref name="chord"/> the way the user reads it.</summary>
    /// <param name="chord">The combination.</param>
    /// <returns>Modifiers in a fixed order, then the key: <c>Ctrl+Alt+1</c>.</returns>
    public static string Format(HotkeyChord chord)
    {
        var parts = new List<string>(5);
        foreach (var (flag, name) in ModifierOrder)
        {
            if (chord.Modifiers.HasFlag(flag)) parts.Add(name);
        }

        parts.Add(KeyNames.TryGetValue(chord.VirtualKey, out var key)
            ? key
            : string.Create(CultureInfo.InvariantCulture, $"VK{chord.VirtualKey:X2}"));

        return string.Join('+', parts);
    }

    /// <summary>Reads text written by <see cref="Format"/>, or typed by hand.</summary>
    /// <param name="text">For example <c>ctrl + alt + 1</c>. Case and spacing do not matter.</param>
    /// <param name="chord">The combination, when the text is one.</param>
    /// <returns>False for an unknown key or modifier, a repeated modifier, or no key at all.</returns>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[^1].Length == 0) return false;

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!ModifierNames.TryGetValue(parts[i], out var flag) || modifiers.HasFlag(flag)) return false;
            modifiers |= flag;
        }

        if (!KeysByName.TryGetValue(parts[^1], out var virtualKey)) return false;

        chord = new HotkeyChord(modifiers, virtualKey);
        return true;
    }

    /// <summary>Reads a chord, throwing when the text is not one.</summary>
    /// <param name="text">The chord's text.</param>
    /// <returns>The combination.</returns>
    /// <exception cref="FormatException">The text is not a chord.</exception>
    public static HotkeyChord Parse(string text) =>
        TryParse(text, out var chord)
            ? chord
            : throw new FormatException($"\"{text}\" is not a key combination.");

    /// <summary>Why <paramref name="chord"/> would make a bad global shortcut, if it would.</summary>
    /// <param name="chord">The combination.</param>
    /// <returns>A sentence to show the user, or null when the chord is fine.</returns>
    /// <remarks>
    /// A global shortcut is taken away from every other program, so a combination that types
    /// something — a bare letter, or Shift and a letter — would stop the user typing it anywhere.
    /// Ctrl, Alt or the Windows key has to be part of it; the exceptions are F13 to F24, which
    /// no program uses and most keyboards lack, and the other function keys with a modifier.
    /// </remarks>
    public static string? Problem(HotkeyChord chord)
    {
        if (!IsNamedKey(chord.VirtualKey)) return "That key can't be used in a shortcut.";

        var isFunctionKey = chord.VirtualKey is >= F1 and <= F24;
        if (isFunctionKey && (chord.Modifiers != HotkeyModifiers.None || chord.VirtualKey >= F13)) return null;

        const HotkeyModifiers anchors = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows;
        return (chord.Modifiers & anchors) != 0
            ? null
            : "Add Ctrl, Alt or the Windows key, or the key would stop typing in every other app.";
    }

    private static Dictionary<int, string> BuildKeyNames()
    {
        var names = new Dictionary<int, string>();

        for (var c = '0'; c <= '9'; c++) names[c] = c.ToString();
        for (var c = 'A'; c <= 'Z'; c++) names[c] = c.ToString();
        for (var i = 0; i < 24; i++) names[F1 + i] = string.Create(CultureInfo.InvariantCulture, $"F{i + 1}");
        for (var i = 0; i < 10; i++) names[0x60 + i] = string.Create(CultureInfo.InvariantCulture, $"Num{i}");

        names[0x20] = "Space";
        names[0x09] = "Tab";
        names[0x0D] = "Enter";
        names[0x13] = "Pause";
        names[0x2D] = "Insert";
        names[0x2E] = "Delete";
        names[0x24] = "Home";
        names[0x23] = "End";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x25] = "Left";
        names[0x26] = "Up";
        names[0x27] = "Right";
        names[0x28] = "Down";

        // US-layout punctuation keys. The name is what the key types unshifted there; on other
        // layouts the same physical key types something else, which is the usual compromise.
        names[0xBA] = ";";
        names[0xBB] = "=";
        names[0xBC] = ",";
        names[0xBD] = "-";
        names[0xBE] = ".";
        names[0xBF] = "/";
        names[0xC0] = "`";
        names[0xDB] = "[";
        names[0xDC] = "\\";
        names[0xDD] = "]";
        names[0xDE] = "'";

        return names;
    }
}

/// <summary>Stores a <see cref="HotkeyChord"/> as its text form.</summary>
/// <remarks>
/// Text that does not parse reads as no shortcut rather than failing the line, so a hand-edited
/// typo costs one prompt its shortcut instead of costing the user the whole prompt.
/// </remarks>
public sealed class HotkeyChordJsonConverter : JsonConverter<HotkeyChord?>
{
    private static readonly JavaScriptEncoder RelaxedEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override HotkeyChord? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        return HotkeyChords.TryParse(reader.GetString(), out var chord) ? chord : null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, HotkeyChord? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Relaxed escaping, so the file says "Ctrl+Alt+1" rather than "Ctrl\u002BAlt\u002B1": the
        // point of storing text is that a person can read it.
        if (value is { } chord)
        {
            writer.WriteStringValue(JsonEncodedText.Encode(HotkeyChords.Format(chord), RelaxedEncoder));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
