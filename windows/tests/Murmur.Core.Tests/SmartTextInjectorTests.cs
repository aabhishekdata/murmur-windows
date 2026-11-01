using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Typing or pasting, the clipboard put back, and the user's fingers on the modifiers.</summary>
public sealed class SmartTextInjectorTests
{
    private const int LeftShift = 0xA0;
    private const int RightCtrl = 0xA3;
    private const int LeftAlt = 0xA4;

    /// <summary>No real waiting anywhere, but the same number of modifier polls as the defaults.</summary>
    private static readonly SmartTextInjectorOptions Instant = new()
    {
        ChunkGap = TimeSpan.Zero,
        ModifierReleaseWait = TimeSpan.FromMilliseconds(4),
        ModifierPollInterval = TimeSpan.FromMilliseconds(1),
        ClipboardSettle = TimeSpan.Zero,
        RestoreDelay = TimeSpan.Zero,
    };

    private static (SmartTextInjector Injector, FakeKeystrokeSender Keys, FakeClipboard Clipboard) Build(
        SmartTextInjectorOptions? options = null)
    {
        var log = new KeyboardLog();
        var keys = new FakeKeystrokeSender(log);
        var clipboard = new FakeClipboard(log) { Text = "what the user copied" };
        return (new SmartTextInjector(keys, clipboard, options ?? Instant), keys, clipboard);
    }

    [Fact]
    public async Task Short_single_line_text_is_typed_and_the_clipboard_left_alone()
    {
        var (injector, keys, clipboard) = Build();

        (await injector.InjectAsync("Hello there", CancellationToken.None)).ShouldBeTrue();

        keys.Log.Entries.ShouldBe(["type:Hello there"]);
        clipboard.Text.ShouldBe("what the user copied");
    }

    [Fact]
    public async Task Typing_is_chunked_without_splitting_a_surrogate_pair()
    {
        var (injector, keys, _) = Build(Instant with { ChunkSize = 4 });

        await injector.InjectAsync("abc\U0001F642def", CancellationToken.None);

        keys.Typed.ShouldBe(["abc\U0001F642", "def"]);
        string.Concat(keys.Typed).ShouldBe("abc\U0001F642def");
    }

    [Theory]
    [InlineData("Line one\nLine two", false)]
    [InlineData("short", true)]
    public async Task Multi_line_or_verbatim_text_is_pasted_then_the_clipboard_restored(string text, bool verbatim)
    {
        var (injector, keys, clipboard) = Build();

        (await injector.InjectAsync(text, verbatim, CancellationToken.None)).ShouldBeTrue();

        var pasted = text.ReplaceLineEndings("\r\n");
        keys.Log.Entries.ShouldBe(["get", "set:" + pasted, "paste", "get", "set:what the user copied"]);
        clipboard.Text.ShouldBe("what the user copied");
    }

    [Fact]
    public async Task Long_text_is_pasted()
    {
        var (injector, keys, _) = Build();

        await injector.InjectAsync(new string('x', 201), CancellationToken.None);

        keys.Log.Entries.ShouldContain("paste");
        keys.Typed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_clipboard_without_text_is_never_emptied()
    {
        // No text can mean an image or files: emptying the clipboard "back" would destroy them.
        var (injector, keys, clipboard) = Build();
        clipboard.Text = null;

        await injector.InjectAsync("prompt", verbatim: true, CancellationToken.None);

        keys.Log.Entries.ShouldBe(["get", "set:prompt", "paste"]);
        clipboard.Text.ShouldBe("prompt");
    }

    [Fact]
    public async Task Cancelling_after_the_clipboard_was_written_still_restores_it()
    {
        var (injector, keys, clipboard) = Build(Instant with { ClipboardSettle = TimeSpan.FromSeconds(30) });
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await injector.InjectAsync("prompt", verbatim: true, cancel.Token));

        keys.Log.Entries.ShouldBe(["get", "set:prompt", "get", "set:what the user copied"]);
        clipboard.Text.ShouldBe("what the user copied");
    }

    [Fact]
    public async Task Overlapping_injections_take_turns_on_the_clipboard()
    {
        // Dictation and a prompt's shortcut share one injector; neither may paste the other's
        // text, or put the other's text "back".
        var (injector, keys, clipboard) = Build(Instant with
        {
            ClipboardSettle = TimeSpan.FromMilliseconds(20),
            RestoreDelay = TimeSpan.FromMilliseconds(20),
        });

        var first = injector.InjectAsync("first", verbatim: true, CancellationToken.None).AsTask();
        var second = injector.InjectAsync("second", verbatim: true, CancellationToken.None).AsTask();
        var enter = injector.PressEnterAsync(CancellationToken.None).AsTask();
        await Task.WhenAll(first, second, enter);

        keys.Log.Entries.ShouldBe(
        [
            "get", "set:first", "paste", "get", "set:what the user copied",
            "get", "set:second", "paste", "get", "set:what the user copied",
            "enter",
        ]);
        clipboard.Text.ShouldBe("what the user copied");
    }

    [Fact]
    public async Task Something_copied_during_the_paste_is_not_overwritten()
    {
        var (injector, keys, clipboard) = Build();

        // The second read is the restore check; the user copied something just before it.
        clipboard.BeforeRead = (c, reads) =>
        {
            if (reads == 1) c.Text = "copied meanwhile";
        };

        await injector.InjectAsync("prompt", verbatim: true, CancellationToken.None);

        clipboard.Text.ShouldBe("copied meanwhile");
        keys.Log.Entries.ShouldBe(["get", "set:prompt", "paste", "get"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clipboard_that_cannot_be_written_falls_back_to_typing_with_real_enters(bool throws)
    {
        var (injector, keys, clipboard) = Build();
        clipboard.RefuseWrites = !throws;
        clipboard.ThrowOnWrite = throws;

        (await injector.InjectAsync("Line one\nLine two", CancellationToken.None)).ShouldBeTrue();

        keys.Log.Entries.ShouldNotContain("paste");
        keys.Log.Entries.Where(e => e is "enter" || e.StartsWith("type:", StringComparison.Ordinal))
            .ShouldBe(["type:Line one", "enter", "type:Line two"]);
    }

    [Fact]
    public async Task Without_a_clipboard_everything_is_typed()
    {
        var keys = new FakeKeystrokeSender();
        var injector = new SmartTextInjector(keys, clipboard: null, options: Instant);

        await injector.InjectAsync("A\r\nB", verbatim: true, CancellationToken.None);

        keys.Log.Entries.ShouldBe(["type:A", "enter", "type:B"]);
    }

    [Fact]
    public async Task Modifiers_let_go_while_waiting_are_not_touched()
    {
        var (injector, keys, _) = Build();
        keys.ThenHeld(RightCtrl, LeftAlt).ThenHeld(RightCtrl).ThenHeld();

        await injector.InjectAsync("Hello", CancellationToken.None);

        keys.HeldReads.ShouldBe(3);
        keys.Log.Entries.ShouldBe(["type:Hello"]);
    }

    [Fact]
    public async Task Modifiers_still_held_are_lifted_around_the_paste_and_pressed_again()
    {
        var (injector, keys, _) = Build();
        keys.HeldAfterScript = [LeftShift, RightCtrl];

        await injector.InjectAsync("prompt", verbatim: true, CancellationToken.None);

        keys.Log.Entries.ShouldBe(
        [
            "get", "set:prompt",
            "release:A0", "release:A3",
            "paste",
            "press:A0", "press:A3",
            "get", "set:what the user copied",
        ]);
    }

    [Fact]
    public async Task The_wait_for_modifiers_is_bounded()
    {
        var (injector, keys, _) = Build(Instant with
        {
            ModifierReleaseWait = TimeSpan.FromMilliseconds(400),
            ModifierPollInterval = TimeSpan.FromMilliseconds(20),
        });
        keys.HeldAfterScript = [LeftAlt];

        await injector.InjectAsync("Hi", CancellationToken.None);

        // One look before waiting, then one per 20 ms poll across 400 ms.
        keys.HeldReads.ShouldBe(21);
        keys.Log.Entries.ShouldBe(["release:A4", "type:Hi", "press:A4"]);
    }

    [Fact]
    public async Task Enter_lifts_held_modifiers_without_waiting()
    {
        var (injector, keys, _) = Build();
        keys.HeldAfterScript = [LeftShift];

        (await injector.PressEnterAsync(CancellationToken.None)).ShouldBeTrue();

        keys.HeldReads.ShouldBe(1);
        keys.Log.Entries.ShouldBe(["release:A0", "enter", "press:A0"]);
    }

    [Fact]
    public async Task Nothing_is_sent_for_empty_text()
    {
        var (injector, keys, _) = Build();

        (await injector.InjectAsync(string.Empty, verbatim: true, CancellationToken.None)).ShouldBeTrue();

        keys.Log.Entries.ShouldBeEmpty();
    }
}
