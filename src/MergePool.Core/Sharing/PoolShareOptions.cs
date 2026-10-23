using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MergePool.Core.Sharing;

/// <summary>
/// Whether a pool is shared on the network over SMB, and to whom.
/// </summary>
/// <remarks>
/// <para>This exists because of how people actually reach a pool from containers. Docker Desktop
/// cannot bind-mount a WinFsp drive — there is no block device behind it for the Linux VM to
/// recognise — so the supported route is SMB, and until now that meant creating the share by hand
/// and writing a watchdog to put it back whenever the pool remounted. MergePool knows exactly when
/// a pool is mounted, unmounted and remounted, so it is far better placed to keep the share in step
/// than a script is. See <c>docs/DOCKER.md</c>.</para>
/// <para>Off by default, and it stays off until someone has said who may connect. A pool holds
/// everything its owner has: a share that defaulted to on, or to granting Everyone, would be a
/// worse bug than any this release fixes.</para>
/// </remarks>
public sealed class PoolShareOptions
{
    /// <summary>Share this pool over SMB while it is mounted.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// The share name, as it appears in <c>\\machine\name</c>. Empty means one derived from the
    /// pool's name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Share it read-only, whatever the accounts below are otherwise allowed.</summary>
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; }

    /// <summary>
    /// The Windows accounts allowed to connect, as <c>MACHINE\\user</c> or <c>user</c>.
    /// </summary>
    /// <remarks>
    /// No default, and no share without one. MergePool will not grant <c>Everyone</c>, and will not
    /// guess an account on the owner's behalf: granting the wrong one exposes every file in the
    /// pool to the network, and the person setting this up is the only one who knows which service
    /// account their containers use.
    /// </remarks>
    [JsonPropertyName("allowedAccounts")]
    public List<string> AllowedAccounts { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = [];

    /// <summary>Whether this is a complete enough description to create a share from.</summary>
    public bool IsUsable(string poolName) =>
        Enabled && AllowedAccounts.Count > 0 && ShareNameFor(poolName) is not null;

    /// <summary>
    /// Why the share cannot be created, or <c>null</c> when it can. Said in the window rather than
    /// discovered as a share that silently never appears.
    /// </summary>
    public string? Problem(string poolName)
    {
        if (!Enabled)
        {
            return null;
        }

        if (AllowedAccounts.Count == 0)
        {
            return "No account may connect yet. Add the Windows account your containers will sign in as — "
                + "MergePool will not share a pool to everyone.";
        }

        return ShareNameFor(poolName) is null
            ? "The share name has no usable characters in it. Letters, digits, spaces, dots, dashes and "
              + "underscores are allowed, up to 80 characters."
            : null;
    }

    /// <summary>
    /// The share name to use: the configured one, or one made from the pool's name. <c>null</c>
    /// when neither survives being made valid.
    /// </summary>
    public string? ShareNameFor(string poolName) =>
        Sanitise(Name) ?? Sanitise(poolName);

    /// <summary>
    /// Reduces a name to something the SMB server will take. Windows rejects a share name
    /// containing <c>" \ / [ ] : | &lt; &gt; + = ; , * ?</c>, and one ending in <c>$</c> is hidden,
    /// which would be a confusing thing to arrive at by accident.
    /// </summary>
    public static string? Sanitise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var builder = new StringBuilder(name.Length);

        foreach (var character in name.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is ' ' or '.' or '-' or '_')
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] is not ('-' or ' '))
            {
                // One dash stands in for a run of characters the SMB server will not take, so
                // "My/Pool" becomes "My-Pool" rather than "MyPool" — but not next to a space that
                // is already separating the words, which would give "Media -2026".
                builder.Append('-');
            }
        }

        var cleaned = builder.ToString().Trim(' ', '.', '-', '_');

        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, 80)];
    }
}
