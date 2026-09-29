using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

/// <summary>M14.4b-1 §1 repository invariants 1–7 for the F3 two-file layout.</summary>
public sealed class JsonServerRepositoryRoutingTests : IDisposable
{
    private const string DirectId = "de305d54-75b4-431b-adb2-eb6b9e546014";
    private const string SecondDirectId = "4f1c7e2a-9b3d-4e5f-8a6b-7c8d9e0f1a2b";
    private const string RoutedId = "0b8e6c1d-2f3a-4b5c-9d6e-7f8a9b0c1d2e";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ServerMonitor.RoutingRepo.Tests",
        Guid.NewGuid().ToString("N"));

    private string DirectPath => Path.Combine(_directory, "servers.json");

    private string RoutedPath => Path.Combine(_directory, "routed-servers.json");

    // ---- invariant 1: per-entry tolerant parse -------------------------------------------------------

    [Fact]
    public async Task OneBadEntry_DoesNotPreventLoadingTheOthers()
    {
        await WriteDirectAsync($$"""
            [
              {{DirectEntry(DirectId, "first")}},
              { "id": "{{Guid.NewGuid()}}", "name": "bad", "port": "not-a-number" },
              42,
              null,
              { "name": "no id" },
              {{DirectEntry(SecondDirectId, "second")}}
            ]
            """);
        await WriteRoutedAsync($$"""
            { "schemaVersion": 1, "servers": [
              { "id": "{{Guid.NewGuid()}}", "name": "bad routed", "route": { "jump": { "port": "x" } } },
              {{RoutedEntry(RoutedId, "routed")}}
            ] }
            """);
        using var repository = CreateRepository();

        var servers = await repository.GetAllAsync();

        Assert.Equal(
            new[] { Guid.Parse(DirectId), Guid.Parse(SecondDirectId), Guid.Parse(RoutedId) }.Order(),
            servers.Select(server => server.Id).Order());
        Assert.NotNull(servers.Single(server => server.Id == Guid.Parse(RoutedId)).Route?.Jump);
    }

    // ---- invariant 2: unknown data survives ----------------------------------------------------------

    [Fact]
    public async Task UnknownEntryProperties_AnyCasing_SurviveASave()
    {
        await WriteDirectAsync($$"""
            [
              {
                "id": "{{DirectId}}", "NAME": "old name", "host": "a.example.test", "port": 22,
                "username": "monitor", "authenticationMethod": 1, "privateKeyPath": "C:\\k",
                "createdAt": "2026-01-01T00:00:00+00:00",
                "futureField": { "nested": [1, 2, 3], "flag": true },
                "EXTRA": "kept"
              }
            ]
            """);
        using var repository = CreateRepository();
        var loaded = Assert.Single(await repository.GetAllAsync());

        await repository.SaveAllAsync([loaded with { Name = "new name" }]);

        var entry = Assert.IsType<JsonObject>(Assert.Single(ReadArray(DirectPath)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "nested": [1, 2, 3], "flag": true }"""), entry["futureField"]));
        Assert.Equal("kept", entry["EXTRA"]!.GetValue<string>());
        // The known property is written once, with the new value, in canonical casing.
        Assert.Single(entry, property => string.Equals(property.Key, "name", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("new name", entry["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task RoutedFile_UnknownTopLevelAndNestedProperties_SurviveASave()
    {
        await WriteRoutedAsync($$"""
            {
              "schemaVersion": 1,
              "generator": { "version": "9.9" },
              "servers": [
                {
                  "id": "{{RoutedId}}", "name": "routed", "host": "10.0.0.5", "port": 22, "username": "app",
                  "authenticationMethod": 1, "privateKeyPath": "C:\\k", "createdAt": "2026-01-01T00:00:00+00:00",
                  "route": {
                    "futureRouteField": "r",
                    "jump": { "host": "bastion.example.test", "port": 22, "username": "jump",
                              "authenticationMethod": 1, "privateKeyPath": "C:\\j", "futureJumpField": [true] }
                  },
                  "futureEntryField": 7
                }
              ],
              "TrailingUnknown": null
            }
            """);
        using var repository = CreateRepository();
        var loaded = Assert.Single(await repository.GetAllAsync());

        await repository.SaveAllAsync([loaded with { Name = "renamed" }]);

        var root = ReadObject(RoutedPath);
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "version": "9.9" }"""), root["generator"]));
        Assert.True(root.ContainsKey("TrailingUnknown"));
        var entry = Assert.IsType<JsonObject>(Assert.Single(root["servers"]!.AsArray()));
        Assert.Equal("renamed", entry["name"]!.GetValue<string>());
        Assert.Equal(7, entry["futureEntryField"]!.GetValue<int>());
        Assert.Equal("r", entry["route"]!["futureRouteField"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[true]"), entry["route"]!["jump"]!["futureJumpField"]));
        Assert.Equal("bastion.example.test", entry["route"]!["jump"]!["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnparsableEntries_AreWrittenBackVerbatim_InBothFiles()
    {
        const string badDirect = """{ "id": "11111111-2222-3333-4444-555555555555", "port": "x", "odd": [1, {"a": null}] }""";
        const string badRouted = """{ "id": "66666666-7777-8888-9999-000000000000", "route": { "jump": { "port": { } } } }""";
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "ok")}, {badDirect}, \"just a string\" ]");
        await WriteRoutedAsync($$"""{ "schemaVersion": 1, "servers": [ {{badRouted}}, {{RoutedEntry(RoutedId, "ok")}} ] }""");
        using var repository = CreateRepository();
        var loaded = await repository.GetAllAsync();

        await repository.SaveAllAsync(loaded.ToArray());

        var direct = ReadArray(DirectPath);
        Assert.Contains(direct, node => JsonNode.DeepEquals(node, JsonNode.Parse(badDirect)));
        Assert.Contains(direct, node => JsonNode.DeepEquals(node, JsonValue.Create("just a string")));
        var routed = ReadObject(RoutedPath)["servers"]!.AsArray();
        Assert.Contains(routed, node => JsonNode.DeepEquals(node, JsonNode.Parse(badRouted)));
        Assert.Equal(2, (await CreateRepository().GetAllAsync()).Count);
    }

    [Fact]
    public async Task EntriesThisInstanceNeverHandedOut_AreNeverDeleted()
    {
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "existing")} ]");
        using var repository = CreateRepository();

        // A save without a prior load (or after a load that failed) must not treat the entry as removed.
        await repository.SaveAllAsync([CreateServer(Guid.Parse(SecondDirectId))]);

        var ids = (await CreateRepository().GetAllAsync()).Select(server => server.Id).ToArray();
        Assert.Contains(Guid.Parse(DirectId), ids);
        Assert.Contains(Guid.Parse(SecondDirectId), ids);
    }

    [Fact]
    public async Task LoadedEntryAbsentFromTheSave_IsRemoved()
    {
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "a")}, {DirectEntry(SecondDirectId, "b")} ]");
        using var repository = CreateRepository();
        var loaded = await repository.GetAllAsync();

        await repository.SaveAllAsync(loaded.Where(server => server.Id != Guid.Parse(DirectId)).ToArray());

        Assert.Equal(Guid.Parse(SecondDirectId), Assert.Single(await CreateRepository().GetAllAsync()).Id);
    }

    // ---- invariant 3: never overwrite what failed to parse -------------------------------------------

    [Theory]
    [InlineData(false, "{ invalid json é\r\n")]
    [InlineData(false, """{ "not": "an array" }""")]
    [InlineData(false, "null")]
    [InlineData(true, "{ invalid json")]
    [InlineData(true, """[ { "id": "x" } ]""")]
    [InlineData(true, """{ "servers": [] }""")]
    [InlineData(true, """{ "schemaVersion": 0, "servers": [] }""")]
    [InlineData(true, """{ "schemaVersion": "1", "servers": [] }""")]
    [InlineData(true, """{ "schemaVersion": 1, "servers": {} }""")]
    public async Task InvalidFile_IsBackedUpByteForByte_BeforeItIsReplaced(bool routedFile, string content)
    {
        var path = routedFile ? RoutedPath : DirectPath;
        Directory.CreateDirectory(_directory);
        var original = System.Text.Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(path, original);
        using var repository = CreateRepository();

        Assert.Empty(await repository.GetAllAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(path)); // a read never touches it
        Assert.Empty(Backups(path));

        var saved = routedFile ? CreateRoutedServer(Guid.NewGuid()) : CreateServer(Guid.NewGuid());
        await repository.SaveAllAsync([saved]);

        var backup = Assert.Single(Backups(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        Assert.Matches(@"\.corrupt-\d{8}T\d{9}Z$", backup);
        Assert.Equal(saved.Id, Assert.Single(await CreateRepository().GetAllAsync()).Id);
    }

    [Fact]
    public async Task Utf8BomFile_IsNotTreatedAsCorrupt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(
            DirectPath,
            [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes($"[ {DirectEntry(DirectId, "bom")} ]")]);
        using var repository = CreateRepository();

        var loaded = Assert.Single(await repository.GetAllAsync());
        await repository.SaveAllAsync([loaded]);

        Assert.Empty(Backups(DirectPath));
    }

    // ---- invariant 4: future schema is read-only -----------------------------------------------------

    [Fact]
    public async Task FutureSchemaRoutedFile_IsNotLoaded_AndNeverRewritten()
    {
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "direct")}, {DirectEntry(RoutedId, "stale direct copy")} ]");
        await WriteRoutedAsync($$"""{ "schemaVersion": 2, "servers": [ {{RoutedEntry(RoutedId, "future")}} ], "newThing": 1 }""");
        var routedBefore = await File.ReadAllBytesAsync(RoutedPath);
        using var repository = CreateRepository();

        var loaded = await repository.GetAllAsync();

        // Neither the future routed server nor a direct copy of it is loaded.
        Assert.Equal(Guid.Parse(DirectId), Assert.Single(loaded).Id);

        await repository.SaveAllAsync([loaded[0] with { Name = "edited" }]);
        Assert.Equal(routedBefore, await File.ReadAllBytesAsync(RoutedPath));
        Assert.Contains(ReadArray(DirectPath), node => node?["id"]?.GetValue<string>() == RoutedId);

        var directBefore = await File.ReadAllBytesAsync(DirectPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAllAsync([loaded[0], CreateRoutedServer(Guid.NewGuid())]));
        Assert.Equal(routedBefore, await File.ReadAllBytesAsync(RoutedPath));
        Assert.Equal(directBefore, await File.ReadAllBytesAsync(DirectPath));
    }

    // ---- invariant 5: a route never degrades to direct -----------------------------------------------

    [Theory]
    [InlineData("route", """{ "jump": { "host": "bastion" } }""")]
    [InlineData("Route", "null")]
    [InlineData("ROUTE", "\"anything\"")]
    [InlineData("jump", """{ "host": "bastion", "port": 22 }""")]
    [InlineData("Jump", "\"bastion\"")]
    public async Task LegacyEntryCarryingARoute_IsQuarantined_NotLoadedAsDirect(string propertyName, string value)
    {
        var routedLike = DirectEntry(RoutedId, "routed in legacy file").TrimEnd('}') + $", \"{propertyName}\": {value} }}";
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "direct")}, {routedLike} ]");
        using var repository = CreateRepository();

        var loaded = await repository.GetAllAsync();

        Assert.Equal(Guid.Parse(DirectId), Assert.Single(loaded).Id);
        await repository.SaveAllAsync(loaded.ToArray());
        Assert.Contains(ReadArray(DirectPath), node => JsonNode.DeepEquals(node, JsonNode.Parse(routedLike)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(""", "route": null""")]
    [InlineData(""", "route": { }""")]
    [InlineData(""", "route": { "jump": null }""")]
    [InlineData(""", "route": "bastion" """)]
    [InlineData(""", "route": { "jump": { "port": "x" } }""")]
    public async Task RoutedEntryWithoutAUsableRoute_IsQuarantined_NotLoadedAsDirect(string routeFragment)
    {
        var entry = DirectEntry(RoutedId, "routeless").TrimEnd('}') + routeFragment + " }";
        await WriteRoutedAsync($$"""{ "schemaVersion": 1, "servers": [ {{entry}} ] }""");
        using var repository = CreateRepository();

        Assert.Empty(await repository.GetAllAsync());

        await repository.SaveAllAsync([CreateServer(Guid.Parse(DirectId))]);
        Assert.Contains(ReadObject(RoutedPath)["servers"]!.AsArray(), node => JsonNode.DeepEquals(node, JsonNode.Parse(entry)));
        Assert.DoesNotContain(ReadArray(DirectPath), node => node?["id"]?.GetValue<string>() == RoutedId);
    }

    [Fact]
    public async Task DirectCopyOfAnUnloadableRoutedId_IsKeptButNeverLoaded()
    {
        var routeless = DirectEntry(RoutedId, "routeless").TrimEnd('}') + ", \"route\": { } }";
        await WriteDirectAsync($"[ {DirectEntry(RoutedId, "direct copy")} ]");
        await WriteRoutedAsync($$"""{ "schemaVersion": 1, "servers": [ {{routeless}} ] }""");
        using var repository = CreateRepository();

        Assert.Empty(await repository.GetAllAsync());

        await repository.SaveAllAsync([]);
        Assert.Contains(ReadArray(DirectPath), node => node?["id"]?.GetValue<string>() == RoutedId);
    }

    [Theory]
    [InlineData("""{ "servers": [ {{ROUTED}} ] }""")]
    [InlineData("""{ "schemaVersion": 0, "servers": [ {{ROUTED}} ] }""")]
    [InlineData("""{ "schemaVersion": "1", "servers": [ {{ROUTED}} ] }""")]
    [InlineData("""[ {{ROUTED}} ]""")]
    public async Task CrashLeftDirectCopy_IsNotLoaded_WhenTheRoutedFileIsInvalidButStillListsTheId(string routedTemplate)
    {
        // Review F4: an invalid routed file that still enumerates the id keeps claiming it, before AND after
        // the save that replaces the invalid file.
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "direct")}, {DirectEntry(RoutedId, "stale direct copy")} ]");
        await WriteRoutedAsync(routedTemplate.Replace("{{ROUTED}}", RoutedEntry(RoutedId, "routed"), StringComparison.Ordinal));
        using var repository = CreateRepository();

        var loaded = await repository.GetAllAsync();
        Assert.Equal(Guid.Parse(DirectId), Assert.Single(loaded).Id);

        await repository.SaveAllAsync(loaded.ToArray());
        Assert.Single(Backups(RoutedPath));

        // The recovered entry is carried verbatim into the replacement routed file, so the id stays claimed:
        // from now on it can only ever load ROUTED, never as the stale direct copy.
        var afterSave = await CreateRepository().GetAllAsync();
        Assert.Contains(afterSave, server => server.Id == Guid.Parse(DirectId));
        Assert.All(
            afterSave.Where(server => server.Id == Guid.Parse(RoutedId)),
            server => Assert.NotNull(server.Route));
        Assert.Contains(ReadObject(RoutedPath)["servers"]!.AsArray(), node => node?["id"]?.GetValue<string>() == RoutedId);
    }

    [Fact]
    public async Task Save_SplitsServersByRoute_AndTheLegacyFileKeepsItsSchema()
    {
        using var repository = CreateRepository();
        var direct = CreateServer(Guid.Parse(DirectId));
        var routed = CreateRoutedServer(Guid.Parse(RoutedId));

        await repository.SaveAllAsync([direct, routed]);

        var legacy = Assert.IsType<JsonObject>(Assert.Single(ReadArray(DirectPath)));
        Assert.Equal(DirectId, legacy["id"]!.GetValue<string>());
        Assert.DoesNotContain(legacy, property =>
            property.Key.Equals("route", StringComparison.OrdinalIgnoreCase)
            || property.Key.Equals("jump", StringComparison.OrdinalIgnoreCase));
        var routedEntry = Assert.Single(ReadObject(RoutedPath)["servers"]!.AsArray());
        Assert.Equal(RoutedId, routedEntry!["id"]!.GetValue<string>());

        var restored = await CreateRepository().GetAllAsync();
        Assert.Equal(direct, restored.Single(server => server.Id == direct.Id));
        Assert.Equal(routed, restored.Single(server => server.Id == routed.Id));
        Assert.False(File.Exists(DirectPath + ".tmp"));
        Assert.False(File.Exists(RoutedPath + ".tmp"));
    }

    [Fact]
    public async Task NoRoutedServers_DoesNotCreateTheRoutedFile()
    {
        using var repository = CreateRepository();

        await repository.SaveAllAsync([CreateServer(Guid.Parse(DirectId))]);

        Assert.True(File.Exists(DirectPath));
        Assert.False(File.Exists(RoutedPath));
    }

    // ---- invariant 6: moves are crash-safe toward the route ------------------------------------------

    [Fact]
    public async Task IdInBothFiles_LoadsTheRoutedCopy_AndTheNextSaveRemovesTheDuplicate()
    {
        await WriteDirectAsync($"[ {DirectEntry(RoutedId, "direct copy")} ]");
        await WriteRoutedAsync($$"""{ "schemaVersion": 1, "servers": [ {{RoutedEntry(RoutedId, "routed copy")}} ] }""");
        using var repository = CreateRepository();

        var loaded = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("routed copy", loaded.Name);
        Assert.NotNull(loaded.Route);

        await repository.SaveAllAsync([loaded]);
        Assert.Empty(ReadArray(DirectPath));
        Assert.Single(ReadObject(RoutedPath)["servers"]!.AsArray());
    }

    // The ordering tests simulate a real CRASH at a write: the process dies, so no rollback runs. The fault
    // seam fails the given write AND every restore, leaving the files exactly as a crash at that point would.

    [Fact]
    public async Task GainingARoute_WritesTheRoutedFileFirst()
    {
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "moving")} ]");
        var crash = new CrashPoint();
        using var repository = CreateRepository(crash.Injector);
        var loaded = Assert.Single(await repository.GetAllAsync());

        // Crash at the FIRST write in the correct order: nothing may have changed. Had servers.json been
        // written first, the server would already be gone from it with no routed copy — lost.
        crash.At = RoutedPath;
        await Assert.ThrowsAnyAsync<IOException>(() =>
            repository.SaveAllAsync([loaded with { Route = CreateRoute() }]));

        var afterCrash = Assert.Single(await CreateRepository().GetAllAsync());
        Assert.Equal(loaded.Id, afterCrash.Id);

        crash.At = null;
        await repository.SaveAllAsync([loaded with { Route = CreateRoute() }]);
        Assert.NotNull(Assert.Single(await CreateRepository().GetAllAsync()).Route);
        Assert.Empty(ReadArray(DirectPath));
    }

    [Fact]
    public async Task LosingARoute_WritesTheLegacyFileFirst()
    {
        await WriteRoutedAsync($$"""{ "schemaVersion": 1, "servers": [ {{RoutedEntry(RoutedId, "moving")}} ] }""");
        var crash = new CrashPoint();
        using var repository = CreateRepository(crash.Injector);
        var loaded = Assert.Single(await repository.GetAllAsync());

        crash.At = DirectPath;
        await Assert.ThrowsAnyAsync<IOException>(() =>
            repository.SaveAllAsync([loaded with { Route = null }]));

        var afterCrash = Assert.Single(await CreateRepository().GetAllAsync());
        Assert.NotNull(afterCrash.Route);

        crash.At = null;
        await repository.SaveAllAsync([loaded with { Route = null }]);
        Assert.Null(Assert.Single(await CreateRepository().GetAllAsync()).Route);
        Assert.Empty(ReadObject(RoutedPath)["servers"]!.AsArray());
    }

    [Fact]
    public async Task GainAndLossInOneSave_NeverShowAnyoneAsDirectOnlyOrLost()
    {
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "gainer")} ]");
        await WriteRoutedAsync($$"""{ "schemaVersion": 1, "servers": [ {{RoutedEntry(RoutedId, "loser")}} ] }""");
        var crash = new CrashPoint();
        using var repository = CreateRepository(crash.Injector);
        var loaded = await repository.GetAllAsync();
        var gainer = loaded.Single(server => server.Id == Guid.Parse(DirectId)) with { Route = CreateRoute() };
        var loser = loaded.Single(server => server.Id == Guid.Parse(RoutedId)) with { Route = null };

        // Crash after the intermediate routed write, before servers.json.
        crash.At = DirectPath;
        await Assert.ThrowsAnyAsync<IOException>(() => repository.SaveAllAsync([gainer, loser]));

        var afterCrash = await CreateRepository().GetAllAsync();
        Assert.Equal(2, afterCrash.Count);
        Assert.All(afterCrash, server => Assert.NotNull(server.Route));

        crash.At = null;
        await repository.SaveAllAsync([gainer, loser]);
        var final = await CreateRepository().GetAllAsync();
        Assert.NotNull(final.Single(server => server.Id == gainer.Id).Route);
        Assert.Null(final.Single(server => server.Id == loser.Id).Route);
    }

    [Fact]
    public async Task AThrownFailure_IsAllOrNothing_TheFirstFileIsRolledBack()
    {
        // Review F1 at repository level: without a crash, a failure of the second write restores the first.
        await WriteDirectAsync($"[ {DirectEntry(DirectId, "gainer")} ]");
        await WriteRoutedAsync($$"""{ "schemaVersion": 1, "servers": [ {{RoutedEntry(RoutedId, "loser")}} ] }""");
        var directBefore = await File.ReadAllBytesAsync(DirectPath);
        var routedBefore = await File.ReadAllBytesAsync(RoutedPath);
        using var repository = CreateRepository(point =>
        {
            if (point == "write:" + DirectPath)
            {
                throw new IOException("Synthetic write failure.");
            }
        });
        var loaded = await repository.GetAllAsync();
        var gainer = loaded.Single(server => server.Id == Guid.Parse(DirectId)) with { Route = CreateRoute() };
        var loser = loaded.Single(server => server.Id == Guid.Parse(RoutedId)) with { Route = null };

        var failure = await Assert.ThrowsAsync<IOException>(() => repository.SaveAllAsync([gainer, loser]));

        Assert.Equal("Synthetic write failure.", failure.Message);
        Assert.Equal(directBefore, await File.ReadAllBytesAsync(DirectPath));
        Assert.Equal(routedBefore, await File.ReadAllBytesAsync(RoutedPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonServerRepository CreateRepository(Action<string>? faults = null) => new(
        new ServerStorageOptions { FilePath = DirectPath },
        NullLogger<JsonServerRepository>.Instance)
    {
        FaultInjector = faults
    };

    private sealed class CrashPoint
    {
        public string? At { get; set; }

        public void Injector(string point)
        {
            if (At is not null && (point == "write:" + At || point.StartsWith("restore:", StringComparison.Ordinal)))
            {
                throw new IOException("Simulated crash at " + point);
            }
        }
    }

    private IEnumerable<string> Backups(string path) =>
        Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, Path.GetFileName(path) + ".corrupt-*")
            : [];

    private async Task WriteDirectAsync(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(DirectPath, content);
    }

    private async Task WriteRoutedAsync(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(RoutedPath, content);
    }

    private static JsonArray ReadArray(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsArray();

    private static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static string DirectEntry(string id, string name) => $$"""
        { "id": "{{id}}", "name": "{{name}}", "host": "server.example.test", "port": 22, "username": "monitor",
          "authenticationMethod": 1, "privateKeyPath": "C:\\keys\\id", "createdAt": "2026-01-01T00:00:00+00:00" }
        """;

    private static string RoutedEntry(string id, string name) => $$"""
        { "id": "{{id}}", "name": "{{name}}", "host": "10.0.0.5", "port": 22, "username": "app",
          "authenticationMethod": 1, "privateKeyPath": "C:\\keys\\id", "createdAt": "2026-01-01T00:00:00+00:00",
          "route": { "jump": { "host": "bastion.example.test", "port": 22, "username": "jump",
                               "authenticationMethod": 1, "privateKeyPath": "C:\\keys\\jump" } } }
        """;

    private static ServerRoute CreateRoute() => new()
    {
        Jump = new JumpHop
        {
            Host = "bastion.example.test",
            Port = 2222,
            Username = "jump",
            AuthenticationMethod = AuthenticationMethod.Password,
            CredentialReferenceId = Guid.Parse("9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d")
        }
    };

    private static Server CreateServer(Guid id) => new()
    {
        Id = id,
        Name = "Server",
        Host = "server.example.test",
        Port = 22,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = "C:\\keys\\id",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static Server CreateRoutedServer(Guid id) => CreateServer(id) with
    {
        Host = "10.0.0.5",
        Route = CreateRoute()
    };
}
