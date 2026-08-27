using ServerMonitor.Collectors.Workloads.Parsing;
using ServerMonitor.Collectors.Tests.Workloads;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Collectors.Tests.Workloads.Parsing;

public sealed class DockerPsJsonParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Parse_empty_input_returns_empty(string? input)
    {
        var result = DockerPsJsonParser.Parse(input);

        Assert.Empty(result.Containers);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Parse_reads_fields_state_and_short_id()
    {
        var line = """{"ID":"abc123def4567890abcdef","Names":"/web","Image":"nginx:1.27","State":"running","Status":"Up 2 hours","CreatedAt":"2024-06-01 12:34:56 +0000 UTC"}""";

        var result = DockerPsJsonParser.Parse(line);

        var container = Assert.Single(result.Containers);
        Assert.Equal("abc123def456", container.ContainerId); // short id (12)
        Assert.Equal("web", container.Name);                 // leading slash stripped
        Assert.Equal("nginx:1.27", container.Image);
        Assert.Equal(ContainerState.Running, container.State);
        Assert.Equal("Up 2 hours", container.StatusText);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 12, 34, 56, TimeSpan.Zero), container.CreatedAt);
    }

    [Fact]
    public void Parse_representative_fixture_covers_running_stopped_and_unhealthy()
    {
        var result = DockerPsJsonParser.Parse(FixtureText.Read("docker-ps-representative.ndjson"));

        Assert.Equal(4, result.Containers.Count);
        Assert.False(result.Truncated);
        Assert.Equal(ContainerHealth.Healthy, result.Containers.Single(c => c.Name == "api").Health);
        Assert.Equal(ContainerHealth.Unhealthy, result.Containers.Single(c => c.Name == "worker").Health);
        Assert.Equal(ContainerState.Exited, result.Containers.Single(c => c.Name == "job").State);
        Assert.Equal(ContainerState.Paused, result.Containers.Single(c => c.Name == "paused").State);
    }

    [Fact]
    public void Parse_hostile_fixture_neutralizes_controls_and_preserves_safe_unicode()
    {
        var container = Assert.Single(
            DockerPsJsonParser.Parse(FixtureText.Read("docker-ps-hostile.ndjson")).Containers);

        Assert.Equal(WorkloadLimits.MaxTextLength, container.Name.Length);
        Assert.DoesNotContain('\n', container.Name);
        Assert.DoesNotContain('\t', container.Name);
        Assert.DoesNotContain((char)0x1b, container.Name);
        Assert.DoesNotContain('\u202e', container.Name);
        Assert.Contains("😀", container.Name, StringComparison.Ordinal);
        Assert.Contains("\"quote\\slash", container.Name, StringComparison.Ordinal);
        Assert.Equal("registry.example/😀\"quoted\\image", container.Image);
    }

    [Fact]
    public void Parse_malformed_fixture_preserves_records_around_bad_lines()
    {
        var result = DockerPsJsonParser.Parse(FixtureText.Read("docker-ps-malformed.ndjson"));

        Assert.Contains(result.Containers, c => c.Name == "before");
        Assert.Contains(result.Containers, c => c.Name == "after");
        Assert.DoesNotContain(result.Containers, c => c.Name == "not JSON");
    }

    [Fact]
    public void Parse_empty_fixture_is_empty()
    {
        var result = DockerPsJsonParser.Parse(FixtureText.Read("docker-ps-empty.ndjson"));

        Assert.Empty(result.Containers);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Parse_large_500_container_dataset_is_complete_and_bounded()
    {
        var input = string.Join(
            '\n',
            Enumerable.Range(0, 500)
                .Select(i => $$"""{"ID":"{{i:x16}}","Names":"container-{{i:D4}}","Image":"img","State":"running","Status":"Up"}"""));

        var result = DockerPsJsonParser.Parse(input);

        Assert.Equal(500, result.Containers.Count);
        Assert.False(result.Truncated);
        Assert.All(result.Containers, container =>
        {
            Assert.InRange(container.Name.Length, 1, WorkloadLimits.MaxTextLength);
            Assert.InRange(container.ContainerId.Length, 1, 12);
        });
    }

    [Theory]
    [InlineData("created", ContainerState.Created)]
    [InlineData("running", ContainerState.Running)]
    [InlineData("restarting", ContainerState.Restarting)]
    [InlineData("paused", ContainerState.Paused)]
    [InlineData("exited", ContainerState.Exited)]
    [InlineData("dead", ContainerState.Dead)]
    [InlineData("removing", ContainerState.Removing)]
    [InlineData("something-else", ContainerState.Unknown)]
    public void Parse_maps_state(string raw, ContainerState expected)
    {
        var line = $$"""{"ID":"id","Names":"c","Image":"img","State":"{{raw}}","Status":"x"}""";

        var container = Assert.Single(DockerPsJsonParser.Parse(line).Containers);

        Assert.Equal(expected, container.State);
    }

    [Theory]
    [InlineData("Up 2 hours (healthy)", ContainerHealth.Healthy)]
    [InlineData("Up 5 seconds (unhealthy)", ContainerHealth.Unhealthy)]
    [InlineData("Up 10 seconds (health: starting)", ContainerHealth.Starting)]
    [InlineData("Up 3 days", ContainerHealth.None)]
    [InlineData("Exited (0) 2 minutes ago", ContainerHealth.None)]
    [InlineData("", ContainerHealth.Unknown)]
    public void Parse_derives_health_from_status_parenthetical(string status, ContainerHealth expected)
    {
        var line = $$"""{"ID":"id","Names":"c","Image":"img","State":"running","Status":"{{status}}"}""";

        var container = Assert.Single(DockerPsJsonParser.Parse(line).Containers);

        Assert.Equal(expected, container.Health);
    }

    [Fact]
    public void Parse_skips_a_malformed_line_without_dropping_the_rest()
    {
        var input = string.Join('\n',
            """{"ID":"a","Names":"one","Image":"i","State":"running","Status":"Up"}""",
            "{ this is not valid json",
            """{"ID":"b","Names":"two","Image":"i","State":"exited","Status":"Exited (0)"}""");

        var result = DockerPsJsonParser.Parse(input);

        Assert.Equal(2, result.Containers.Count);
        Assert.Equal("one", result.Containers[0].Name);
        Assert.Equal("two", result.Containers[1].Name);
    }

    [Fact]
    public void Parse_unparseable_created_at_is_null()
    {
        var line = """{"ID":"id","Names":"c","Image":"img","State":"running","Status":"Up","CreatedAt":"just now"}""";

        var container = Assert.Single(DockerPsJsonParser.Parse(line).Containers);

        Assert.Null(container.CreatedAt);
    }

    [Fact]
    public void Parse_sanitizes_hostile_name_and_image()
    {
        // JSON-escaped ESC () and RLO override (‮): valid JSON whose decoded value is hostile.
        var line = "{\"ID\":\"id\",\"Names\":\"ev\\u001b[31mil\",\"Image\":\"img\\u202ename\"," +
                   "\"State\":\"running\",\"Status\":\"Up\"}";

        var container = Assert.Single(DockerPsJsonParser.Parse(line).Containers);

        Assert.Equal("evil", container.Name);       // ANSI escape stripped
        Assert.Equal("imgname", container.Image);   // bidi override stripped
    }

    [Fact]
    public void Parse_caps_the_list_and_flags_truncation()
    {
        var lines = Enumerable.Range(0, WorkloadLimits.MaxContainers + 5)
            .Select(i => $$"""{"ID":"id{{i}}","Names":"c{{i}}","Image":"img","State":"running","Status":"Up"}""");
        var input = string.Join('\n', lines);

        var result = DockerPsJsonParser.Parse(input);

        Assert.Equal(WorkloadLimits.MaxContainers, result.Containers.Count);
        Assert.True(result.Truncated);
    }
}
