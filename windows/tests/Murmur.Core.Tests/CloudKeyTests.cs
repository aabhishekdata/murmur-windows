using System.Reflection;
using System.Text;
using System.Text.Json;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.Core.Tests;

/// <summary>Where the Azure key lives, and everywhere it must not.</summary>
public sealed class CloudKeyTests : IDisposable
{
    private const string Key = "8f3a1c9e5b7d4f2a6c0e9b8d7a6f5e4d";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"murmur-secrets-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void A_secret_round_trips_through_the_file_and_is_not_stored_in_the_clear()
    {
        var store = new ProtectedSecretFile(_directory, new FakeDataProtector());
        store.Write(CloudKey.SecretName, Key);

        var path = store.PathFor(CloudKey.SecretName);
        path.ShouldEndWith(Path.Combine(_directory, "azure-key.bin"));
        Encoding.UTF8.GetString(File.ReadAllBytes(path)).ShouldNotContain(Key);

        new ProtectedSecretFile(_directory, new FakeDataProtector()).Read(CloudKey.SecretName).ShouldBe(Key);
        File.Exists(path + ".tmp").ShouldBeFalse("the temporary file is moved over the real one");
    }

    [Fact]
    public void A_file_that_cannot_be_decrypted_reads_as_no_key_but_is_still_reported_present()
    {
        new ProtectedSecretFile(_directory, new FakeDataProtector()).Write(CloudKey.SecretName, Key);

        // Another user, another machine, or a restored profile: DPAPI refuses all of them.
        var elsewhere = new ProtectedSecretFile(_directory, new FakeDataProtector { FailUnprotect = true });
        elsewhere.Read(CloudKey.SecretName).ShouldBeNull();
        elsewhere.Contains(CloudKey.SecretName).ShouldBeTrue();

        new CloudKey(elsewhere).State.ShouldBe(CloudKeyState.Unreadable, "the UI asks for the key again");
    }

    [Fact]
    public void A_damaged_file_reads_as_no_key_rather_than_throwing()
    {
        var store = new ProtectedSecretFile(_directory, new FakeDataProtector());
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(store.PathFor(CloudKey.SecretName), [1, 2, 3]);

        store.Read(CloudKey.SecretName).ShouldBeNull();
    }

    [Fact]
    public void Removing_a_secret_deletes_the_file_and_removing_twice_is_harmless()
    {
        var store = new ProtectedSecretFile(_directory, new FakeDataProtector());
        store.Write(CloudKey.SecretName, Key);

        store.Remove(CloudKey.SecretName);
        store.Remove(CloudKey.SecretName);

        store.Contains(CloudKey.SecretName).ShouldBeFalse();
        store.Read(CloudKey.SecretName).ShouldBeNull();
    }

    [Theory]
    [InlineData("../azure-key")]
    [InlineData("a/b")]
    [InlineData("key.bin")]
    public void A_secret_name_cannot_climb_out_of_the_folder(string name)
    {
        var store = new ProtectedSecretFile(_directory, new FakeDataProtector());
        Should.Throw<ArgumentException>(() => store.PathFor(name));
    }

    [Fact]
    public void The_key_is_entered_once_and_remembered()
    {
        var store = new InMemorySecretStore();
        var key = new CloudKey(store);
        key.State.ShouldBe(CloudKeyState.Missing);

        var changes = 0;
        key.Changed += (_, _) => changes++;
        key.Save("  " + Key + "\n");

        key.Value.ShouldBe(Key, "whitespace from a paste is dropped");
        key.Masked.ShouldBe("•••• 5e4d");
        key.State.ShouldBe(CloudKeyState.Saved);
        changes.ShouldBe(1);

        new CloudKey(store).Value.ShouldBe(Key, "a later launch reads it back");
    }

    [Fact]
    public void The_key_is_read_from_the_store_once_and_then_held()
    {
        var protector = new FakeDataProtector();
        var store = new ProtectedSecretFile(_directory, protector);
        store.Write(CloudKey.SecretName, Key);

        var key = new CloudKey(store);
        for (var i = 0; i < 5; i++) key.Value.ShouldBe(Key);

        protector.UnprotectCalls.ShouldBe(1, "a dictation must not pay for a file read and a DPAPI call");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two keys")]
    public void Something_that_is_not_a_key_is_refused(string text)
    {
        var store = new InMemorySecretStore();
        Should.Throw<ArgumentException>(() => new CloudKey(store).Save(text));
        store.Contains(CloudKey.SecretName).ShouldBeFalse();
    }

    [Fact]
    public void A_rejection_is_remembered_until_a_new_key_or_a_request_gets_through()
    {
        var key = new CloudKey(new InMemorySecretStore());
        key.Save(Key);

        key.MarkRejected();
        key.State.ShouldBe(CloudKeyState.Rejected);

        key.MarkAccepted();
        key.State.ShouldBe(CloudKeyState.Saved);

        key.MarkRejected();
        key.Save(Key + "x");
        key.State.ShouldBe(CloudKeyState.Saved);

        key.Remove();
        key.State.ShouldBe(CloudKeyState.Missing);
        key.Masked.ShouldBeNull();
    }

    [Fact]
    public void An_outcome_for_a_key_that_has_since_been_replaced_is_ignored()
    {
        var key = new CloudKey(new InMemorySecretStore());
        key.Save(Key);
        key.Read(out var sent).ShouldBe(Key);

        // The user saves the right key while a request with the old one is still out.
        key.Save(Key + "x");
        key.MarkRejected(sent);
        key.State.ShouldBe(CloudKeyState.Saved, "the 401 was for the old key");

        key.MarkRejected(key.Generation);
        key.State.ShouldBe(CloudKeyState.Rejected);
        key.MarkAccepted(sent);
        key.State.ShouldBe(CloudKeyState.Rejected, "a success with the old key says nothing about the new one");
    }

    [Fact]
    public void The_state_is_worked_out_from_the_store_once_and_then_kept()
    {
        var store = new InMemorySecretStore();
        store.Corrupt(CloudKey.SecretName);
        var key = new CloudKey(store);

        for (var i = 0; i < 5; i++) key.State.ShouldBe(CloudKeyState.Unreadable);
        var lookups = store.Lookups;
        lookups.ShouldBeLessThanOrEqualTo(2, "one read, and one look for an unreadable file");

        key.Remove();
        for (var i = 0; i < 5; i++) key.State.ShouldBe(CloudKeyState.Missing);
        store.Lookups.ShouldBe(lookups, "the bar asks on every keystroke; the disk must not be");
    }

    [Fact]
    public void Printing_the_key_holder_never_prints_the_key()
    {
        var key = new CloudKey(new InMemorySecretStore());
        key.Save(Key);

        key.ToString().ShouldNotContain(Key);
        key.ToString().ShouldNotContain(Key[^4..]);
    }

    [Fact]
    public void Settings_json_never_contains_the_key_with_every_field_set()
    {
        // The key is saved the way the app saves it, beside the settings file...
        var secrets = new ProtectedSecretFile(Path.Combine(_directory, "secrets"), new FakeDataProtector());
        new CloudKey(secrets).Save(Key);

        // ...and every setting is given a value, so that a field added later which happened to
        // carry it would be caught here rather than in someone's settings.json.
        var data = EverythingSet();
        var path = Path.Combine(_directory, "settings.json");
        using (var settings = new AppSettings(path, new ManualTimeProvider()))
        {
            settings.Update(data);
            settings.Flush();
        }

        var json = File.ReadAllText(path);
        json.ShouldNotContain(Key);
        json.ShouldNotContain(Key[..8]);
        JsonSerializer.Serialize(data, SettingsJsonContext.Default.SettingsData).ShouldNotContain(Key);
        data.ToString().ShouldNotContain(Key);

        // Proof that the record really was filled: every property differs from its default.
        var defaults = new SettingsData();
        foreach (var property in typeof(SettingsData).GetProperties())
        {
            property.GetValue(data).ShouldNotBe(property.GetValue(defaults), $"{property.Name} was not set");
        }
    }

    [Fact]
    public void A_rejected_key_is_reported_in_both_cloud_modes_and_ignored_offline()
    {
        EngineStatus.Evaluate(EngineMode.Azure, false, true, true, keyRejected: true)
            .ShouldBe(EngineReadiness.KeyRejected);
        EngineStatus.Evaluate(EngineMode.AzureWithParakeetFallback, true, true, true, keyRejected: true)
            .ShouldBe(EngineReadiness.KeyRejected);
        EngineStatus.Evaluate(EngineMode.Parakeet, true, true, true, keyRejected: true)
            .ShouldBe(EngineReadiness.Ready);
        EngineStatus.ShortLabel(EngineReadiness.NoApiKey).ShouldBe("Add API key");
        EngineStatus.ShortLabel(EngineReadiness.Ready).ShouldBeNull();
    }

    /// <summary>A settings record with every property moved off its default.</summary>
    private static SettingsData EverythingSet()
    {
        var data = new SettingsData();
        foreach (var property in typeof(SettingsData).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;
            var current = property.GetValue(data);

            object value = type switch
            {
                _ when type == typeof(string) => $"value-of-{property.Name}",
                _ when type == typeof(int) => (int)current! + 7,
                _ when type == typeof(double) => (double)current! / 2 + 0.01,
                _ when type == typeof(bool) => !(bool)current!,
                _ when type == typeof(string[]) => new[] { "heard=>intended" },
                _ when type == typeof(HotkeyChord?) => new HotkeyChord(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'K'),
                _ when type.IsEnum => Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current)),
                _ => throw new InvalidOperationException($"Teach this test about {type} ({property.Name})."),
            };

            property.SetValue(data, value);
        }

        return data;
    }
}
