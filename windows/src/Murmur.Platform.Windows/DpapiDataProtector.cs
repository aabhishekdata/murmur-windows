using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Murmur.Abstractions;

namespace Murmur.Platform.Windows;

/// <summary>
/// DPAPI, for the current user: <c>CryptProtectData</c> and <c>CryptUnprotectData</c>.
/// </summary>
/// <remarks>
/// <para>
/// A binding and nothing more. Where the blob is kept, what happens when it cannot be read and
/// what is shown to the user all live in <c>Murmur.Core.ProtectedSecretFile</c>, which CI tests
/// with a fake; this file is only the two calls CI cannot make meaningfully.
/// </para>
/// <para>
/// CurrentUser scope (no <c>CRYPTPROTECT_LOCAL_MACHINE</c>), so another account on the same PC
/// cannot decrypt the key. <c>CRYPTPROTECT_UI_FORBIDDEN</c>, so a tray app never pops a dialog.
/// The fixed entropy means a blob written by some other program for this user does not decrypt
/// as ours, and ours does not decrypt as theirs.
/// </para>
/// </remarks>
public sealed class DpapiDataProtector : IDataProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    private static readonly byte[] Entropy = "Murmur/azure-key/v1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB dataIn,
        string? description,
        ref DATA_BLOB entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB dataIn,
        IntPtr description,
        ref DATA_BLOB entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Call(plaintext, protect: true);

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Call(protectedData, protect: false);

    private static byte[] Call(ReadOnlySpan<byte> input, bool protect)
    {
        var inputBlob = Copy(input);
        var entropyBlob = Copy(Entropy);
        var output = default(DATA_BLOB);

        try
        {
            var ok = protect
                ? CryptProtectData(ref inputBlob, "Murmur", ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);

            if (!ok) throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Free(inputBlob, wipe: true);
            Free(entropyBlob, wipe: false);

            if (output.Data != IntPtr.Zero)
            {
                // The decrypted key, when unprotecting: zeroed before the memory goes back.
                Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
    }

    private static DATA_BLOB Copy(ReadOnlySpan<byte> bytes)
    {
        var array = bytes.ToArray();
        var blob = new DATA_BLOB { Size = array.Length, Data = Marshal.AllocHGlobal(Math.Max(1, array.Length)) };
        Marshal.Copy(array, 0, blob.Data, array.Length);
        CryptographicOperations.ZeroMemory(array);
        return blob;
    }

    private static void Free(DATA_BLOB blob, bool wipe)
    {
        if (blob.Data == IntPtr.Zero) return;
        if (wipe) Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
    }
}
