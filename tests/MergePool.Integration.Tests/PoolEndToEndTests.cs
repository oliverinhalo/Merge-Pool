using System.Text;
using MergePool.Core.FileSystem;
using Xunit;

namespace MergePool.Integration.Tests;

/// <summary>
/// End-to-end scenarios over fake drives: the pool is exercised through the same engine a mount
/// drives, and every assertion is made against the real files on the drives.
/// </summary>
public sealed class PoolEndToEndTests
{
    private static void Write(PoolFileSystemEngine engine, string path, string content)
    {
        var status = engine.Open(
            new PoolOpenRequest
            {
                PoolPath = path,
                Disposition = PoolCreateDisposition.OverwriteOrCreate,
                Access = PoolAccess.ReadWrite,
                Share = PoolShare.All,
                AllocationSize = Encoding.UTF8.GetByteCount(content),
            },
            out var handle,
            out _);

        Assert.Equal(PoolFsStatus.Success, status);
        Assert.Equal(
            PoolFsStatus.Success,
            engine.Write(handle!, Encoding.UTF8.GetBytes(content), 0, false, false, out _, out _));
        engine.Close(handle!);
    }

    private static string Read(PoolFileSystemEngine engine, string path)
    {
        Assert.Equal(PoolFsStatus.Success, engine.Open(new PoolOpenRequest { PoolPath = path }, out var handle, out var info));
        var buffer = new byte[info!.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var status = engine.Read(handle!, buffer.AsSpan(read), read, out var chunk);
            if (status == PoolFsStatus.EndOfFile)
            {
                break;
            }

            Assert.Equal(PoolFsStatus.Success, status);
            read += chunk;
        }

        engine.Close(handle!);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    [Fact]
    public void Files_written_to_the_pool_are_plain_files_on_the_drives()
    {
        using var drives = new FakeDriveSet();
        var (_, a) = drives.AddDrive("A", freeBytes: 1_000);
        var (_, b) = drives.AddDrive("B", freeBytes: 9_000_000);

        drives.Engine.Operations.CreateDirectory("Music/Album");
        Write(drives.Engine, "Music/Album/track.flac", "audio-bytes");

        // The file is readable straight off the drive, at the same relative path, with no app.
        var onB = Path.Combine(drives.PartRoot(b), "Music", "Album", "track.flac");
        Assert.True(File.Exists(onB));
        Assert.Equal("audio-bytes", File.ReadAllText(onB));

        // And it exists on exactly one drive: files are never split or duplicated.
        Assert.False(File.Exists(Path.Combine(drives.PartRoot(a), "Music", "Album", "track.flac")));
    }

    [Fact]
    public void Nothing_outside_the_pool_part_is_ever_modified()
    {
        using var drives = new FakeDriveSet();
        drives.AddDrive("A");

        var existing = Path.Combine(drives.DriveRoot("A"), "PreExisting");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "user.txt"), "untouched");

        drives.Engine.Operations.CreateDirectory("PoolFolder");
        Write(drives.Engine, "PoolFolder/pooled.txt", "pooled");
        drives.Engine.Operations.DeleteDirectory("PoolFolder", recursive: true);

        Assert.Equal("untouched", File.ReadAllText(Path.Combine(existing, "user.txt")));
        Assert.Empty(drives.Engine.View.EnumerateNames(""));
    }

    [Fact]
    public void A_pool_survives_a_drive_disappearing_and_coming_back()
    {
        using var drives = new FakeDriveSet();
        var (volumeA, _) = drives.AddDrive("A");
        drives.AddDrive("B");

        drives.Engine.Operations.CreateDirectory("Docs");
        Write(drives.Engine, "Docs/on-a.txt", "from A");
        var onA = drives.Engine.View.FindFile("Docs/on-a.txt")!.Part.PartId;

        // Put a second file on the other drive so the pool has content on both.
        var other = drives.Current.Parts.First(p => p.PartId != onA);
        var otherPath = Path.Combine(other.RequireRootPath(), "Docs", "on-b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.WriteAllText(otherPath, "from B");

        var missing = drives.Current.Parts.First(p => p.PartId == onA).Volume;
        drives.Volumes.SetPresent(missing, present: false);

        Assert.Equal(["on-b.txt"], drives.Engine.View.EnumerateNames("Docs"));
        Assert.Equal("from B", Read(drives.Engine, "Docs/on-b.txt"));

        drives.Volumes.SetPresent(missing, present: true);

        Assert.Equal(["on-a.txt", "on-b.txt"], drives.Engine.View.EnumerateNames("Docs"));
        Assert.Equal("from A", Read(drives.Engine, "Docs/on-a.txt"));
        _ = volumeA;
    }

    [Fact]
    public void A_growing_file_moves_whole_to_a_drive_that_fits_it()
    {
        using var drives = new FakeDriveSet();
        var (volumeA, a) = drives.AddDrive("A", totalBytes: 100_000, freeBytes: 100_000);
        var (_, b) = drives.AddDrive("B", totalBytes: 100_000, freeBytes: 20);

        Write(drives.Engine, "big.bin", "start");
        Assert.Equal(a, drives.Engine.View.FindFile("big.bin")!.Part.PartId);

        // A fills up; B is given room. The open file has to end up whole on B.
        drives.Volumes.SetCapacity(volumeA, 100_000, 4);
        var volumeB = drives.Current.Parts.First(p => p.PartId == b).Volume;
        drives.Volumes.SetCapacity(volumeB, 100_000, 100_000);

        drives.Engine.Open(
            new PoolOpenRequest { PoolPath = "big.bin", Access = PoolAccess.ReadWrite, Share = PoolShare.All },
            out var handle,
            out _);

        var payload = Encoding.UTF8.GetBytes(new string('z', 8192));
        Assert.Equal(PoolFsStatus.Success, drives.Engine.Write(handle!, payload, 5, false, false, out _, out _));
        drives.Engine.Close(handle!);

        var located = drives.Engine.View.FindAllFiles("big.bin");
        Assert.Single(located);
        Assert.Equal(b, located[0].Part.PartId);
        Assert.Equal(8197, new FileInfo(located[0].HostPath).Length);
        Assert.StartsWith("start", File.ReadAllText(located[0].HostPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_deep_tree_spanning_drives_reads_back_intact()
    {
        using var drives = new FakeDriveSet();
        drives.AddDrive("A", freeBytes: 1_000_000);
        drives.AddDrive("B", freeBytes: 1_000_000);
        drives.AddDrive("C", freeBytes: 1_000_000);

        var expected = new Dictionary<string, string>();
        for (var i = 0; i < 40; i++)
        {
            var path = $"Tree/Level{i % 4}/file-{i}.dat";
            drives.Engine.Operations.CreateDirectory($"Tree/Level{i % 4}");
            var content = $"payload-{i}";
            Write(drives.Engine, path, content);
            expected[path] = content;
        }

        foreach (var (path, content) in expected)
        {
            Assert.Equal(content, Read(drives.Engine, path));
        }

        // Every file lives on exactly one drive.
        foreach (var path in expected.Keys)
        {
            Assert.Single(drives.Engine.View.FindAllFiles(path));
        }

        // And the listing is the union of all three drives.
        var listed = drives.Engine.View.EnumerateNames("Tree/Level0");
        Assert.Equal(10, listed.Count);
    }

    [Fact]
    public void Long_paths_round_trip_through_the_pool()
    {
        using var drives = new FakeDriveSet();
        drives.AddDrive("A");

        var deep = string.Join('/', Enumerable.Range(0, 12).Select(i => $"directory-level-{i}-{new string('n', 20)}"));
        drives.Engine.Operations.CreateDirectory(deep);

        var path = deep + "/deep-file.txt";
        Write(drives.Engine, path, "deep");

        Assert.Equal("deep", Read(drives.Engine, path));
        Assert.True(drives.Engine.View.FileExists(path));
    }
}
