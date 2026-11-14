using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

namespace MergePool.Ui.Services;

/// <summary>
/// Writes what the window did on its way up, and what killed it if something did.
/// </summary>
/// <remarks>
/// <para>This exists because of a report that said only "the app GUI is not loading", and there was
/// nothing anywhere to say more. The window had an unhandled-exception handler, but it is attached
/// in <c>OnStartup</c> — and the two failures that look exactly like "it does not load" both happen
/// before that: a resource dictionary that will not parse takes the application down inside
/// <c>InitializeComponent</c>, before any of our code runs, and a window that starts in the
/// notification area looks identical to one that never appeared.</para>
/// <para>So the log is written from the application's constructor onwards, to the configuration
/// folder rather than the program folder, because an update replaces the program folder and this is
/// the one thing you want to still be there afterwards. It is small, it is overwritten each start
/// (with the previous run kept beside it), and it is plain text someone can paste into an issue.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StartupLog
{
    private static readonly object Gate = new();

    private static string? _path;

    /// <summary>Where the log is, once it has been opened. Shown to the user in an error.</summary>
    public static string? Path => _path;

    /// <summary>
    /// Starts a log for this run, keeping the previous one as <c>ui.previous.log</c> — the run that
    /// failed is usually the one before the one you are looking at.
    /// </summary>
    public static void Begin(string version)
    {
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MergePool");

            Directory.CreateDirectory(folder);

            var current = System.IO.Path.Combine(folder, "ui.log");
            var previous = System.IO.Path.Combine(folder, "ui.previous.log");

            if (File.Exists(current))
            {
                File.Copy(current, previous, overwrite: true);
            }

            File.WriteAllText(
                current,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"MergePool window {version} starting at {DateTimeOffset.Now:u}{Environment.NewLine}"),
                Encoding.UTF8);

            _path = current;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException)
        {
            // A window that cannot write a log still has to start.
            _path = null;
        }
    }

    /// <summary>Records one step. Cheap, and never throws.</summary>
    public static void Write(string message)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    _path,
                    string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}"),
                    Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records a failure, with everything that would be needed to act on it.</summary>
    public static void Failure(string where, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Write($"FAILED in {where}: {exception.GetType().Name}: {exception.Message}");

        // The stack is the part that identifies which resource or binding broke, so it goes in even
        // though the rest of the log is deliberately terse.
        Write(exception.ToString());
    }
}
