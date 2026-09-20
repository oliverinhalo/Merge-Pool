using System.Text;
using MergePool.Core.FileSystem;
using Xunit;

namespace MergePool.Core.Tests;

public sealed class PoolFileSystemEngineTests
{
    private static PoolFileSystemEngine Engine(TempPool pool, PoolFileSystemOptions? options = null) =>
        new(pool, pool.Placement, relocator: null, options ?? new PoolFileSystemOptions { RelocationReserveBytes = 0 });

    private static PoolOpenRequest CreateRequest(string path, long allocationSize = 0) => new()
    {
        PoolPath = path,
        Disposition = PoolCreateDisposition.Create,
        Access = PoolAccess.ReadWrite,
        Share = PoolShare.All,
        AllocationSize = allocationSize,
    };

    [Fact]
    public void Create_write_and_read_back_through_the_pool()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");
        var engine = Engine(pool);

        Assert.Equal(PoolFsStatus.Success, engine.Open(CreateRequest("notes.txt"), out var handle, out _));
        var payload = Encoding.UTF8.GetBytes("hello pool");
        Assert.Equal(PoolFsStatus.Success, engine.Write(handle!, payload, 0, false, false, out var written, out _));
        Assert.Equal(payload.Length, written);
        engine.Close(handle!);

        Assert.Equal(PoolFsStatus.Success, engine.Open(new PoolOpenRequest { PoolPath = "notes.txt" }, out var reader, out var info));
        var buffer = new byte[64];
        Assert.Equal(PoolFsStatus.Success, engine.Read(reader!, buffer, 0, out var read));
        engine.Close(reader!);

        Assert.Equal("hello pool", Encoding.UTF8.GetString(buffer, 0, read));
        Assert.Equal(payload.Length, info!.Length);
    }

    [Fact]
    public void A_pooled_file_is_a_normal_file_on_the_drive()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var engine = Engine(pool);
        pool.Operations.CreateDirectory("Movies");

        engine.Open(CreateRequest("Movies/clip.mkv"), out var handle, out _);
        engine.Write(handle!, "data"u8, 0, false, false, out _, out _);
        engine.Close(handle!);

        var onDisk = Path.Combine(pool.PartRoot(a), "Movies", "clip.mkv");
        Assert.True(File.Exists(onDisk));
        Assert.Equal("data", File.ReadAllText(onDisk));
    }

    [Fact]
    public void Reading_past_the_end_reports_end_of_file()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "small.txt", "abc");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "small.txt" }, out var handle, out _);
        var buffer = new byte[8];

        Assert.Equal(PoolFsStatus.Success, engine.Read(handle!, buffer, 0, out _));
        Assert.Equal(PoolFsStatus.EndOfFile, engine.Read(handle!, buffer, 3, out _));
    }

    [Fact]
    public void Opening_a_missing_file_reports_object_name_not_found()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");
        var engine = Engine(pool);

        Assert.Equal(
            PoolFsStatus.ObjectNameNotFound,
            engine.Open(new PoolOpenRequest { PoolPath = "nope.txt" }, out _, out _));
    }

    [Fact]
    public void Creating_a_file_in_a_missing_folder_reports_path_not_found()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");
        var engine = Engine(pool);

        Assert.Equal(
            PoolFsStatus.ObjectPathNotFound,
            engine.Open(CreateRequest("nowhere/file.txt"), out _, out _));
    }

    [Fact]
    public void Creating_an_existing_file_collides()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "exists.txt", "x");
        var engine = Engine(pool);

        Assert.Equal(PoolFsStatus.ObjectNameCollision, engine.Open(CreateRequest("exists.txt"), out _, out _));
    }

    [Fact]
    public void Opening_a_folder_as_a_file_is_rejected()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");
        var engine = Engine(pool);
        pool.Operations.CreateDirectory("Movies");

        Assert.Equal(
            PoolFsStatus.FileIsADirectory,
            engine.Open(new PoolOpenRequest { PoolPath = "Movies" }, out _, out _));
    }

    [Fact]
    public void Directory_listing_is_merged_across_drives()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");
        pool.WriteInto(a, "Shared/one.txt", "1");
        pool.WriteInto(b, "Shared/two.txt", "2");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "Shared", IsDirectory = true }, out var handle, out _);
        var entries = engine.ReadDirectory(handle!);

        Assert.Equal(["one.txt", "two.txt"], entries.Select(e => Paths.PoolPath.GetName(e.PoolPath)));
    }

    [Fact]
    public void Directory_listing_resumes_after_a_marker()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "a.txt", "1");
        pool.WriteInto(a, "b.txt", "2");
        pool.WriteInto(a, "c.txt", "3");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "", IsDirectory = true }, out var handle, out _);
        var entries = engine.ReadDirectory(handle!, pattern: null, marker: "a.txt");

        Assert.Equal(["b.txt", "c.txt"], entries.Select(e => e.PoolPath));
    }

    [Fact]
    public void Creating_a_directory_mirrors_it_and_reports_the_attribute()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");
        var engine = Engine(pool);

        var status = engine.Open(
            new PoolOpenRequest { PoolPath = "New", IsDirectory = true, Disposition = PoolCreateDisposition.Create },
            out _,
            out var info);

        Assert.Equal(PoolFsStatus.Success, status);
        Assert.True(info!.IsDirectory);
        Assert.True(Directory.Exists(Path.Combine(pool.PartRoot(a), "New")));
        Assert.True(Directory.Exists(Path.Combine(pool.PartRoot(b), "New")));
    }

    [Fact]
    public void Rename_through_the_engine_keeps_the_drive_and_the_handle_usable()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A", freeBytes: 1000);
        pool.AddDrive("B", freeBytes: 900_000);
        pool.WriteInto(a, "old.txt", "payload");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "old.txt", Access = PoolAccess.ReadWrite }, out var handle, out _);
        Assert.Equal(PoolFsStatus.Success, engine.Rename(handle!, "new.txt", replaceIfExists: false));

        Assert.Equal(a, handle!.PartId);
        Assert.Equal(0, handle.RelocationCount);

        var buffer = new byte[16];
        Assert.Equal(PoolFsStatus.Success, engine.Read(handle, buffer, 0, out var read));
        Assert.Equal("payload", Encoding.UTF8.GetString(buffer, 0, read));

        engine.Close(handle);
        Assert.True(pool.View.FileExists("new.txt"));
        Assert.False(pool.View.FileExists("old.txt"));
    }

    [Fact]
    public void A_file_that_outgrows_its_drive_is_moved_whole_to_one_that_fits()
    {
        using var pool = new TempPool();
        var (volumeA, a) = pool.AddDrive("A", totalBytes: 10_000, freeBytes: 10_000);
        var (_, b) = pool.AddDrive("B", totalBytes: 10_000_000, freeBytes: 10_000_000);
        pool.WriteInto(a, "growing.bin", "seed");

        var engine = Engine(pool);
        RelocationResult? relocated = null;
        engine.FileRelocated += (_, result) => relocated = result;

        engine.Open(new PoolOpenRequest { PoolPath = "growing.bin", Access = PoolAccess.ReadWrite }, out var handle, out _);

        // The drive fills up while the file is open.
        pool.Volumes.SetCapacity(volumeA, totalBytes: 10_000, freeBytes: 8);

        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));
        Assert.Equal(PoolFsStatus.Success, engine.Write(handle!, payload, 4, false, false, out _, out var info));
        engine.Close(handle!);

        Assert.Equal(b, handle!.PartId);
        Assert.Equal(1, handle.RelocationCount);
        Assert.NotNull(relocated);
        Assert.Equal(a, relocated.FromPartId);
        Assert.Equal(b, relocated.ToPartId);

        // Still exactly one copy, and it carries the original bytes plus the new ones.
        var all = pool.View.FindAllFiles("growing.bin");
        Assert.Single(all);
        Assert.Equal(b, all[0].Part.PartId);
        Assert.Equal(4100, new FileInfo(all[0].HostPath).Length);
        Assert.Equal(4100, info!.Length);
        Assert.StartsWith("seed", File.ReadAllText(all[0].HostPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_that_fits_nowhere_reports_disk_full()
    {
        using var pool = new TempPool();
        var (volumeA, a) = pool.AddDrive("A", totalBytes: 10_000, freeBytes: 10_000);
        pool.WriteInto(a, "growing.bin", "seed");

        var engine = Engine(pool);
        engine.Open(new PoolOpenRequest { PoolPath = "growing.bin", Access = PoolAccess.ReadWrite }, out var handle, out _);
        pool.Volumes.SetCapacity(volumeA, totalBytes: 10_000, freeBytes: 0);

        var status = engine.Write(handle!, new byte[4096], 4, false, false, out _, out _);

        Assert.Equal(PoolFsStatus.DiskFull, status);
        Assert.Equal("seed", File.ReadAllText(pool.View.FindFile("growing.bin")!.HostPath));
    }

    [Fact]
    public void Constrained_io_never_extends_a_file()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "fixed.bin", "1234567890");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "fixed.bin", Access = PoolAccess.ReadWrite }, out var handle, out _);
        engine.Write(handle!, new byte[100], 8, writeToEndOfFile: false, constrainedIo: true, out var written, out _);
        engine.Close(handle!);

        Assert.Equal(2, written);
        Assert.Equal(10, new FileInfo(pool.View.FindFile("fixed.bin")!.HostPath).Length);
    }

    [Fact]
    public void WriteToEndOfFile_appends()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "log.txt", "start");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "log.txt", Access = PoolAccess.ReadWrite }, out var handle, out _);
        engine.Write(handle!, "-end"u8, 0, writeToEndOfFile: true, constrainedIo: false, out _, out _);
        engine.Close(handle!);

        Assert.Equal("start-end", File.ReadAllText(pool.View.FindFile("log.txt")!.HostPath));
    }

    [Fact]
    public void SetFileSize_truncates_and_extends()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "sized.bin", "1234567890");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "sized.bin", Access = PoolAccess.ReadWrite }, out var handle, out _);

        Assert.Equal(PoolFsStatus.Success, engine.SetFileSize(handle!, 4, setAllocationSize: false, out var info));
        Assert.Equal(4, info!.Length);

        Assert.Equal(PoolFsStatus.Success, engine.SetFileSize(handle!, 20, setAllocationSize: false, out info));
        Assert.Equal(20, info!.Length);

        engine.Close(handle!);
    }

    [Fact]
    public void SetBasicInfo_passes_timestamps_through_to_the_drive()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "stamped.txt", "x");
        var engine = Engine(pool);

        var when = new DateTimeOffset(2021, 6, 5, 4, 3, 2, TimeSpan.Zero);
        engine.Open(new PoolOpenRequest { PoolPath = "stamped.txt", Access = PoolAccess.ReadWrite }, out var handle, out _);
        var status = engine.SetBasicInfo(handle!, attributes: null, when, when, when, out var info);
        engine.Close(handle!);

        Assert.Equal(PoolFsStatus.Success, status);
        Assert.Equal(when.UtcDateTime, info!.LastWriteTimeUtc.UtcDateTime);
        Assert.Equal(when.UtcDateTime, File.GetLastWriteTimeUtc(pool.View.FindFile("stamped.txt")!.HostPath));
    }

    [Fact]
    public void CanDelete_refuses_a_non_empty_folder_and_the_root()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        pool.WriteInto(a, "Full/file.txt", "x");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "Full", IsDirectory = true }, out var full, out _);
        engine.Open(new PoolOpenRequest { PoolPath = "", IsDirectory = true }, out var root, out _);

        Assert.Equal(PoolFsStatus.DirectoryNotEmpty, engine.CanDelete(full!));
        Assert.Equal(PoolFsStatus.CannotDelete, engine.CanDelete(root!));
    }

    [Fact]
    public void Cleanup_with_delete_removes_the_file_from_every_drive()
    {
        using var pool = new TempPool();
        var (_, a) = pool.AddDrive("A");
        var (_, b) = pool.AddDrive("B");
        pool.WriteInto(a, "dup.txt", "a");
        pool.WriteInto(b, "dup.txt", "b");
        var engine = Engine(pool);

        engine.Open(new PoolOpenRequest { PoolPath = "dup.txt", Access = PoolAccess.ReadWrite }, out var handle, out _);
        Assert.Equal(PoolFsStatus.Success, engine.Cleanup(handle!, delete: true));
        engine.Close(handle!);

        Assert.Empty(pool.View.FindAllFiles("dup.txt"));
    }

    [Fact]
    public void Volume_info_is_the_sum_of_the_drives_we_can_see()
    {
        using var pool = new TempPool();
        var (volumeA, _) = pool.AddDrive("A", totalBytes: 1000, freeBytes: 400);
        pool.AddDrive("B", totalBytes: 2000, freeBytes: 600);
        var engine = Engine(pool);

        var info = engine.GetVolumeInfo();
        Assert.Equal(3000, info.TotalBytes);
        Assert.Equal(1000, info.FreeBytes);

        pool.Volumes.SetPresent(volumeA, present: false);
        Assert.Equal(2000, engine.GetVolumeInfo().TotalBytes);
    }

    [Fact]
    public void An_offline_pool_reports_device_not_ready_rather_than_an_empty_pool()
    {
        using var pool = new TempPool();
        var (volumeA, _) = pool.AddDrive("A");
        pool.Volumes.SetPresent(volumeA, present: false);
        var engine = Engine(pool);

        Assert.Equal(PoolFsStatus.DeviceNotReady, engine.GetFileInfo("any.txt", out _));
        Assert.Equal(PoolFsStatus.DeviceNotReady, engine.Open(new PoolOpenRequest { PoolPath = "any.txt" }, out _, out _));
    }

    [Fact]
    public void Allocation_size_steers_a_new_file_to_a_drive_that_fits_it()
    {
        using var pool = new TempPool();
        pool.AddDrive("Small", totalBytes: 10_000, freeBytes: 1_000);
        var (_, big) = pool.AddDrive("Big", totalBytes: 10_000_000, freeBytes: 900_000);
        var engine = Engine(pool);

        engine.Open(CreateRequest("preallocated.bin", allocationSize: 50_000), out var handle, out _);
        engine.Close(handle!);

        Assert.Equal(big, handle!.PartId);
    }
}
