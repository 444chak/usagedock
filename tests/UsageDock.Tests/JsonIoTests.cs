using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public sealed class JsonIoTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-jsonio-" + Guid.NewGuid().ToString("N"));

    public JsonIoTests() => Directory.CreateDirectory(_directory);

    private string Path_(string name) => Path.Combine(_directory, name);

    private sealed class Payload
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }
    }

    [Fact]
    public void Writes_then_reads_back()
    {
        var path = Path_("a.json");

        Assert.True(JsonIo.TryWriteFile(path, new Payload { Name = "x", Count = 3 }));

        var read = JsonIo.TryReadFile<Payload>(path);

        Assert.NotNull(read);
        Assert.Equal("x", read!.Name);
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public void Overwrites_an_existing_file()
    {
        var path = Path_("b.json");
        JsonIo.TryWriteFile(path, new Payload { Count = 1 });
        JsonIo.TryWriteFile(path, new Payload { Count = 2 });

        Assert.Equal(2, JsonIo.TryReadFile<Payload>(path)!.Count);
    }

    [Fact]
    public void Leaves_no_temporary_file_behind()
    {
        JsonIo.TryWriteFile(Path_("c.json"), new Payload());

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void A_missing_file_reads_as_null() =>
        Assert.Null(JsonIo.TryReadFile<Payload>(Path_("missing.json")));

    [Fact]
    public void A_corrupt_file_reads_as_null()
    {
        var path = Path_("bad.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(JsonIo.TryReadFile<Payload>(path));
    }

    [Fact]
    public void An_empty_file_reads_as_null()
    {
        var path = Path_("empty.json");
        File.WriteAllText(path, "   ");

        Assert.Null(JsonIo.TryReadFile<Payload>(path));
    }

    [Fact]
    public void Reads_a_file_another_process_holds_open()
    {
        var path = Path_("locked.json");
        JsonIo.TryWriteFile(path, new Payload { Name = "held" });

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal("held", JsonIo.TryReadFile<Payload>(path)!.Name);
    }

    [Fact]
    public void Concurrent_writers_all_succeed_and_the_file_stays_parseable()
    {
        var path = Path_("race.json");

        // Mirrors several Claude Code hook processes firing at once.
        Parallel.For(0, 64, i => JsonIo.TryWriteFile(path, new Payload { Name = "hook", Count = i }));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));

        var read = JsonIo.TryReadFile<Payload>(path);
        Assert.NotNull(read);
        Assert.Equal("hook", read!.Name);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
