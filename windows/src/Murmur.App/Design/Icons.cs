using System.Collections.Concurrent;
using Avalonia.Media;

namespace Murmur.App.Design;

/// <summary>
/// The app's icons: Fluent System Icons, as path data.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft's Fluent System Icons (MIT — credited in <c>THIRD-PARTY-NOTICES.md</c>), copied from
/// <c>@fluentui/svg-icons</c> 1.1.292, the 20 px regular and filled sets. They replace the
/// font glyphs the sidebar used to borrow from Inter (⌂ A ¶ ⚙), which drew at different weights
/// and sizes from one another and looked like nothing in Windows.
/// </para>
/// <para>
/// Path strings rather than an icon font: a handful of icons is a few kilobytes of text, needs no
/// font file in the bundle, and renders identically on the headless test runner. Each is drawn on
/// a 20 × 20 grid; <see cref="Size"/> is that grid. The <c>F1</c> prefix selects the non-zero fill
/// rule SVG uses — Avalonia's default, even-odd, would punch holes in overlapping shapes.
/// </para>
/// <para>
/// To add one, take the <c>d</c> attribute from the SVG of the same name and keep the
/// <c>F1</c>. Every constant here is parsed by a test.
/// </para>
/// </remarks>
public static class Icons
{
    /// <summary>The grid every icon is drawn on.</summary>
    public const double Size = 20;

    private static readonly ConcurrentDictionary<string, Geometry> s_parsed = new(StringComparer.Ordinal);

    /// <summary>The parsed geometry for one of the constants here, shared and cached.</summary>
    /// <param name="data">Path data.</param>
    /// <returns>The geometry. Must not be modified.</returns>
    public static Geometry Geometry(string data) =>
        s_parsed.GetOrAdd(data, static d => StreamGeometry.Parse(d));

    /// <summary>Home — the hub's first page. <c>home_20_regular</c>.</summary>
    public const string Home =
        "F1 M9 2.39a1.5 1.5 0 0 1 2 0l5.5 4.94c.32.28.5.69.5 1.12v7.05c0 .83-.67 1.5-1.5 1.5H13a1.5 1.5 0 0 1-1.5-1.5V12a.5.5 0 0 0-.5-.5H9a.5.5 0 0 0-.5.5v3.5c0 .83-.67 1.5-1.5 1.5H4.5A1.5 1.5 0 0 1 3 15.5V8.45c0-.43.18-.84.5-1.12L9 2.39Zm1.33.74a.5.5 0 0 0-.66 0l-5.5 4.94a.5.5 0 0 0-.17.38v7.05c0 .28.22.5.5.5H7a.5.5 0 0 0 .5-.5V12c0-.83.67-1.5 1.5-1.5h2c.83 0 1.5.67 1.5 1.5v3.5c0 .28.22.5.5.5h2.5a.5.5 0 0 0 .5-.5V8.45a.5.5 0 0 0-.17-.38l-5.5-4.94Z";

    /// <summary>Home, selected. <c>home_20_filled</c>.</summary>
    public const string HomeFilled =
        "F1 M11 2.39a1.5 1.5 0 0 0-2 0L3.5 7.33c-.32.28-.5.69-.5 1.12v7.05c0 .83.67 1.5 1.5 1.5h2c.83 0 1.5-.67 1.5-1.5v-4c0-.28.22-.5.5-.5h3c.28 0 .5.22.5.5v4c0 .83.67 1.5 1.5 1.5h2c.83 0 1.5-.67 1.5-1.5V8.45c0-.43-.18-.84-.5-1.12L11 2.39Z";

    /// <summary>History — every transcript. <c>history_20_regular</c>.</summary>
    public const string History =
        "F1 M10 4a6 6 0 1 1-5.98 5.54.5.5 0 1 0-1-.08A7 7 0 1 0 5 5.1V3.5a.5.5 0 0 0-1 0v3c0 .28.22.5.5.5h3a.5.5 0 0 0 0-1H5.53c1.1-1.23 2.7-2 4.47-2Zm0 2.5a.5.5 0 0 0-1 0v4c0 .28.22.5.5.5h3a.5.5 0 0 0 0-1H10V6.5Z";

    /// <summary>History, selected. <c>history_20_filled</c>.</summary>
    public const string HistoryFilled =
        "F1 M10 4a6 6 0 1 1-6 5.78.75.75 0 1 0-1.5-.06V10A7.5 7.5 0 1 0 5 4.41V3.25a.75.75 0 0 0-1.5 0v3c0 .41.34.75.75.75h3a.75.75 0 0 0 0-1.5H6.03A5.98 5.98 0 0 1 10 4Zm.5 2.25a.75.75 0 0 0-1.5 0v4c0 .41.34.75.75.75h3a.75.75 0 0 0 0-1.5H10.5V6.25Z";

    /// <summary>Dictionary — custom words and corrections. <c>book_letter_20_regular</c>.</summary>
    public const string Dictionary =
        "F1 M4 4v12c0 1.1.9 2 2 2h9.5a.5.5 0 0 0 0-1H6a1 1 0 0 1-1-1h10a1 1 0 0 0 1-1V4a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2Zm10-1a1 1 0 0 1 1 1v11H5V4a1 1 0 0 1 1-1h8Zm-4 2c.2 0 .38.12.46.3l3 7a.5.5 0 0 1-.92.4l-.73-1.7H8.2l-.73 1.7a.5.5 0 0 1-.92-.4l3-7A.5.5 0 0 1 10 5Zm-1.38 5h2.76L10 6.77 8.62 10Z";

    /// <summary>Dictionary, selected. <c>book_letter_20_filled</c>.</summary>
    public const string DictionaryFilled =
        "F1 M6 2a2 2 0 0 0-2 2v12c0 1.1.9 2 2 2h9.5a.5.5 0 0 0 0-1H6a1 1 0 0 1-1-1h10a1 1 0 0 0 1-1V4a2 2 0 0 0-2-2H6Zm4 3c.2 0 .38.12.46.3l3 7a.5.5 0 0 1-.92.4l-.73-1.7H8.2l-.73 1.7a.5.5 0 0 1-.92-.4l3-7A.5.5 0 0 1 10 5Zm-1.38 5h2.76L10 6.77 8.62 10Z";

    /// <summary>Prompts and snippets. <c>clipboard_text_ltr_20_regular</c>.</summary>
    public const string Prompts =
        "F1 M6.5 8a.5.5 0 0 0 0 1h7a.5.5 0 0 0 0-1h-7ZM6 11.5c0-.28.22-.5.5-.5h3a.5.5 0 0 1 0 1h-3a.5.5 0 0 1-.5-.5Zm.5 2.5a.5.5 0 0 0 0 1h5a.5.5 0 0 0 0-1h-5Zm2-12c-.65 0-1.2.42-1.41 1H5.5C4.67 3 4 3.67 4 4.5v12c0 .83.67 1.5 1.5 1.5h9c.83 0 1.5-.67 1.5-1.5v-12c0-.83-.67-1.5-1.5-1.5h-1.59c-.2-.58-.76-1-1.41-1h-3Zm3 1a.5.5 0 0 1 0 1h-3a.5.5 0 0 1 0-1h3Zm-6 1h1.59c.2.58.76 1 1.41 1h3c.65 0 1.2-.42 1.41-1h1.59c.28 0 .5.22.5.5v12a.5.5 0 0 1-.5.5h-9a.5.5 0 0 1-.5-.5v-12c0-.28.22-.5.5-.5Z";

    /// <summary>Prompts and snippets, selected. <c>clipboard_text_ltr_20_filled</c>.</summary>
    public const string PromptsFilled =
        "F1 M8.5 2c-.65 0-1.2.42-1.41 1H5.5C4.67 3 4 3.67 4 4.5v12c0 .83.67 1.5 1.5 1.5h9c.83 0 1.5-.67 1.5-1.5v-12c0-.83-.67-1.5-1.5-1.5h-1.59c-.2-.58-.76-1-1.41-1h-3Zm3 1a.5.5 0 0 1 0 1h-3a.5.5 0 0 1 0-1h3Zm-5 5h7a.5.5 0 0 1 0 1h-7a.5.5 0 0 1 0-1Zm0 3h3a.5.5 0 0 1 0 1h-3a.5.5 0 0 1 0-1ZM6 14.5c0-.28.22-.5.5-.5h5a.5.5 0 0 1 0 1h-5a.5.5 0 0 1-.5-.5Z";

    /// <summary>Settings. <c>settings_20_regular</c>.</summary>
    public const string Settings =
        "F1 M1.91 7.38A8.5 8.5 0 0 1 3.7 4.3a.5.5 0 0 1 .54-.13l1.92.68a1 1 0 0 0 1.32-.76l.36-2a.5.5 0 0 1 .4-.4 8.53 8.53 0 0 1 3.55 0c.2.04.35.2.38.4l.37 2a1 1 0 0 0 1.32.76l1.92-.68a.5.5 0 0 1 .54.13 8.5 8.5 0 0 1 1.78 3.08c.06.2 0 .4-.15.54l-1.56 1.32a1 1 0 0 0 0 1.52l1.56 1.32a.5.5 0 0 1 .15.54 8.5 8.5 0 0 1-1.78 3.08.5.5 0 0 1-.54.13l-1.92-.68a1 1 0 0 0-1.32.76l-.37 2a.5.5 0 0 1-.38.4 8.53 8.53 0 0 1-3.56 0 .5.5 0 0 1-.39-.4l-.36-2a1 1 0 0 0-1.32-.76l-1.92.68a.5.5 0 0 1-.54-.13 8.5 8.5 0 0 1-1.78-3.08.5.5 0 0 1 .15-.54l1.56-1.32a1 1 0 0 0 0-1.52L2.06 7.92a.5.5 0 0 1-.15-.54Zm1.06 0 1.3 1.1a2 2 0 0 1 0 3.04l-1.3 1.1c.3.79.72 1.51 1.25 2.16l1.6-.58a2 2 0 0 1 2.63 1.53l.3 1.67a7.56 7.56 0 0 0 2.5 0l.3-1.67a2 2 0 0 1 2.64-1.53l1.6.58a7.5 7.5 0 0 0 1.24-2.16l-1.3-1.1a2 2 0 0 1 0-3.04l1.3-1.1a7.5 7.5 0 0 0-1.25-2.16l-1.6.58a2 2 0 0 1-2.63-1.53l-.3-1.67a7.55 7.55 0 0 0-2.5 0l-.3 1.67A2 2 0 0 1 5.81 5.8l-1.6-.58a7.5 7.5 0 0 0-1.24 2.16ZM7.5 10a2.5 2.5 0 1 1 5 0 2.5 2.5 0 0 1-5 0Zm1 0a1.5 1.5 0 1 0 3 0 1.5 1.5 0 0 0-3 0Z";

    /// <summary>Settings, selected. <c>settings_20_filled</c>.</summary>
    public const string SettingsFilled =
        "F1 M1.91 7.38A8.5 8.5 0 0 1 3.7 4.3a.5.5 0 0 1 .54-.13l1.92.68a1 1 0 0 0 1.32-.76l.36-2a.5.5 0 0 1 .4-.4 8.53 8.53 0 0 1 3.55 0c.2.04.35.2.38.4l.37 2a1 1 0 0 0 1.32.76l1.92-.68a.5.5 0 0 1 .54.13 8.5 8.5 0 0 1 1.78 3.08c.06.2 0 .4-.15.54l-1.56 1.32a1 1 0 0 0 0 1.52l1.56 1.32a.5.5 0 0 1 .15.54 8.5 8.5 0 0 1-1.78 3.08.5.5 0 0 1-.54.13l-1.92-.68a1 1 0 0 0-1.32.76l-.37 2a.5.5 0 0 1-.38.4 8.53 8.53 0 0 1-3.56 0 .5.5 0 0 1-.39-.4l-.36-2a1 1 0 0 0-1.32-.76l-1.92.68a.5.5 0 0 1-.54-.13 8.5 8.5 0 0 1-1.78-3.08.5.5 0 0 1 .15-.54l1.56-1.32a1 1 0 0 0 0-1.52L2.06 7.92a.5.5 0 0 1-.15-.54ZM8 10a2 2 0 1 0 4 0 2 2 0 0 0-4 0Z";

    /// <summary>Transcribed on this PC (Parakeet). <c>laptop_20_regular</c>.</summary>
    public const string Laptop =
        "F1 M3 7c0-1.1.9-2 2-2h10a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Zm2-1a1 1 0 0 0-1 1v5a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V7a1 1 0 0 0-1-1H5Zm-3 9.5c0-.28.22-.5.5-.5h15a.5.5 0 0 1 0 1h-15a.5.5 0 0 1-.5-.5Z";

    /// <summary>Transcribed in the cloud (Azure). <c>cloud_20_regular</c>.</summary>
    public const string Cloud =
        "F1 M10 4c2.82 0 4.41 1.92 4.65 4.25h.07A3.33 3.33 0 0 1 18 11.62 3.33 3.33 0 0 1 14.72 15H5.28A3.33 3.33 0 0 1 2 11.62c0-1.8 1.37-3.27 3.1-3.37h.25C5.6 5.9 7.18 4 10 4Zm0 1C7.89 5 6.55 6.32 6.35 8.35a1 1 0 0 1-1 .9h-.07A2.33 2.33 0 0 0 3 11.62 2.33 2.33 0 0 0 5.28 14h9.44A2.33 2.33 0 0 0 17 11.62a2.33 2.33 0 0 0-2.28-2.37h-.07a1 1 0 0 1-1-.9C13.45 6.32 12.11 5 10 5Z";

    /// <summary>The cloud is unreachable. <c>cloud_off_20_regular</c>.</summary>
    public const string CloudOff =
        "F1 M2.85 2.15a.5.5 0 1 0-.7.7l3.67 3.67c-.25.53-.4 1.11-.47 1.73H5.1A3.34 3.34 0 0 0 2 11.62 3.33 3.33 0 0 0 5.28 15h9.01l2.86 2.85a.5.5 0 0 0 .7-.7l-15-15ZM13.3 14h-8A2.33 2.33 0 0 1 3 11.62a2.33 2.33 0 0 1 2.28-2.37h.07a1 1 0 0 0 1-.9c.04-.38.11-.74.23-1.06L13.29 14ZM17 11.62c0 .9-.48 1.68-1.18 2.08l.73.73a3.4 3.4 0 0 0 1.45-2.8 3.33 3.33 0 0 0-3.28-3.38h-.07C14.4 5.92 12.82 4 10 4c-1.2 0-2.2.35-2.94.94l.71.71A3.76 3.76 0 0 1 10 5c2.1 0 3.45 1.32 3.65 3.35a1 1 0 0 0 1 .9h.07A2.33 2.33 0 0 1 17 11.62Z";

    /// <summary>The microphone, and the Transcription group. <c>mic_20_regular</c>.</summary>
    public const string Mic =
        "F1 M10 13a3 3 0 0 0 3-3V5a3 3 0 1 0-6 0v5a3 3 0 0 0 3 3Zm0-1a2 2 0 0 1-2-2V5a2 2 0 1 1 4 0v5a2 2 0 0 1-2 2ZM5 9.5c.28 0 .5.22.5.5a4.5 4.5 0 1 0 9 0 .5.5 0 0 1 1 0 5.5 5.5 0 0 1-5 5.48v2.02a.5.5 0 0 1-1 0v-2.02a5.5 5.5 0 0 1-5-5.48c0-.28.22-.5.5-.5Z";

    /// <summary>The push-to-talk key. <c>keyboard_20_regular</c>.</summary>
    public const string Keyboard =
        "F1 M5 12.5c0-.28.22-.5.5-.5h9a.5.5 0 0 1 0 1h-9a.5.5 0 0 1-.5-.5ZM11.5 8a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5Zm3.75-.75a.75.75 0 1 1-1.5 0 .75.75 0 0 1 1.5 0ZM5.5 8a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5Zm2.25 1.75a.75.75 0 1 1-1.5 0 .75.75 0 0 1 1.5 0Zm2.25.75A.75.75 0 1 0 10 9a.75.75 0 0 0 0 1.5Zm3.76-.75a.75.75 0 1 1-1.5 0 .75.75 0 0 1 1.5 0ZM8.5 8a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5ZM2 5.5C2 4.67 2.67 4 3.5 4h13c.83 0 1.5.67 1.5 1.5v8c0 .83-.67 1.5-1.5 1.5h-13A1.5 1.5 0 0 1 2 13.5v-8ZM3.5 5a.5.5 0 0 0-.5.5v8c0 .28.22.5.5.5h13a.5.5 0 0 0 .5-.5v-8a.5.5 0 0 0-.5-.5h-13Z";

    /// <summary>Clean-up passes. <c>text_grammar_wand_20_regular</c>.</summary>
    public const string CleanUp =
        "F1 M2.5 5a.5.5 0 0 0 0 1h15a.5.5 0 0 0 0-1h-15Zm0 3a.5.5 0 0 0 0 1h9a.5.5 0 0 0 0-1h-9Zm0 3a.5.5 0 0 0 0 1h6.88a1.5 1.5 0 0 1-.37-.99V11H2.5Zm0 3h6.38l-1 1H2.5a.5.5 0 0 1 0-1ZM14 7a.5.5 0 0 1 .5.44l.1.79a2.5 2.5 0 0 0 2.17 2.18l.8.1a.5.5 0 0 1 0 .99l-.8.1a2.5 2.5 0 0 0-2.18 2.17l-.1.8a.5.5 0 0 1-.99 0l-.1-.8a2.5 2.5 0 0 0-2.17-2.18l-.8-.1a.5.5 0 0 1 0-.99l.8-.1a2.5 2.5 0 0 0 2.18-2.17l.1-.8A.5.5 0 0 1 14 7Zm0 2.6a3.5 3.5 0 0 1-1.4 1.4c.59.32 1.08.81 1.4 1.4a3.5 3.5 0 0 1 1.4-1.4A3.5 3.5 0 0 1 14 9.6Zm-2.15 3.55c.2.2.2.5 0 .7l-4 4a.5.5 0 0 1-.7-.7l4-4c.2-.2.5-.2.7 0Z";

    /// <summary>Privacy and data. <c>shield_lock_20_regular</c>.</summary>
    public const string Privacy =
        "F1 M9.72 2.08a.5.5 0 0 1 .56 0c1.94 1.3 4.03 2.1 6.3 2.43A.5.5 0 0 1 17 5v4.34c-.26-.38-.6-.7-1-.94V5.43a15.97 15.97 0 0 1-5.6-2.08L10 3.1l-.4.25A15.97 15.97 0 0 1 4 5.43V9.5c0 3.4 1.97 5.86 6 7.46V17a2 2 0 0 0 .24.94l-.06.03a.5.5 0 0 1-.36 0C5.31 16.23 3 13.39 3 9.5V5a.5.5 0 0 1 .43-.5 15.05 15.05 0 0 0 6.3-2.42ZM12.5 12v-1a2 2 0 1 1 4 0v1h.5a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1h.5Zm1-1v1h2v-1a1 1 0 1 0-2 0Zm1.75 4a.75.75 0 1 0-1.5 0 .75.75 0 0 0 1.5 0Z";

    /// <summary>About. <c>info_20_regular</c>.</summary>
    public const string Info =
        "F1 M10.5 8.91a.5.5 0 0 0-1 .09v4.6a.5.5 0 0 0 1-.1V8.91Zm.3-2.16a.75.75 0 1 0-1.5 0 .75.75 0 0 0 1.5 0ZM18 10a8 8 0 1 0-16 0 8 8 0 0 0 16 0ZM3 10a7 7 0 1 1 14 0 7 7 0 0 1-14 0Z";

    /// <summary>The API key. <c>key_20_regular</c>.</summary>
    public const string Key =
        "F1 M15 6a1 1 0 1 1-2 0 1 1 0 0 1 2 0Zm-2.5-4a5.45 5.45 0 0 0-5.38 6.67c.06.27 0 .5-.14.64l-4.54 4.54A1.5 1.5 0 0 0 2 14.91v1.59c0 .83.67 1.5 1.5 1.5h2c.83 0 1.5-.67 1.5-1.5V16h1a1 1 0 0 0 1-1v-1h1a1 1 0 0 0 1-1v-.18c.5.13 1 .18 1.5.18 3.08 0 5.5-2.42 5.5-5.5S15.58 2 12.5 2ZM8 7.5C8 4.98 9.98 3 12.5 3S17 4.98 17 7.5 15.02 12 12.5 12c-.66 0-1.27-.1-1.78-.35a.5.5 0 0 0-.72.45v.9H9a1 1 0 0 0-1 1v1H7a1 1 0 0 0-1 1v.5a.5.5 0 0 1-.5.5h-2a.5.5 0 0 1-.5-.5v-1.59a.5.5 0 0 1 .15-.35l4.54-4.54c.43-.43.52-1.04.4-1.56-.06-.3-.09-.63-.09-.96Z";

    /// <summary>Test connection. <c>plug_connected_20_regular</c>.</summary>
    public const string PlugConnected =
        "F1 M17.85 2.85a.5.5 0 0 0-.7-.7L14.48 4.8a4.04 4.04 0 0 0-5.33.34l-.3.3a1.49 1.49 0 0 0 0 2.1l3.6 3.6c.58.59 1.52.59 2.1 0l.3-.3a4.04 4.04 0 0 0 .34-5.33l2.66-2.67Zm-4 7.6c-.2.19-.5.19-.7 0l-3.6-3.6c-.19-.2-.19-.5 0-.7l.3-.3a3.04 3.04 0 0 1 4.3 4.3l-.3.3Zm-6.3-1.6a1.49 1.49 0 0 0-2.1 0l-.3.3a4.04 4.04 0 0 0-.34 5.33l-2.66 2.67a.5.5 0 0 0 .7.7l2.67-2.66a4.04 4.04 0 0 0 5.33-.34l.3-.3c.59-.58.59-1.52 0-2.1l-3.6-3.6Zm-1.4.7c.2-.19.5-.19.7 0l3.6 3.6c.19.2.19.5 0 .7l-.3.3a3.04 3.04 0 1 1-4.3-4.3l.3-.3Z";

    /// <summary>A check passed. <c>checkmark_circle_20_regular</c>.</summary>
    public const string CheckmarkCircle =
        "F1 M10 2a8 8 0 1 1 0 16 8 8 0 0 1 0-16Zm0 1a7 7 0 1 0 0 14 7 7 0 0 0 0-14Zm3.36 4.65c.17.17.2.44.06.63l-.06.07-4 4a.5.5 0 0 1-.64.07l-.07-.06-2-2a.5.5 0 0 1 .63-.77l.07.06L9 11.3l3.65-3.65c.2-.2.51-.2.7 0Z";

    /// <summary>Done. <c>checkmark_circle_20_filled</c>.</summary>
    public const string CheckmarkCircleFilled =
        "F1 M10 2a8 8 0 1 1 0 16 8 8 0 0 1 0-16Zm3.36 5.65a.5.5 0 0 0-.64-.06l-.07.06L9 11.3 7.35 9.65l-.07-.06a.5.5 0 0 0-.7.7l.07.07 2 2 .07.06c.17.11.4.11.56 0l.07-.06 4-4 .07-.08a.5.5 0 0 0-.06-.63Z";

    /// <summary>Cancel. <c>dismiss_20_regular</c>.</summary>
    public const string Dismiss =
        "F1 m4.09 4.22.06-.07a.5.5 0 0 1 .63-.06l.07.06L10 9.29l5.15-5.14a.5.5 0 0 1 .63-.06l.07.06c.18.17.2.44.06.63l-.06.07L10.71 10l5.14 5.15c.18.17.2.44.06.63l-.06.07a.5.5 0 0 1-.63.06l-.07-.06L10 10.71l-5.15 5.14a.5.5 0 0 1-.63.06l-.07-.06a.5.5 0 0 1-.06-.63l.06-.07L9.29 10 4.15 4.85a.5.5 0 0 1-.06-.63l.06-.07-.06.07Z";

    /// <summary>A check failed. <c>dismiss_circle_20_regular</c>.</summary>
    public const string DismissCircle =
        "F1 M10 2a8 8 0 1 1 0 16 8 8 0 0 1 0-16Zm0 1a7 7 0 1 0 0 14 7 7 0 0 0 0-14ZM7.8 7.11l.08.06L10 9.3l2.12-2.12a.5.5 0 0 1 .64-.06l.07.06c.17.18.2.44.06.64l-.06.07L10.7 10l2.12 2.12c.17.17.2.44.06.64l-.06.07a.5.5 0 0 1-.64.06l-.07-.06L10 10.7l-2.12 2.12a.5.5 0 0 1-.64.06l-.07-.06a.5.5 0 0 1-.06-.64l.06-.07L9.3 10 7.17 7.88a.5.5 0 0 1-.06-.64l.06-.07a.5.5 0 0 1 .64-.06Z";

    /// <summary>Attention. <c>warning_20_regular</c>.</summary>
    public const string Warning =
        "F1 M9.56 3.26a.5.5 0 0 1 .88 0l6.5 12a.5.5 0 0 1-.44.74h-13a.5.5 0 0 1-.44-.74l6.5-12Zm1.76-.47a1.5 1.5 0 0 0-2.64 0l-6.5 12A1.5 1.5 0 0 0 3.5 17h13a1.5 1.5 0 0 0 1.32-2.21l-6.5-12ZM10.5 7.5a.5.5 0 0 0-1 0v4a.5.5 0 1 0 1 0v-4Zm.25 6.25a.75.75 0 1 1-1.5 0 .75.75 0 0 1 1.5 0Z";

    /// <summary>Search. <c>search_20_regular</c>.</summary>
    public const string Search =
        "F1 M12.73 13.44a6.5 6.5 0 1 1 .7-.7l3.42 3.4a.5.5 0 0 1-.63.77l-.07-.06-3.42-3.41Zm-.71-.71A5.54 5.54 0 0 0 14 8.5a5.5 5.5 0 1 0-1.98 4.23Z";

    /// <summary>Welcome. <c>hand_wave_20_regular</c>.</summary>
    public const string HandWave =
        "F1 M15.85 1.14a.5.5 0 0 0-.7 0 .5.5 0 0 0 0 .72 6.57 6.57 0 0 1 1.86 5.56.5.5 0 0 0 .42.58.5.5 0 0 0 .56-.42 7.6 7.6 0 0 0-2.14-6.44ZM4.63 3.04a1.6 1.6 0 0 1 3.04-.63 1.6 1.6 0 0 1 2.59.7l.08.27a1.6 1.6 0 0 1 2.55.73l1.07 3.23.58 1.7a8.5 8.5 0 0 1 .38 3.9l-.27 2a2.5 2.5 0 0 1-1.51 1.96l-2.16.9c-.97.4-2.07.15-2.8-.55-3.12-2.99-5.88-3.97-6.65-4.2-.35-.11-.66-.52-.47-.98.15-.35.51-.97 1.28-1.32.6-.26 1.36-.33 2.35-.06L2.77 5.1a1.61 1.61 0 0 1 1.02-2.03c.28-.09.57-.1.84-.04Zm2.15 3.64.55 1.68a.5.5 0 1 1-.95.31l-.57-1.72-.95-2.52a.6.6 0 0 0-.76-.4.61.61 0 0 0-.38.76l2.25 6.55a.5.5 0 0 1-.67.62c-1.33-.57-2.11-.49-2.55-.3-.3.14-.5.34-.62.53a18.9 18.9 0 0 1 6.74 4.33c.47.46 1.15.6 1.72.36l2.16-.9c.5-.2.84-.66.9-1.18l.28-1.99a7.5 7.5 0 0 0-.34-3.44l-.58-1.7v-.01l-1.07-3.24a.6.6 0 0 0-1.16.3l.85 2.62a.5.5 0 0 1-.95.31L9.85 5.1a1.63 1.63 0 0 1-.04-.13l-.5-1.56a.6.6 0 0 0-1.15.38l.6 1.85a.5.5 0 0 1 .03.11l.53 1.6a.5.5 0 1 1-.95.3L6.79 2.92a.6.6 0 0 0-1.13.38l1.1 3.33.02.06Zm7.46-3.61a.5.5 0 0 1 .69.17l.3.5c.5.83.76 1.78.77 2.75a.5.5 0 1 1-1 .01c0-.79-.22-1.57-.63-2.25l-.3-.5a.5.5 0 0 1 .17-.68Z";

    /// <summary>Get the offline model. <c>arrow_download_20_regular</c>.</summary>
    public const string ArrowDownload =
        "F1 M15.5 17a.5.5 0 0 1 .09 1H4.5a.5.5 0 0 1-.09-1H15.5ZM10 2a.5.5 0 0 1 .5.41V14.3l3.64-3.65a.5.5 0 0 1 .64-.06l.07.06c.17.17.2.44.06.63l-.06.07-4.5 4.5a.5.5 0 0 1-.25.14L10 16a.5.5 0 0 1-.4-.2l-4.46-4.45a.5.5 0 0 1 .64-.76l.07.06 3.65 3.64V2.5c0-.27.22-.5.5-.5Z";

    /// <summary>Suggestions. <c>sparkle_20_regular</c>.</summary>
    public const string Sparkle =
        "F1 M7.4 12.8a1.04 1.04 0 0 0 1.59-.51l.45-1.37a2.34 2.34 0 0 1 1.47-1.48l1.4-.45A1.04 1.04 0 0 0 12.25 7l-1.37-.45A2.34 2.34 0 0 1 9.4 5.08L8.95 3.7a1.03 1.03 0 0 0-.82-.68 1.04 1.04 0 0 0-1.15.7l-.46 1.4a2.34 2.34 0 0 1-1.44 1.45L3.7 7a1.04 1.04 0 0 0 .02 1.97l1.37.45a2.33 2.33 0 0 1 1.48 1.48l.46 1.4c.07.2.2.37.38.5Zm.08-7.4.53-1.38.44 1.37a3.33 3.33 0 0 0 2.12 2.12l1.4.53-1.38.45a3.34 3.34 0 0 0-2.11 2.11l-.53 1.38-.45-1.38a3.34 3.34 0 0 0-2.1-2.12L4 7.96 5.4 7.5a3.36 3.36 0 0 0 2.08-2.12Zm6.06 11.45a.8.8 0 0 0 1.22-.4l.25-.76a1.09 1.09 0 0 1 .68-.68l.77-.25a.8.8 0 0 0-.02-1.52l-.77-.25a1.08 1.08 0 0 1-.68-.68l-.25-.77a.8.8 0 0 0-1.52.01l-.24.76a1.1 1.1 0 0 1-.67.68l-.77.25a.8.8 0 0 0 0 1.52l.77.25a1.09 1.09 0 0 1 .68.68l.25.77c.06.16.16.3.3.4Zm-.92-2.8-.18-.05.19-.06a2.09 2.09 0 0 0 1.3-1.32l.06-.18.05.18a2.08 2.08 0 0 0 1.32 1.32l.2.06-.18.06a2.08 2.08 0 0 0-1.32 1.32l-.06.18-.06-.18a2.07 2.07 0 0 0-1.32-1.32Z";

    /// <summary>Next. <c>arrow_right_20_regular</c>.</summary>
    public const string ArrowRight =
        "F1 M10.84 3.13a.5.5 0 0 0-.68.74l6.17 5.63H2.5a.5.5 0 0 0 0 1h13.83l-6.17 5.63a.5.5 0 0 0 .68.74l6.91-6.32a.75.75 0 0 0 0-1.1l-6.91-6.32Z";

    /// <summary>Typing into the app in focus. <c>cursor_click_20_regular</c>.</summary>
    public const string CursorClick =
        "F1 M7.5 2c.28 0 .5.22.5.5v2a.5.5 0 0 1-1 0v-2c0-.28.22-.5.5-.5ZM3.61 3.61c.2-.2.51-.2.7 0l1.42 1.42a.5.5 0 1 1-.7.7L3.6 4.32a.5.5 0 0 1 0-.7Zm7.78 0c.2.2.2.51 0 .7L9.97 5.74a.5.5 0 1 1-.7-.7l1.41-1.42c.2-.2.51-.2.7 0ZM2 7.5c0-.28.22-.5.5-.5h2a.5.5 0 0 1 0 1h-2a.5.5 0 0 1-.5-.5Zm6.64-.2A1 1 0 0 0 7 8.07v9.1a1 1 0 0 0 1.75.66l2.03-2.32c.28-.32.7-.51 1.13-.51h3.2a1 1 0 0 0 .65-1.77L8.64 7.3ZM8 17.17v-9.1L15.12 14H11.9c-.72 0-1.4.31-1.88.85L8 17.17Z";

    /// <summary>Back. <c>arrow_left_20_regular</c>.</summary>
    public const string ArrowLeft =
        "F1 M9.16 16.87a.5.5 0 1 0 .67-.74L3.67 10.5H17.5a.5.5 0 0 0 0-1H3.67l6.16-5.63a.5.5 0 0 0-.67-.74L2.24 9.44a.75.75 0 0 0 0 1.11l6.92 6.32Z";
}
