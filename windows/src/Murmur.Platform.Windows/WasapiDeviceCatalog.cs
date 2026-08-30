using System.Runtime.InteropServices;
using Murmur.Abstractions;
using NAudio.CoreAudioApi;

namespace Murmur.Platform.Windows;

/// <summary>
/// Lists the machine's capture devices through WASAPI.
/// </summary>
/// <remarks>
/// <para>
/// The "default" flag reports the default <b>communications</b> endpoint, not the console one.
/// Those are two separate settings in Windows and they routinely disagree — a headset is
/// usually the communications default while the built-in array stays the console default. This
/// app opens the communications endpoint when no device has been chosen, so this is the flag
/// that describes what will actually be recorded from, and showing the other one would be a lie
/// that costs somebody an afternoon.
/// </para>
/// <para>
/// Never throws. A machine with no microphone, a driver mid-reinstall and a session with no
/// audio endpoint at all are all normal states for a dictation app to start up in, and the
/// picker showing "System default" alone is the correct outcome for every one of them.
/// </para>
/// </remarks>
public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
{
    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceInfo> List()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            var defaultId = DefaultCaptureId(enumerator);
            var devices = new List<AudioDeviceInfo>();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    // FriendlyName reads the property store, which can fail for a device that
                    // is disappearing as we walk the list. One bad device must not empty the
                    // picker.
                    string name;
                    try
                    {
                        name = device.FriendlyName;
                    }
                    catch (COMException)
                    {
                        continue;
                    }

                    devices.Add(new AudioDeviceInfo(
                        device.ID, name, string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
                }
            }

            return devices;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    private static string? DefaultCaptureId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device.ID;
        }
        catch (COMException)
        {
            // No default endpoint at all — a machine with no microphone.
            return null;
        }
    }
}
