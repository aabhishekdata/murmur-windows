using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Who sees the first-run window: new installs, and nobody who was already using Murmur.
/// </summary>
/// <remarks>
/// The gate is the settings version, and the rule that matters most is the one that is easy to
/// get backwards: a settings file written before the version existed belongs to an existing user,
/// and they must never be greeted with "Welcome to Murmur".
/// </remarks>
public sealed class OnboardingGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-onboarding", Guid.NewGuid().ToString("N"));

    public OnboardingGateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void A_fresh_install_is_onboarded()
    {
        using var settings = new AppSettings(SettingsPath);

        settings.Data.SettingsVersion.ShouldBe(0);
        Onboarding.IsNeeded(settings.Data).ShouldBeTrue();
    }

    [Fact]
    public void A_settings_file_from_before_the_version_existed_is_an_existing_user()
    {
        // Exactly what the previous release wrote: every field, and no SettingsVersion.
        File.WriteAllText(SettingsPath, """
            {
              "PushToTalkKey": 161,
              "InjectText": true,
              "KeepHistory": true,
              "FlowBarEdge": 0,
              "FlowBarOffset": 0.5,
              "Engine": 0
            }
            """);

        using var settings = new AppSettings(SettingsPath);

        Onboarding.IsNeeded(settings.Data).ShouldBeFalse("current users must not see a welcome screen");
        settings.Data.SettingsVersion.ShouldBe(SettingsData.CurrentVersion);
        settings.Data.PushToTalkKey.ShouldBe(0xA1, "the rest of the file is read as before");
    }

    [Fact]
    public void Quitting_half_way_through_setup_shows_it_again()
    {
        // Choices made during setup are saved as they are made, with the version still 0. That
        // explicit 0 is what tells this file apart from an old one.
        using (var first = new AppSettings(SettingsPath))
        {
            first.Update(first.Data with { Engine = EngineMode.Azure });
            first.Flush();
        }

        File.ReadAllText(SettingsPath).ShouldContain("\"SettingsVersion\": 0");

        using var again = new AppSettings(SettingsPath);
        Onboarding.IsNeeded(again.Data).ShouldBeTrue();
        again.Data.Engine.ShouldBe(EngineMode.Azure, "what was chosen is kept");
    }

    [Fact]
    public void Finishing_setup_is_remembered()
    {
        using (var first = new AppSettings(SettingsPath))
        {
            Onboarding.Complete(first);
            Onboarding.IsNeeded(first.Data).ShouldBeFalse();
            first.Flush();
        }

        using var again = new AppSettings(SettingsPath);
        Onboarding.IsNeeded(again.Data).ShouldBeFalse();
    }

    [Fact]
    public void Completing_twice_changes_nothing()
    {
        using var settings = new AppSettings(SettingsPath);
        Onboarding.Complete(settings);
        settings.Flush();

        var changes = 0;
        settings.Changed += (_, _) => changes++;
        Onboarding.Complete(settings);

        changes.ShouldBe(0);
    }

    [Fact]
    public void An_unreadable_settings_file_belongs_to_someone_who_has_used_Murmur()
    {
        // Defaults, as ever — but not a welcome screen for a user whose file got corrupted.
        File.WriteAllText(SettingsPath, "{ this is not json");

        using var settings = new AppSettings(SettingsPath);

        Onboarding.IsNeeded(settings.Data).ShouldBeFalse();
        settings.Data.PushToTalkKey.ShouldBe(new SettingsData().PushToTalkKey);
    }

    [Fact]
    public void A_newer_version_than_this_build_knows_is_not_onboarded_again()
    {
        File.WriteAllText(SettingsPath, $$"""{ "SettingsVersion": {{SettingsData.CurrentVersion + 3}} }""");

        using var settings = new AppSettings(SettingsPath);

        Onboarding.IsNeeded(settings.Data).ShouldBeFalse();
    }
}
