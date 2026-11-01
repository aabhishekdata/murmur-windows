using System.Security.Cryptography;
using System.Text;
using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>
/// Stores secrets as encrypted files, one per name, in <c>%LocalAppData%\Murmur\secrets</c>.
/// </summary>
/// <remarks>
/// <para>
/// The encryption is whatever <see cref="IDataProtector"/> the app is given — DPAPI for the
/// current user on Windows — so the file is useless copied to another account or another PC.
/// Nothing here is Windows-specific, which is what lets it be tested anywhere with a fake.
/// </para>
/// <para>
/// <b>Writes are atomic</b>, through a temporary file moved over the real one: a crash halfway
/// must leave the old key or the new key, never a truncated blob that decrypts to nothing.
/// </para>
/// <para>
/// <b>An unreadable file is not an error.</b> A profile restored onto a new machine, a Windows
/// password reset by an administrator, a file damaged on disk — DPAPI refuses all of them, and
/// the right response to every one is to ask for the key again, not to crash. <see cref="Read"/>
/// answers null and <see cref="Contains"/> still answers true, which is how the UI tells "no
/// key yet" from "enter your key again".
/// </para>
/// </remarks>
public sealed class ProtectedSecretFile : ISecretStore
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _directory;
    private readonly IDataProtector _protector;
    private readonly Lock _lock = new();

    /// <summary>Stores secrets in <paramref name="directory"/>.</summary>
    /// <param name="directory">The folder; created on first write.</param>
    /// <param name="protector">Encrypts and decrypts the files.</param>
    public ProtectedSecretFile(string directory, IDataProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(protector);

        _directory = directory;
        _protector = protector;
    }

    /// <summary>The default folder.</summary>
    public static string DefaultDirectory => MurmurPaths.In("secrets");

    /// <summary>Where the secret called <paramref name="name"/> is kept.</summary>
    /// <param name="name">Letters, digits and dashes only.</param>
    /// <returns>The file path.</returns>
    public string PathFor(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // A name is part of a path, so it is held to a shape that cannot climb out of the folder.
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("A secret's name may contain only letters, digits and dashes.", nameof(name));
        }

        return Path.Combine(_directory, name + ".bin");
    }

    /// <inheritdoc />
    public string? Read(string name)
    {
        var path = PathFor(name);

        lock (_lock)
        {
            byte[] blob;
            try
            {
                if (!File.Exists(path)) return null;
                blob = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            byte[]? plain = null;
            try
            {
                plain = _protector.Unprotect(blob);
                var value = Strict.GetString(plain);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException or ArgumentException)
            {
                return null;
            }
            finally
            {
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    /// <inheritdoc />
    public void Write(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var path = PathFor(name);

        var plain = Strict.GetBytes(value);
        try
        {
            var blob = _protector.Protect(plain);

            lock (_lock)
            {
                Directory.CreateDirectory(_directory);

                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, blob);
                File.Move(temporary, path, overwrite: true);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <inheritdoc />
    public void Remove(string name)
    {
        var path = PathFor(name);

        lock (_lock)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <inheritdoc />
    public bool Contains(string name)
    {
        var path = PathFor(name);

        lock (_lock) return File.Exists(path);
    }
}
