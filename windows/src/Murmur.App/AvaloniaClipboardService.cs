using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Murmur.Abstractions;

namespace Murmur.App;

/// <summary>
/// The clipboard, through Avalonia, on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia reaches the clipboard through a window, so this borrows the app's main window — the
/// floating bar, which exists for the life of the process whether or not it is showing. Every
/// call is marshalled to the UI thread, because the injector calls from the dictation thread or
/// the hotkey thread and the Win32 clipboard is not safe to touch from either.
/// </para>
/// <para>
/// Before the app has a window there is no clipboard, and the injector falls back to typing.
/// </para>
/// <para>
/// Everything written here is transient — a paste in progress, or the user's own text being put
/// back after it — so it is marked to stay out of Win+V history and cloud clipboard sync. A saved
/// prompt pasted twenty times a day would otherwise bury everything the user copied themselves,
/// and dictated text would sync to every device signed in to the account.
/// </para>
/// </remarks>
internal sealed class AvaloniaClipboardService : IClipboardService
{
    /// <summary>
    /// The registered clipboard formats Windows reads to decide what a copy may do. Each is
    /// documented under "Cloud Clipboard and Clipboard History Formats" as a DWORD where zero
    /// means "not into history" and "not to the cloud".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Platform formats, so the names reach <c>RegisterClipboardFormat</c> exactly as written.
    /// On other platforms they are inert extra formats nobody reads.
    /// </para>
    /// <para>
    /// <c>ExcludeClipboardContentFromMonitorProcessing</c> is deliberately not set. It hides the
    /// copy from every clipboard listener, including the ones that carry it into a Remote
    /// Desktop session or a VM, so Ctrl+V there would paste the remote side's stale clipboard
    /// instead of the dictation.
    /// </para>
    /// </remarks>
    internal static readonly DataFormat<byte[]> CanIncludeInHistory =
        DataFormat.CreateBytesPlatformFormat("CanIncludeInClipboardHistory");

    /// <inheritdoc cref="CanIncludeInHistory"/>
    internal static readonly DataFormat<byte[]> CanUploadToCloud =
        DataFormat.CreateBytesPlatformFormat("CanUploadToCloudClipboard");

    /// <inheritdoc />
    public Task<string?> GetTextAsync(CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Clipboard() is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        });

    /// <inheritdoc />
    public Task<bool> SetTextAsync(string? text, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Clipboard() is not { } clipboard) return false;

            if (text is null) await clipboard.ClearAsync();
            else await clipboard.SetDataAsync(Transient(text));
            return true;
        });

    /// <summary>
    /// <paramref name="text"/> as clipboard data that Windows keeps out of history and cloud sync.
    /// </summary>
    internal static DataTransfer Transient(string text)
    {
        var item = new DataTransferItem();
        item.SetText(text);

        // A fresh array per format: the platform may hold on to what it is given until the
        // clipboard changes again, and one shared buffer is one accidental write from leaking.
        item.Set(CanIncludeInHistory, new byte[4]);
        item.Set(CanUploadToCloud, new byte[4]);

        var data = new DataTransfer();
        data.Add(item);
        return data;
    }

    private static IClipboard? Clipboard() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? (desktop.MainWindow ?? (desktop.Windows.Count > 0 ? desktop.Windows[0] : null))?.Clipboard
            : null;
}
