using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

public sealed class JsonNotificationSettingsServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), "ServerMonitor.NotificationSettings.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFile_DefaultsToEnabledWithoutCreatingAFile()
    {
        var path = Path.Combine(_testDirectory, "notification-settings.json");

        var service = Create(path);

        Assert.True(service.NotificationsEnabled);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("{ malformed")]
    [InlineData("{}")]
    [InlineData("null")]
    public void MalformedOrMissingValue_DefaultsToEnabled(string json)
    {
        var path = Path.Combine(_testDirectory, "notification-settings.json");
        Directory.CreateDirectory(_testDirectory);
        File.WriteAllText(path, json);

        Assert.True(Create(path).NotificationsEnabled);
    }

    [Fact]
    public void OversizedFile_DefaultsToEnabled()
    {
        var path = Path.Combine(_testDirectory, "notification-settings.json");
        Directory.CreateDirectory(_testDirectory);
        File.WriteAllText(path, new string('x', JsonNotificationSettingsService.MaxFileBytes + 1));

        Assert.True(Create(path).NotificationsEnabled);
    }

    [Fact]
    public void Set_PersistsOnlyGlobalBooleanAndSurvivesRestart()
    {
        var path = Path.Combine(_testDirectory, "notification-settings.json");
        var service = Create(path);

        service.SetNotificationsEnabled(false);

        Assert.False(service.NotificationsEnabled);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("notificationsEnabled", property.Name);
        Assert.False(property.Value.GetBoolean());
        Assert.False(Create(path).NotificationsEnabled);
    }

    [Fact]
    public void WriteFailure_DoesNotCommitOrRaiseEvent_AndRecoveryPersists()
    {
        var path = Path.Combine(_testDirectory, "notification-settings.json");
        var temporaryPath = path + ".tmp";
        Directory.CreateDirectory(temporaryPath);
        var service = Create(path);
        var changes = 0;
        service.NotificationsEnabledChanged += (_, _) => changes++;

        Assert.Throws<UnauthorizedAccessException>(() => service.SetNotificationsEnabled(false));
        Assert.True(service.NotificationsEnabled);
        Assert.Equal(0, changes);

        Directory.Delete(temporaryPath);
        service.SetNotificationsEnabled(false);

        Assert.False(service.NotificationsEnabled);
        Assert.Equal(1, changes);
        Assert.False(Create(path).NotificationsEnabled);
    }

    [Fact]
    public void SettingSameValue_IsIdempotentAndDoesNotRaiseEvent()
    {
        var service = Create(Path.Combine(_testDirectory, "notification-settings.json"));
        var changes = 0;
        service.NotificationsEnabledChanged += (_, _) => changes++;

        service.SetNotificationsEnabled(true);

        Assert.Equal(0, changes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static JsonNotificationSettingsService Create(string path) => new(
        new NotificationSettingsStorageOptions { FilePath = path },
        NullLogger<JsonNotificationSettingsService>.Instance);
}
