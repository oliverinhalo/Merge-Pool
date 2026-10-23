using System.Diagnostics;
using MergePool.Core.FileSystem;
using MergePool.Core.Model;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// The failures this file guards against were all reported from a production pool backing a Docker
/// Desktop host: the mount going completely unresponsive under concurrent read+write, a healthy
/// mount transiently listing a populated folder as empty, and ~1-2 MB/s throughput off four NVMe
/// drives.
/// </summary>
/// <remarks>
/// <para>They share one cause: work that blocks — a machine-wide volume scan, a seek on a shared
/// stream, a relocation swapping a stream out — happening while a lock every other file system
/// callback needs is held. A file system cannot do that. A blocked callback is a thread blocked
/// inside the kernel's I/O path, so the process waiting on it is in an uninterruptible wait and
/// cannot be killed, and WinFsp's dispatcher pool is finite: a handful of blocked callbacks takes
/// the whole volume down.</para>
/// <para>Every test here is written to fail against the code as it was reported, and each one names
/// the symptom it stands for.</para>
/// </remarks>
public sealed class ConcurrentIoTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static PoolSnapshot EmptySnapshot(Guid poolId) => new() { PoolId = poolId, Parts = [] };

    /// <summary>
    /// Symptom 1, the hang: resolving the topology asks Windows about every volume on the machine,
    /// and the pool's own mount is one of them. That query comes back into the file system as a
    /// <c>GetVolumeInfo</c> callback on another thread, which needs the topology too. Hold a lock
    /// across the resolve and the two threads wait on each other for ever.
    /// </summary>
    [Fact]
    public async Task Resolving_the_topology_can_be_re_entered_from_another_thread_without_deadlocking()
    {
        var poolId = Guid.NewGuid();
        CachingPoolTopology? topology = null;
        var reentered = new ManualResetEventSlim(false);

        topology = new CachingPoolTopology(
            () =>
            {
                // What the kernel does on our behalf: while we are inside the resolve, the volume
                // query lands on a different thread and asks the same file system for its volume
                // information. The resolve cannot finish until that returns.
                var inner = Task.Run(() =>
                {
                    _ = topology!.Current;
                    reentered.Set();
                });

                if (!inner.Wait(Patience))
                {
                    throw new TimeoutException("A volume query against our own mount never returned.");
                }

                return EmptySnapshot(poolId);
            },
            TimeSpan.FromMilliseconds(1));

        var resolved = Task.Run(() => topology.Current);
        var finished = await Task.WhenAny(resolved, Task.Delay(Patience));

        Assert.True(
            ReferenceEquals(finished, resolved),
            "Reading the topology deadlocked against a volume query of our own mount — the reported hang.");
        Assert.True(reentered.Wait(Patience));
    }

    /// <summary>
    /// Symptom 1 and 3: one slow resolve must not stop every other callback. A dead network mapping
    /// or a drive busy under load can take many seconds to answer, and that is a drive the pool does
    /// not even use.
    /// </summary>
    [Fact]
    public async Task A_slow_resolve_does_not_block_readers_that_already_have_a_snapshot()
    {
        var poolId = Guid.NewGuid();
        var resolves = 0;
        var releaseResolve = new ManualResetEventSlim(false);

        var topology = new CachingPoolTopology(
            () =>
            {
                // The first resolve answers at once so there is something cached; the second blocks,
                // standing in for a volume that has stopped answering.
                if (Interlocked.Increment(ref resolves) > 1)
                {
                    releaseResolve.Wait(Patience);
                }

                return EmptySnapshot(poolId);
            },
            TimeSpan.FromMilliseconds(1));

        _ = topology.Current;
        Thread.Sleep(20);

        // A refresh is now due, and the resolve for it will block. Readers must keep being served
        // the snapshot we already have rather than queueing behind it.
        var readers = Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var round = 0; round < 20; round++)
            {
                _ = topology.Current;
            }
        })));

        var finished = await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(5)));
        releaseResolve.Set();

        Assert.True(
            ReferenceEquals(finished, readers),
            "Readers queued behind one stalled volume query instead of being served the cached topology.");
    }

    /// <summary>
    /// Symptom 1 and 3, the data path: WinFsp dispatches reads and writes for one handle on many
    /// threads at once. Seeking a shared <see cref="FileStream"/> per call means two threads
    /// interleave a position set with another thread's read, and the read returns another offset's
    /// bytes. Silent wrong data is worse than the hang it came with.
    /// </summary>
    [Fact]
    public void Concurrent_reads_of_one_handle_each_get_the_bytes_they_asked_for()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");
        var engine = new PoolFileSystemEngine(pool, pool.Placement, relocator: null,
            new PoolFileSystemOptions { RelocationReserveBytes = 0 });

        // Each 4 KiB block is filled with its own block number, so a read landing at the wrong
        // offset is obvious rather than plausible.
        const int blockSize = 4096;
        const int blocks = 64;
        var payload = new byte[blockSize * blocks];
        for (var block = 0; block < blocks; block++)
        {
            Array.Fill(payload, (byte)(block & 0xFF), block * blockSize, blockSize);
        }

        Assert.Equal(PoolFsStatus.Success, engine.Open(
            new PoolOpenRequest
            {
                PoolPath = "big.bin",
                Disposition = PoolCreateDisposition.Create,
                Access = PoolAccess.ReadWrite,
                Share = PoolShare.All,
            },
            out var writer,
            out _));

        Assert.Equal(PoolFsStatus.Success, engine.Write(writer!, payload, 0, false, false, out _, out _));
        engine.Close(writer!);

        Assert.Equal(PoolFsStatus.Success, engine.Open(
            new PoolOpenRequest { PoolPath = "big.bin", Access = PoolAccess.Read, Share = PoolShare.All },
            out var handle,
            out _));

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 8, worker =>
        {
            var buffer = new byte[blockSize];

            for (var round = 0; round < 40; round++)
            {
                var block = (worker * 7 + round * 3) % blocks;
                var status = engine.Read(handle!, buffer, (long)block * blockSize, out var read);

                if (status != PoolFsStatus.Success || read != blockSize)
                {
                    failures.Add($"block {block}: status {status}, {read} bytes");
                    continue;
                }

                if (buffer.Any(b => b != (byte)(block & 0xFF)))
                {
                    failures.Add($"block {block}: got block {buffer[0]}'s bytes");
                }
            }
        });

        engine.Close(handle!);

        Assert.True(
            failures.IsEmpty,
            "Concurrent reads of one handle returned the wrong data or failed: "
            + string.Join("; ", failures.Take(5)));
    }

    /// <summary>
    /// Symptom 1, the reported trigger: one process reading a large file through the pool while
    /// another writes into it. Reads and writes of the same handle must not corrupt each other, and
    /// neither must hang.
    /// </summary>
    [Fact]
    public async Task Reading_a_file_while_another_thread_writes_it_neither_hangs_nor_tears()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");
        var engine = new PoolFileSystemEngine(pool, pool.Placement, relocator: null,
            new PoolFileSystemOptions { RelocationReserveBytes = 0 });

        const int blockSize = 4096;
        var zeroes = new byte[blockSize];

        Assert.Equal(PoolFsStatus.Success, engine.Open(
            new PoolOpenRequest
            {
                PoolPath = "stream.bin",
                Disposition = PoolCreateDisposition.Create,
                Access = PoolAccess.ReadWrite,
                Share = PoolShare.All,
            },
            out var handle,
            out _));

        // A 1 MiB file of zeroes to read back while the tail is being appended to.
        for (var block = 0; block < 256; block++)
        {
            Assert.Equal(PoolFsStatus.Success,
                engine.Write(handle!, zeroes, (long)block * blockSize, false, false, out _, out _));
        }

        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var torn = 0;

        var writing = Task.Run(() =>
        {
            var block = new byte[blockSize];
            Array.Fill(block, (byte)0xAB);
            var offset = 256L * blockSize;

            while (!stop.IsCancellationRequested)
            {
                engine.Write(handle!, block, offset, false, false, out _, out _);
                offset += blockSize;
            }
        });

        var reading = Task.Run(() =>
        {
            var buffer = new byte[blockSize];

            while (!stop.IsCancellationRequested)
            {
                // Reads the region that is only ever zeroes. Anything else means a read saw another
                // thread's offset.
                if (engine.Read(handle!, buffer, 0, out var read) == PoolFsStatus.Success
                    && buffer.Take(read).Any(b => b != 0))
                {
                    Interlocked.Increment(ref torn);
                }
            }
        });

        var both = Task.WhenAll(writing, reading);
        var finished = await Task.WhenAny(both, Task.Delay(Patience));
        stop.Cancel();
        engine.Close(handle!);

        Assert.True(
            ReferenceEquals(finished, both),
            "A concurrent read and write of one handle hung — the reported symptom.");
        Assert.Equal(0, torn);
    }

    /// <summary>
    /// Symptom 2, the dangerous one: a folder with real content in it listed as empty, and
    /// successfully, so an application checking for a marker file concludes the data is gone. A part
    /// that is unreachable right now is not a part with no files in it.
    /// </summary>
    [Fact]
    public void A_folder_whose_drive_is_unreachable_fails_rather_than_listing_empty()
    {
        using var pool = new TempPool();
        var (_, partId) = pool.AddDrive("A");
        pool.WriteInto(partId, "Photos/2026/img001.jpg", "content");

        // A second drive that carries the folder but none of the files: the ordinary state of a
        // pool, because directories span drives and files do not. It is what makes this dangerous —
        // the folder still resolves, so the listing succeeds, and it is empty.
        var (_, otherPartId) = pool.AddDrive("B");
        Directory.CreateDirectory(Path.Combine(pool.PartRoot(otherPartId), "Photos", "2026"));

        var good = pool.Current;
        var engine = new PoolFileSystemEngine(
            new FixedTopology(good), pool.Placement, relocator: null, new PoolFileSystemOptions());

        Assert.Equal(PoolFsStatus.Success, Listing(engine, "Photos/2026", out var entries));
        Assert.Single(entries!);

        // Now the drive holding those files stops answering: a volume query that timed out, an
        // enclosure that re-enumerated, a letter in flux. The files are still on the disk.
        var hiccup = good with
        {
            Parts =
            [
                .. good.Parts.Select(part => part.PartId == partId
                    ? part with { State = PoolPartState.Unavailable, RootPath = null }
                    : part),
            ],
        };

        var incomplete = new PoolFileSystemEngine(
            new FixedTopology(hiccup), pool.Placement, relocator: null, new PoolFileSystemOptions());

        var status = Listing(incomplete, "Photos/2026", out var afterwards);

        Assert.Equal(PoolFsStatus.DeviceNotReady, status);
        Assert.True(
            afterwards is null or { Count: 0 },
            "A listing that cannot be served must not come back as a successful empty folder.");

        // A drive that is genuinely gone is different: that is a degraded pool, which MergePool
        // supports, and its listings are served as fact.
        var removed = good with
        {
            Parts =
            [
                .. good.Parts.Select(part => part.PartId == partId
                    ? part with { State = PoolPartState.Offline, RootPath = null }
                    : part),
            ],
        };

        var degraded = new PoolFileSystemEngine(
            new FixedTopology(removed), pool.Placement, relocator: null, new PoolFileSystemOptions());

        Assert.Equal(PoolFsStatus.Success, Listing(degraded, "Photos/2026", out var whenDegraded));
        Assert.Empty(whenDegraded!);
    }

    /// <summary>Opens a directory through the engine and reads it, as the mount does.</summary>
    private static PoolFsStatus Listing(
        PoolFileSystemEngine engine,
        string poolPath,
        out IReadOnlyList<PoolFileInfo>? entries)
    {
        entries = null;

        var opened = engine.Open(
            new PoolOpenRequest { PoolPath = poolPath, IsDirectory = true, Share = PoolShare.All },
            out var handle,
            out _);

        if (opened != PoolFsStatus.Success || handle is null)
        {
            return opened;
        }

        try
        {
            return engine.TryReadDirectory(handle, pattern: null, marker: null, out entries);
        }
        finally
        {
            engine.Close(handle);
        }
    }

    /// <summary>
    /// Symptom 2's root cause: a resolve that fails — a volume query that threw, a drive that did
    /// not answer in time — must not be published as a pool with no drives in it. The last topology
    /// we successfully resolved is a far better answer than an empty one.
    /// </summary>
    [Fact]
    public void A_failed_resolve_keeps_the_last_good_topology_instead_of_emptying_the_pool()
    {
        var poolId = Guid.NewGuid();
        var populated = new PoolSnapshot
        {
            PoolId = poolId,
            Parts =
            [
                new PoolPart
                {
                    PartId = Guid.NewGuid(),
                    Volume = Volumes.VolumeId.Empty,
                    RootPath = "/pool/a",
                    State = PoolPartState.Online,
                    TotalBytes = 1000,
                    FreeBytes = 900,
                },
            ],
        };

        var fail = false;
        var topology = new CachingPoolTopology(
            () => fail ? throw new IOException("the volume did not answer") : populated,
            TimeSpan.FromMilliseconds(1));

        Assert.Single(topology.Current.Parts);

        fail = true;
        Thread.Sleep(20);

        // Several reads, because the refresh is allowed to happen in the background: none of them
        // may report a pool that has lost its drives.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Single(topology.Current.Parts);
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Symptom 3: per-call overhead. A megabyte of 64 KiB writes — what one second of a real copy
    /// looks like — must not each pay for a machine-wide volume scan.
    /// </summary>
    [Fact]
    public void A_run_of_writes_does_not_resolve_the_topology_once_per_write()
    {
        using var pool = new TempPool();
        pool.AddDrive("A");

        var resolves = 0;
        var topology = new CachingPoolTopology(
            () =>
            {
                Interlocked.Increment(ref resolves);
                return pool.Current;
            },
            TimeSpan.FromSeconds(2));

        var engine = new PoolFileSystemEngine(topology, pool.Placement, relocator: null,
            new PoolFileSystemOptions { RelocationReserveBytes = 0 });

        Assert.Equal(PoolFsStatus.Success, engine.Open(
            new PoolOpenRequest
            {
                PoolPath = "copy.bin",
                Disposition = PoolCreateDisposition.Create,
                Access = PoolAccess.ReadWrite,
                Share = PoolShare.All,
            },
            out var handle,
            out _));

        var block = new byte[64 * 1024];
        var started = Stopwatch.StartNew();

        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(PoolFsStatus.Success,
                engine.Write(handle!, block, (long)i * block.Length, false, false, out _, out _));
        }

        started.Stop();
        engine.Close(handle!);

        Assert.True(resolves <= 2, $"16 writes resolved the topology {resolves} times.");
    }

    /// <summary>A topology that never changes, for tests that want one exact snapshot.</summary>
    private sealed class FixedTopology(PoolSnapshot snapshot) : IPoolTopology
    {
        public PoolSnapshot Current { get; } = snapshot;
    }
}
