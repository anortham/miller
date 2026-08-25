using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreRefusalLedgerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "miller-refusals-" + Guid.NewGuid().ToString("N"));

    public StoreRefusalLedgerTests() => Directory.CreateDirectory(Path.Combine(_root, "src"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AnUnwrittenLedgerReadsEmptyAndCreatesNothing()
    {
        Assert.Empty(new StoreRefusalLedger(_root).Read());
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    [Fact]
    public void ANoOpUpdateWritesNothing()
    {
        new StoreRefusalLedger(_root).Update([], []);
        Assert.False(File.Exists(Path.Combine(_root, ".miller", "store-refusals.json")));
    }

    [Fact]
    public void ARecordedRefusalSurvivesANewLedgerInstance()
    {
        Write("src/a.cs");
        new StoreRefusalLedger(_root).Update([new StoreRefusalEntry("src/a.cs", "abc123")], []);

        Assert.Equal("abc123", new StoreRefusalLedger(_root).Read()["src/a.cs"]);
    }

    [Fact]
    public void AnAcceptedPathIsForgotten()
    {
        Write("src/a.cs");
        Write("src/b.cs");
        var ledger = new StoreRefusalLedger(_root);
        ledger.Update(
            [new StoreRefusalEntry("src/a.cs", "abc123"), new StoreRefusalEntry("src/b.cs", "def456")],
            []);

        ledger.Update([], ["src/a.cs"]);

        Assert.Equal(["src/b.cs"], ledger.Read().Keys);
    }

    [Fact]
    public void AnEntryWhoseFileVanishedIsDroppedOnTheNextWrite()
    {
        Write("src/a.cs");
        Write("src/b.cs");
        var ledger = new StoreRefusalLedger(_root);
        ledger.Update([new StoreRefusalEntry("src/a.cs", "abc123")], []);

        File.Delete(Path.Combine(_root, "src", "a.cs"));
        ledger.Update([new StoreRefusalEntry("src/b.cs", "def456")], []);

        Assert.Equal(["src/b.cs"], ledger.Read().Keys);
    }

    [Fact]
    public void AMalformedLedgerReadsAsNoMemory()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".miller"));
        File.WriteAllText(Path.Combine(_root, ".miller", "store-refusals.json"), "{ not json");

        Assert.Empty(new StoreRefusalLedger(_root).Read());
    }

    private void Write(string relativePath) =>
        File.WriteAllText(
            Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            "class X;");
}
