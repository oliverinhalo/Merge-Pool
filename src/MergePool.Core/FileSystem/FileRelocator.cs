using MergePool.Core.Model;
using MergePool.Core.Paths;
using MergePool.Core.Union;

namespace MergePool.Core.FileSystem;

/// <summary>
/// Copies file metadata the pool must preserve when a file moves between drives. ACLs need
/// Windows APIs, so the Windows host supplies an implementation; the default is a no-op.
/// </summary>
public interface IFileSecurityCopier
{
    void CopySecurity(string sourceHostPath, string targetHostPath);

    /// <summary>Alternate data streams. Copied only on NTFS-to-NTFS moves.</summary>
    void CopyAlternateStreams(string sourceHostPath, string targetHostPath);
}

public sealed class NullFileSecurityCopier : IFileSecurityCopier
{
    public static NullFileSecurityCopier Instance { get; } = new();

    public void CopySecurity(string sourceHostPath, string targetHostPath)
    {
    }

    public void CopyAlternateStreams(string sourceHostPath, string targetHostPath)
    {
    }
}

public sealed record RelocationResult
{
    public required Guid FromPartId { get; init; }

    public required Guid ToPartId { get; init; }

    public required string NewHostPath { get; init; }

    public required long Bytes { get; init; }
}

/// <summary>
/// Moves a file whole from one drive to another. A file is never split, so a relocation is always
/// all-or-nothing: the copy lands under a temporary name and only replaces the original once it is
/// complete and flushed.
/// </summary>
public sealed class FileRelocator(IFileSecurityCopier? securityCopier = null)
{
    private const string TempSuffix = ".mergepool-move";

    private readonly IFileSecurityCopier _security = securityCopier ?? NullFileSecurityCopier.Instance;

    public int BufferSize { get; init; } = 1 << 20;

    /// <summary>
    /// Copies <paramref name="source"/> onto <paramref name="target"/>'s drive and removes the
    /// original. Throws without touching the original if the copy cannot complete.
    /// </summary>
    public RelocationResult Relocate(
        PoolLocation source,
        PoolPart target,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source.Part.PartId == target.PartId)
        {
            return new RelocationResult
            {
                FromPartId = source.Part.PartId,
                ToPartId = target.PartId,
                NewHostPath = source.HostPath,
                Bytes = new FileInfo(source.HostPath).Length,
            };
        }

        var targetPath = PoolPath.ToHostPath(target.RequireRootPath(), source.PoolPath);
        PoolOperations.GuardInsidePart(target, targetPath);
        PoolOperations.GuardInsidePart(source.Part, source.HostPath);

        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var staging = targetPath + TempSuffix;
        long copied;

        try
        {
            copied = CopyContents(source.HostPath, staging, progress, cancellationToken);
            CopyMetadata(source.HostPath, staging);
            File.Move(staging, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        File.Delete(source.HostPath);

        return new RelocationResult
        {
            FromPartId = source.Part.PartId,
            ToPartId = target.PartId,
            NewHostPath = targetPath,
            Bytes = copied,
        };
    }

    private long CopyContents(string source, string target, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);

        // Pre-sizing surfaces "out of space" before we have written anything.
        if (input.Length > 0)
        {
            output.SetLength(input.Length);
            output.Position = 0;
        }

        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            total += read;
            progress?.Report(total);
        }

        output.Flush(flushToDisk: true);
        return total;
    }

    private void CopyMetadata(string source, string target)
    {
        var info = new FileInfo(source);
        File.SetCreationTimeUtc(target, info.CreationTimeUtc);
        File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
        File.SetLastAccessTimeUtc(target, info.LastAccessTimeUtc);

        _security.CopyAlternateStreams(source, target);
        _security.CopySecurity(source, target);

        // Attributes last: a read-only target cannot be written to afterwards.
        var attributes = info.Attributes & ~FileAttributes.ReparsePoint;
        File.SetAttributes(target, attributes);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
