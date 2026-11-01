using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using Murmur.App;
using Murmur.App.Design;
using Shouldly;
using Xunit;

namespace Murmur.AppTests;

/// <summary>
/// The UI platform underneath the views: the Avalonia line, the control theme, and the
/// clipboard writes that must stay out of Windows' clipboard history.
/// </summary>
public sealed class PlatformTests
{
    [Fact]
    public void Every_Avalonia_assembly_the_app_is_built_against_is_from_one_major_version()
    {
        // Mixing an 11.x assembly into a 12.x app compiles and then fails at run time with a
        // missing method, so a partial upgrade has to be caught here rather than on a user's
        // machine. 12 is the line whose render loop sleeps when nothing is animating.
        var avalonia = typeof(Murmur.App.App).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name is not null && a.Name.StartsWith("Avalonia", StringComparison.Ordinal))
            .ToList();

        avalonia.ShouldNotBeEmpty();
        avalonia.ShouldAllBe(a => a.Version!.Major == 12);
        typeof(Window).Assembly.GetName().Version!.Major.ShouldBe(12);
    }

    [AvaloniaFact]
    public void The_control_theme_is_FluentAvalonia_with_the_accent_and_theme_hooks_off()
    {
        var theme = AppTheme.Create().ShouldBeOfType<FluentAvaloniaTheme>();

        // The user's accent can be red, and red means recording, so the accent is applied by
        // AccentPolicy instead. Light, dark and high contrast are ThemeResources' job. The system
        // font is on: Segoe UI Variable is what makes it a Windows 11 window.
        theme.PreferUserAccentColor.ShouldBeFalse();
        theme.UseSystemFontOnWindows.ShouldBeTrue();
        theme.PreferSystemTheme.ShouldBeFalse();

        Application.Current!.Styles.OfType<FluentAvaloniaTheme>().ShouldHaveSingleItem();
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void With_no_system_accent_the_theme_draws_its_controls_in_Murmurs_indigo(string variant)
    {
        // The headless platform has no real accent (its placeholder reports Windows 10 blue as
        // if it were a choice), so this is the fallback: indigo, in both themes.
        var theme = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        Application.Current!.TryGetResource("SystemAccentColor", theme, out var accent).ShouldBeTrue();
        accent.ShouldBe(Color.FromRgb(0x5B, 0x5B, 0xD6));
        accent.ShouldBe(AccentPolicy.Fallback);
    }

    [Fact]
    public void Pasted_text_is_marked_to_stay_out_of_clipboard_history_and_cloud_sync()
    {
        var data = AvaloniaClipboardService.Transient("review this pull request");

        data.TryGetText().ShouldBe("review this pull request");

        // Windows reads each of these by name as a DWORD where zero means "no". They are
        // independent switches, so history and cloud sync are each closed explicitly.
        data.TryGetValue(AvaloniaClipboardService.CanIncludeInHistory).ShouldBe(new byte[4]);
        data.TryGetValue(AvaloniaClipboardService.CanUploadToCloud).ShouldBe(new byte[4]);

        AvaloniaClipboardService.CanIncludeInHistory.Identifier.ShouldBe("CanIncludeInClipboardHistory");
        AvaloniaClipboardService.CanUploadToCloud.Identifier.ShouldBe("CanUploadToCloudClipboard");

        // Not the monitor exclusion: that hides the copy from Remote Desktop and VM clipboard
        // redirection too, so Ctrl+V in a remote window would paste something stale.
        data.TryGetValue(DataFormat.CreateBytesPlatformFormat("ExcludeClipboardContentFromMonitorProcessing"))
            .ShouldBeNull();
    }

    [Fact]
    public void Each_paste_gets_its_own_buffers()
    {
        var first = AvaloniaClipboardService.Transient("one");
        var second = AvaloniaClipboardService.Transient("two");

        first.TryGetValue(AvaloniaClipboardService.CanIncludeInHistory)
            .ShouldNotBeSameAs(second.TryGetValue(AvaloniaClipboardService.CanIncludeInHistory));
    }

    [AvaloniaFact]
    public async Task Marked_text_round_trips_through_a_real_clipboard_as_ordinary_text()
    {
        // The markers are extra formats beside the text, not a replacement for it: whatever the
        // target app reads on Ctrl+V must still be the prompt.
        var window = new Window();
        window.Show();
        var clipboard = window.Clipboard.ShouldNotBeNull();

        await clipboard.SetDataAsync(AvaloniaClipboardService.Transient("line one\nline two"));

        (await clipboard.TryGetTextAsync()).ShouldBe("line one\nline two");
        (await clipboard.TryGetValueAsync(AvaloniaClipboardService.CanIncludeInHistory)).ShouldBe(new byte[4]);

        window.Close();
    }
}
