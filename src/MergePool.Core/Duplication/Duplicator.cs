using MergePool.Core.Config;
using MergePool.Core.Model;
using MergePool.Core.Union;

namespace MergePool.Core.Duplication;

public sealed record DuplicationRunResult
{
    public required int CopiesAdded { get; init; }

    public required int CopiesRefreshed { get; init; }

    public required int CopiesRemoved { get; init; }

    public required long BytesWritten { get; init; }

    /// <summary>Files passed over this time: in use, or vanished while the plan was being worked.</summary>
    public required int Skipped { get; init; }

    public required IReadOnlyList<string> Problems { get; init; }

    public static DuplicationRunResult Nothing { get; } = new()
    {
        CopiesAdded = 0,
        CopiesRefreshed = 0,
        CopiesRemoved = 0,
        BytesWritten = 0,
        Skipped = 0,
        Problems = Array.Empty<string>(),
    };
}

/// <summary>
/// Makes the copies a <see cref="DuplicationPlan"/> asks for.
/// </summary>
/// <remarks>
/// <para>As timid as the rebalancer, for the same reasons: a capped write rate so real IO always
/// wins, pausable between files, and a file something else has open is left for next time rather
/// than fought over.</para>
/// <para>A copy is written to the part's hidden work folder and only then moved into place, so a
/// half-written copy is never visible in the pool — which matters more here than anywhere else,
/// because the union view reads the newest copy, and a half-written one would be the newest.</para>
/// </remarks>
public sealed class Duplicator(
    IPoolTopology topology,
    DuplicationOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly IPoolTopology _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly DuplicationOptions _options = options ?? new DuplicationOptions();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ManualResetEventSlim _resumed = new(initialState: true);

    public bool IsPaused => !_resumed.IsSet;

    public event EventHandler<DuplicationAction>? ActionApplied;

    /// <summary>Stops after the copy in flight. Used while the pool is busy or upgrading.</summary>
    public void Pause() => _resumed.Reset();

    public void Resume() => _resumed.Set();

    public async Task<DuplicationRunResult> RunAsync(DuplicationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.IsEmpty)
        {
            return DuplicationRunResult.Nothing;
        }

        var added = 0;
        var refreshed = 0;
        var removed = 0;
        var written = 0L;
        var skipped = 0;
        var problems = new List<string>();

        var snapshot = _topology.Current;

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _resumed.Wait(cancellationToken);

            var part = snapshot.FindPart(action.TargetPartId);
            if (part is null || !part.IsOnline)
            {
                skipped++;
                continue;
            }

            try
            {
                // The boundary, checked again at the moment of writing rather than only when the
                // plan was made: MergePool only ever touches what is inside a pool part.
                PoolOperations.GuardInsidePart(part, action.TargetHostPath);

                switch (action.Kind)
                {
                    case DuplicationActionKind.RemoveCopy:
                        if (Remove(action))
                        {
                            removed++;
                            ActionApplied?.Invoke(this, action);
                        }
                        else
                        {
                            skipped++;
                        }

                        break;

                    default:
                        var bytes = await CopyAsync(action, part, cancellationToken).ConfigureAwait(false);
                        if (bytes < 0)
                        {
                            skipped++;
                            break;
                        }

                        written += bytes;
                        if (action.Kind is DuplicationActionKind.AddCopy)
                        {
                            added++;
                        }
                        else
                        {
                            refreshed++;
                        }

                        ActionApplied?.Invoke(this, action);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{action.PoolPath}: {exception.Message}");
            }
        }

        return new DuplicationRunResult
        {
            CopiesAdded = added,
            CopiesRefreshed = refreshed,
            CopiesRemoved = removed,
            BytesWritten = written,
            Skipped = skipped,
            Problems = problems,
        };
    }

    /// <summary>
    /// Writes one copy. Returns the bytes written, or -1 when the file was left for next time.
    /// </summary>
    private async Task<long> CopyAsync(DuplicationAction action, PoolPart target, CancellationToken cancellationToken)
    {
        if (action.SourceHostPath is null || !File.Exists(action.SourceHostPath))
        {
            // Deleted or renamed since the plan was made. The next pass sees the new truth.
            return -1;
        }

        var workFolder = Path.Combine(target.RequireRootPath(), PoolPartLayout.WorkFolderName);
        PoolOperations.GuardInsidePart(target, workFolder);
        Directory.CreateDirectory(workFolder);

        var staging = Path.Combine(workFolder, Guid.NewGuid().ToString("N") + ".copy");

        try
        {
            long copied;

            using (var source = TryOpenForReading(action.SourceHostPath))
            {
                if (source is null)
                {
                    // Something else has it open for writing. Copying it now would copy a file
                    // mid-change, so it waits.
                    return -1;
                }

                using var destination = new FileStream(staging, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                });

                copied = await CopyThrottledAsync(source, destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The copy carries the source's timestamps, so it is not mistaken for a newer version
            // of the file it is a copy of — the union view reads by last-write time.
            CopyTimestamps(action.SourceHostPath, staging);

            var parent = Path.GetDirectoryName(action.TargetHostPath);
            if (!string.IsNullOrEmpty(parent))
            {
                PoolOperations.GuardInsidePart(target, parent);
                Directory.CreateDirectory(parent);
            }

            File.Move(staging, action.TargetHostPath, overwrite: true);
            return copied;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Copies at the configured rate. The pause is checked between blocks, so pausing takes effect
    /// part-way through a large file rather than after it.
    /// </summary>
    private async Task<long> CopyThrottledAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[BlockSize];
        var budget = Math.Max(1, _options.MegabytesPerSecond) * 1024L * 1024L;
        var total = 0L;
        var windowBytes = 0L;
        var windowStarted = _timeProvider.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _resumed.Wait(cancellationToken);

            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            windowBytes += read;

            var elapsed = _timeProvider.GetElapsedTime(windowStarted);
            if (windowBytes < budget)
            {
                continue;
            }

            var owed = TimeSpan.FromSeconds(1) - elapsed;
            if (owed > TimeSpan.Zero)
            {
                await Task.Delay(owed, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            windowBytes = 0;
            windowStarted = _timeProvider.GetTimestamp();
        }
    }

    private static bool Remove(DuplicationAction action)
    {
        if (!File.Exists(action.TargetHostPath))
        {
            return false;
        }

        File.Delete(action.TargetHostPath);
        return true;
    }

    /// <summary>
    /// Opens a copy's source without disturbing anything else using it. Returns null when it is
    /// open for writing somewhere: a file being changed is not a file to copy.
    /// </summary>
    private static FileStream? TryOpenForReading(string path)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CopyTimestamps(string source, string target)
    {
        try
        {
            var info = new FileInfo(source);
            File.SetCreationTimeUtc(target, info.CreationTimeUtc);
            File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // A copy with its own timestamps is still a correct copy; it just gets compared again.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const int BlockSize = 1024 * 1024;
}
