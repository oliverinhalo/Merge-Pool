using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MergePool.Core.Config;

namespace MergePool.Web;

public enum WebServerState
{
    Stopped = 0,
    Listening = 1,

    /// <summary>The port could not be bound — usually already in use, or not permitted.</summary>
    Failed = 2,
}

public sealed record WebServerStatus
{
    public required WebServerState State { get; init; }

    public int Port { get; init; }

    public WebAccessScope Scope { get; init; }

    /// <summary>The address to type into a browser, or <c>null</c> when nothing is listening.</summary>
    public string? Url { get; init; }

    public string? Error { get; init; }

    public int RequestCount { get; init; }

    public int RejectedCount { get; init; }
}

/// <summary>
/// Serves the web interface: a small single-page app and the JSON API behind it, on a port the user
/// chooses.
/// </summary>
/// <remarks>
/// <para>Built on <see cref="HttpListener"/> rather than a web framework, deliberately. It is in the
/// base class library, so the web interface adds nothing to what has to be installed on the machine
/// — the .NET Desktop Runtime MergePool already needs is enough.</para>
/// <para>Every request must carry the access token. There is no session, no cookie and no login
/// state on the server: the browser holds the token and sends it on each call, so nothing can be
/// reached by pointing a browser at the port.</para>
/// </remarks>
public sealed class WebControlServer : IDisposable
{
    private readonly WebApi _api;
    private readonly Func<WebOptions> _options;
    private readonly Action<string, Exception?>? _log;
    private readonly object _gate = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _loop;
    private WebServerState _state = WebServerState.Stopped;
    private string? _error;
    private int _requests;
    private int _rejected;
    private int _boundPort;
    private WebAccessScope _boundScope;

    public WebControlServer(WebApi api, Func<WebOptions> options, Action<string, Exception?>? log = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    public WebServerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new WebServerStatus
                {
                    State = _state,
                    Port = _boundPort,
                    Scope = _boundScope,
                    Url = _state is WebServerState.Listening ? DescribeUrl(_boundPort, _boundScope) : null,
                    Error = _error,
                    RequestCount = _requests,
                    RejectedCount = _rejected,
                };
            }
        }
    }

    /// <summary>
    /// Brings the server in line with the configuration: starts, stops or rebinds as needed. Safe to
    /// call on every tick — it does nothing when the settings have not changed.
    /// </summary>
    public void Apply()
    {
        var options = _options();

        if (!options.Enabled || !WebOptions.IsUsablePort(options.Port))
        {
            if (!options.Enabled)
            {
                Stop();
                return;
            }

            lock (_gate)
            {
                _state = WebServerState.Failed;
                _error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Port {options.Port} is outside the usable range {WebOptions.MinimumPort}-{WebOptions.MaximumPort}.");
            }

            Stop(keepError: true);
            return;
        }

        lock (_gate)
        {
            var alreadyRight = _state is WebServerState.Listening
                && _boundPort == options.Port
                && _boundScope == options.AccessScope;

            if (alreadyRight)
            {
                return;
            }
        }

        Stop();
        Start(options);
    }

    private void Start(WebOptions options)
    {
        var listener = new HttpListener();

        // Loopback binds without any privilege; the wildcard prefix needs the service's own
        // account, which is exactly why the server lives in the service rather than the window.
        var prefix = options.AccessScope is WebAccessScope.Network
            ? string.Create(CultureInfo.InvariantCulture, $"http://+:{options.Port}/")
            : string.Create(CultureInfo.InvariantCulture, $"http://localhost:{options.Port}/");

        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
        {
            listener.Close();

            lock (_gate)
            {
                _state = WebServerState.Failed;
                _error = Describe(exception, options.Port);
                _boundPort = options.Port;
                _boundScope = options.AccessScope;
            }

            _log?.Invoke($"The web interface could not listen on port {options.Port}: {exception.Message}", exception);
            return;
        }

        var shutdown = new CancellationTokenSource();

        lock (_gate)
        {
            _listener = listener;
            _shutdown = shutdown;
            _state = WebServerState.Listening;
            _error = null;
            _boundPort = options.Port;
            _boundScope = options.AccessScope;
        }

        _loop = Task.Run(() => AcceptAsync(listener, shutdown.Token));
        _log?.Invoke($"The web interface is listening at {DescribeUrl(options.Port, options.AccessScope)}.", null);
    }

    public void Stop(bool keepError = false)
    {
        HttpListener? listener;
        CancellationTokenSource? shutdown;

        lock (_gate)
        {
            listener = _listener;
            shutdown = _shutdown;
            _listener = null;
            _shutdown = null;

            if (_state is WebServerState.Listening)
            {
                _state = WebServerState.Stopped;
            }

            if (!keepError && _state is not WebServerState.Failed)
            {
                _error = null;
            }
        }

        shutdown?.Cancel();

        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException)
        {
            // Already gone.
        }

        shutdown?.Dispose();
    }

    private async Task AcceptAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            // One slow request must not hold up the next.
            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            Interlocked.Increment(ref _requests);

            var options = _options();
            var path = context.Request.Url?.AbsolutePath ?? "/";

            // The page and its assets are public: they contain nothing but the shell that asks for
            // the token. Everything that reads or changes anything sits behind the check below.
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                await ServePageAsync(context, path).ConfigureAwait(false);
                return;
            }

            if (!WebAccessToken.Matches(options.AccessToken, PresentedToken(context.Request)))
            {
                Interlocked.Increment(ref _rejected);
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.Unauthorized,
                    new { error = "The access token is missing or wrong." }).ConfigureAwait(false);

                return;
            }

            var response = await _api.HandleAsync(
                new WebRequest
                {
                    Method = context.Request.HttpMethod,
                    Path = path,
                    Query = context.Request.Url?.Query ?? string.Empty,
                    Body = await ReadBodyAsync(context.Request).ConfigureAwait(false),
                    AllowChanges = options.AllowChanges,
                },
                CancellationToken.None).ConfigureAwait(false);

            await WriteJsonAsync(context, response.Status, response.Payload).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException)
        {
            // The browser went away mid-response. Nothing to report and nothing to fix.
        }
        catch (Exception exception)
        {
            _log?.Invoke("The web interface failed to handle a request.", exception);

            try
            {
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.InternalServerError,
                    new { error = "MergePool could not handle that request." }).ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is IOException or HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Accepts the token as a bearer header, or as a header of its own for simple clients.</summary>
    private static string? PresentedToken(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        return request.Headers["X-MergePool-Token"];
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task ServePageAsync(HttpListenerContext context, string path)
    {
        var name = path is "/" or "" ? "index.html" : path.TrimStart('/');

        var asset = ReadAsset(name);
        if (asset is null)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = ContentTypeFor(name);
        context.Response.ContentEncoding = Encoding.UTF8;

        // The page holds the token in memory only, so caching it would be pointless and confusing
        // after an upgrade.
        context.Response.Headers["Cache-Control"] = "no-store";

        var bytes = Encoding.UTF8.GetBytes(asset);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string? ReadAsset(string name)
    {
        var assembly = typeof(WebControlServer).Assembly;
        var resource = $"MergePool.Web.Assets.{name.Replace('/', '.')}";

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    private static async Task WriteJsonAsync(HttpListenerContext context, HttpStatusCode status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, WebJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";

        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string DescribeUrl(int port, WebAccessScope scope)
    {
        var host = scope is WebAccessScope.Network ? Dns.GetHostName() : "localhost";
        return string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/");
    }

    private static string Describe(Exception exception, int port) => exception switch
    {
        HttpListenerException { ErrorCode: 183 or 32 } => string.Create(
            CultureInfo.InvariantCulture,
            $"Port {port} is already being used by something else. Pick a different one."),

        HttpListenerException { ErrorCode: 5 } => string.Create(
            CultureInfo.InvariantCulture,
            $"Windows refused port {port}. It may be reserved — try a port above 1024 that nothing else uses."),

        _ => string.Create(CultureInfo.InvariantCulture, $"Port {port} could not be opened: {exception.Message}"),
    };

    public void Dispose() => Stop();
}

internal static class WebJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
