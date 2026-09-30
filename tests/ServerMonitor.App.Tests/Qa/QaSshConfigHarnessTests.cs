using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>The Debug-only --qa-ssh-config harness: off by default, and it only re-roots the import source and (M14.5) key discovery.</summary>
public sealed class QaSshConfigHarnessTests
{
    [Fact]
    public void HarnessIsNotRequestedByDefault()
    {
        Assert.Null(QaSshConfigComposition.RequestedProfile());
    }

    [Fact]
    public void UiLanguageHarnessIsNotRequestedByDefault()
    {
        Assert.Null(QaUiLanguageComposition.RequestedLanguage());
    }

    [Fact]
    public void WithoutTheFlag_TheCompositionReadsTheRealProfile()
    {
        var services = new ServiceCollection();
        App.ConfigureApplicationServices(services);

        var registration = services.Last(descriptor => descriptor.ServiceType == typeof(ISshConfigImportSource));
        Assert.Equal(typeof(SshConfigFileImportSource), registration.ImplementationType);

        // With a second public constructor (string profile), DI must still pick the real-profile one.
        IServiceCollection isolated = new ServiceCollection();
        isolated.Add(registration);
        using var provider = isolated.BuildServiceProvider();
        var source = Assert.IsType<SshConfigFileImportSource>(provider.GetRequiredService<ISshConfigImportSource>());
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config"),
            source.ConfigPath);
    }

    [Fact]
    public void Apply_RootsTheRealReadOnlySourceAtTheFixtureProfile_AndWins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISshConfigImportSource, SshConfigFileImportSource>();

        QaSshConfigComposition.Apply(services, @"C:\qa\fixtures\normal");

        using var provider = services.BuildServiceProvider();
        var source = Assert.IsType<SshConfigFileImportSource>(provider.GetRequiredService<ISshConfigImportSource>());
        Assert.Equal(@"C:\qa\fixtures\normal\.ssh\config", source.ConfigPath);
    }

    [Fact]
    public void WithoutTheFlag_KeyDiscoveryLooksInTheRealProfile()
    {
        var services = new ServiceCollection();
        App.ConfigureApplicationServices(services);

        var registration = services.Last(descriptor => descriptor.ServiceType == typeof(ILocalSshKeyDiscovery));
        IServiceCollection isolated = new ServiceCollection();
        isolated.Add(registration);
        using var provider = isolated.BuildServiceProvider();
        var discovery = Assert.IsType<LocalSshKeyDiscovery>(provider.GetRequiredService<ILocalSshKeyDiscovery>());
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            discovery.SshDirectory);
    }

    [Fact]
    public void Apply_AlsoRootsKeyDiscoveryAtTheFixtureProfile_SoQaNeverTouchesTheRealSshDirectory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalSshKeyDiscovery>(_ => new LocalSshKeyDiscovery());

        QaSshConfigComposition.Apply(services, @"C:\qa\fixtures\keys");

        using var provider = services.BuildServiceProvider();
        var discovery = Assert.IsType<LocalSshKeyDiscovery>(provider.GetRequiredService<ILocalSshKeyDiscovery>());
        Assert.Equal(@"C:\qa\fixtures\keys\.ssh", discovery.SshDirectory);
    }
}
