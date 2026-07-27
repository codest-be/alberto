using Alberto.Cli;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Cli;

/// <summary>
/// Tests for <see cref="ConfigFileFinder.Find(DirectoryInfo)"/> — the directory-tree walk that
/// locates <c>.alberto/config.json</c>. Uses a temp directory so no real config files on disk
/// can interfere.
/// </summary>
public class ConfigFileFinderTests : IDisposable
{
    private readonly DirectoryInfo _root;

    public ConfigFileFinderTests()
    {
        _root = Directory.CreateTempSubdirectory("alberto-test-");
    }

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Returns_Null_When_No_Config_File_Exists()
    {
        var result = ConfigFileFinder.Find(_root);

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_Config_When_File_Exists_In_Start_Directory()
    {
        WriteConfig(_root, """{"connection": {"url": "Host=test"}, "schema": "events"}""");

        var result = ConfigFileFinder.Find(_root);

        result.Should().NotBeNull();
        result!.Connection!.Url.Should().Be("Host=test");
        result.Schema.Should().Be("events");
    }

    [Fact]
    public void Returns_Config_When_File_Exists_In_Parent_Directory()
    {
        WriteConfig(_root, """{"connection": {"url": "Host=parent"}}""");
        var deep = _root.CreateSubdirectory("sub").CreateSubdirectory("deep");

        var result = ConfigFileFinder.Find(deep);

        result.Should().NotBeNull();
        result!.Connection!.Url.Should().Be("Host=parent");
    }

    [Fact]
    public void Rejects_File_When_JSON_Is_Malformed()
    {
        WriteConfig(_root, "not valid json {{{ broken");

        var read = () => ConfigFileFinder.Find(_root);

        read.Should().Throw<CliConfigurationException>()
            .Which.Message.Should().Contain(Path.Combine(_root.FullName, ".alberto", "config.json"));
    }

    [Fact]
    public void Rejects_Unknown_Configuration_Properties()
    {
        WriteConfig(_root, """{"shard": {"db1": {"url": "Host=one"}}}""");

        var read = () => ConfigFileFinder.Find(_root);

        read.Should().Throw<CliConfigurationException>()
            .Which.Message.Should().Contain("shard");
    }

    [Fact]
    public void Parses_Shard_Configuration()
    {
        WriteConfig(_root, """
            {
                "connection": {"url": "Host=main"},
                "shards": {
                    "db1": {"url": "Host=shard1", "schema": "s1"},
                    "db2": {"url": "Host=shard2"}
                }
            }
            """);

        var result = ConfigFileFinder.Find(_root);

        result.Should().NotBeNull();
        result!.Shards.Should().HaveCount(2);
        result.Shards!["db1"].Url.Should().Be("Host=shard1");
        result.Shards["db1"].Schema.Should().Be("s1");
        result.Shards["db2"].Url.Should().Be("Host=shard2");
    }

    [Fact]
    public void Parses_Catalog_Configuration()
    {
        WriteConfig(_root, """
            {
                "catalog": {"url": "Host=catalog-db", "schema": "catalog"},
                "module": "orders"
            }
            """);

        var result = ConfigFileFinder.Find(_root);

        result.Should().NotBeNull();
        result!.Catalog!.Url.Should().Be("Host=catalog-db");
        result.Catalog.Schema.Should().Be("catalog");
        result.Module.Should().Be("orders");
    }

    [Fact]
    public void Closer_Config_Wins_Over_More_Distant_Parent()
    {
        WriteConfig(_root, """{"connection": {"url": "Host=root"}}""");
        var child = _root.CreateSubdirectory("child");
        WriteConfig(child, """{"connection": {"url": "Host=child"}}""");

        var result = ConfigFileFinder.Find(child);

        result.Should().NotBeNull();
        result!.Connection!.Url.Should().Be("Host=child");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void WriteConfig(DirectoryInfo dir, string json)
    {
        var configDir = new DirectoryInfo(Path.Combine(dir.FullName, ".alberto"));
        configDir.Create();
        File.WriteAllText(Path.Combine(configDir.FullName, "config.json"), json);
    }
}
