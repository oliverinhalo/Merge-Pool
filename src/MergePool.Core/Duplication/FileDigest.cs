using System.Security.Cryptography;

namespace MergePool.Core.Duplication;

/// <summary>Content comparison for copies, used only when a rule asks for it.</summary>
internal static class FileDigest
{
    /// <summary>
    /// Whether two files hold the same bytes. Compared in blocks rather than hashed: it stops at
    /// the first difference, which for a copy that has gone wrong is usually straight away, and it
    /// never has to read either file twice.
    /// </summary>
    public static bool Same(string left, string right)
    {
        try
        {
            using var a = File.Open(left, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan,
            });

            using var b = File.Open(right, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan,
            });

            if (a.Length != b.Length)
            {
                return false;
            }

            var sizeA = new byte[BlockSize];
            var sizeB = new byte[BlockSize];

            while (true)
            {
                var readA = a.ReadAtLeast(sizeA, sizeA.Length, throwOnEndOfStream: false);
                var readB = b.ReadAtLeast(sizeB, sizeB.Length, throwOnEndOfStream: false);

                if (readA != readB)
                {
                    return false;
                }

                if (readA == 0)
                {
                    return true;
                }

                if (!sizeA.AsSpan(0, readA).SequenceEqual(sizeB.AsSpan(0, readB)))
                {
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as different, but it is not verified either, so the copy
            // is treated as needing to be written again.
            return false;
        }
    }

    /// <summary>SHA-256 of a file, for reporting a copy's identity rather than comparing two.</summary>
    public static string? TryHash(string path)
    {
        try
        {
            using var stream = File.Open(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan,
            });

            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const int BlockSize = 1024 * 1024;
}
