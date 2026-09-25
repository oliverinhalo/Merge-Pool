using System.Security.Cryptography;

namespace MergePool.Web;

/// <summary>The shared secret the web interface requires, and how requests are checked against it.</summary>
public static class WebAccessToken
{
    /// <summary>Long enough that guessing is not a strategy, short enough to retype from a screen.</summary>
    private const int TokenBytes = 24;

    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>
    /// Compares in constant time. A comparison that returns early leaks, one character at a time,
    /// how much of a guess was right.
    /// </summary>
    public static bool Matches(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(presented);

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
