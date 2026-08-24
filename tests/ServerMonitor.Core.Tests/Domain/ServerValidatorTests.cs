using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Tests.Domain;

public sealed class ServerValidatorTests
{
    private readonly ServerValidator _validator = new();

    [Fact]
    public void Validate_AcceptsCompleteServerInput()
    {
        var result = _validator.Validate(CreateValidInput());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("", "host.example.test", 22, "monitor", ServerValidationErrorCode.NameRequired)]
    [InlineData("Server", "", 22, "monitor", ServerValidationErrorCode.HostRequired)]
    [InlineData("Server", "host.example.test", 0, "monitor", ServerValidationErrorCode.PortOutOfRange)]
    [InlineData("Server", "host.example.test", 65536, "monitor", ServerValidationErrorCode.PortOutOfRange)]
    [InlineData("Server", "host.example.test", 22, "", ServerValidationErrorCode.UsernameRequired)]
    public void Validate_RejectsInvalidData(
        string name,
        string host,
        int port,
        string username,
        ServerValidationErrorCode expectedError)
    {
        var input = CreateValidInput() with
        {
            Name = name,
            Host = host,
            Port = port,
            Username = username
        };

        var result = _validator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == expectedError);
    }

    private static ServerInput CreateValidInput() => new()
    {
        Name = "Servidor de teste",
        Host = "host.example.test",
        Port = 22,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Auto
    };
}
