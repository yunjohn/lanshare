using LanTransfer.Core.Files;
using Xunit;

namespace LanTransfer.Core.Tests;

public sealed class AtomicFileCommitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "lantransfer-commit-" + Guid.NewGuid().ToString("N"));

    public AtomicFileCommitTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Commit_ReplacesExistingFileWithCompletedPart()
    {
        var destination = Path.Combine(_root, "file.txt");
        var temporary = destination + ".part";
        File.WriteAllText(destination, "old");
        File.WriteAllText(temporary, "new");

        AtomicFileCommit.Commit(temporary, destination);

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void Commit_WhenPartIsMissing_PreservesExistingFile()
    {
        var destination = Path.Combine(_root, "file.txt");
        File.WriteAllText(destination, "old");

        Assert.ThrowsAny<IOException>(() => AtomicFileCommit.Commit(destination + ".part", destination));

        Assert.Equal("old", File.ReadAllText(destination));
    }

    [Fact]
    public void Commit_WhenDestinationIsLocked_PreservesBothFiles()
    {
        var destination = Path.Combine(_root, "file.txt");
        var temporary = destination + ".part";
        File.WriteAllText(destination, "old");
        File.WriteAllText(temporary, "new");

        using (var held = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => AtomicFileCommit.Commit(temporary, destination));
        }

        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Equal("new", File.ReadAllText(temporary));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
