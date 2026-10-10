using System.Globalization;
using System.Net;
using System.Text.Json;
using MergePool.Core.Config;
using MergePool.Core.Duplication;
using MergePool.Core.Volumes;
using MergePool.Engine;

namespace MergePool.Web;

public sealed record WebRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public string Query { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    /// <summary>False when the interface is read-only: anything that changes state is refused.</summary>
    public bool AllowChanges { get; init; } = true;
}

public sealed record WebResponse
{
    public required HttpStatusCode Status { get; init; }

    public required object Payload { get; init; }

    public static WebResponse Ok(object payload) => new() { Status = HttpStatusCode.OK, Payload = payload };

    public static WebResponse Error(HttpStatusCode status, string message) =>
        new() { Status = status, Payload = new { error = message } };
}

/// <summary>
/// The JSON API the browser talks to. Everything it can do, the window can do too — this is the
/// same engine, reached from a different place, not a second way of managing pools.
/// </summary>
public sealed class WebApi(PoolEngine engine, AutoUpdateService? updates = null)
{
    private readonly PoolEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public Task<WebResponse> HandleAsync(WebRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Task.FromResult(Route(request, cancellationToken));
        }
        catch (KeyNotFoundException exception)
        {
            return Task.FromResult(WebResponse.Error(HttpStatusCode.NotFound, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Task.FromResult(WebResponse.Error(HttpStatusCode.Conflict, exception.Message));
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(WebResponse.Error(HttpStatusCode.BadRequest, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(WebResponse.Error(HttpStatusCode.Forbidden, exception.Message));
        }
        catch (IOException exception)
        {
            return Task.FromResult(WebResponse.Error(HttpStatusCode.ServiceUnavailable, exception.Message));
        }
    }

    private WebResponse Route(WebRequest request, CancellationToken cancellationToken)
    {
        var segments = request.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /api/...
        if (segments.Length < 2 || segments[0] != "api")
        {
            return WebResponse.Error(HttpStatusCode.NotFound, "No such endpoint.");
        }

        var reading = string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);

        if (!reading && !request.AllowChanges)
        {
            return WebResponse.Error(
                HttpStatusCode.Forbidden,
                "The web interface is set to look but not touch. Turn on changes in MergePool's settings.");
        }

        return (segments[1], segments.Length) switch
        {
            ("status", 2) when reading => WebResponse.Ok(Status()),
            ("drives", 2) when reading => WebResponse.Ok(new { drives = Drives() }),
            ("pools", 2) when reading => WebResponse.Ok(new { pools = Pools() }),
            ("pools", 3) when reading => WebResponse.Ok(Pool(ParseGuid(segments[2]))),
            ("pools", _) => PoolAction(segments, request),
            ("update", 3) => UpdateAction(segments[2], reading, cancellationToken),
            _ => WebResponse.Error(HttpStatusCode.NotFound, "No such endpoint."),
        };
    }

    private WebResponse PoolAction(string[] segments, WebRequest request)
    {
        var poolId = ParseGuid(segments[2]);

        // /api/pools/{id}/{action}
        if (segments.Length == 4)
        {
            return segments[3] switch
            {
                "mount" => Post(request, () =>
                {
                    _engine.Mount(poolId);
                    return Pool(poolId);
                }),

                "unmount" => Post(request, () =>
                {
                    _engine.Unmount(poolId);
                    return Pool(poolId);
                }),

                "rebalance" => Post(request, () =>
                {
                    var result = _engine.RebalanceOnce(poolId);
                    return new
                    {
                        moved = result.MovedCount,
                        movedBytes = result.MovedBytes,
                        skipped = result.SkippedInUse,
                        failed = result.Failed,
                        pool = Pool(poolId),
                    };
                }),

                "drives" => DriveAction(poolId, request),
                "protected" => ProtectedFolders(poolId, request),
                _ => WebResponse.Error(HttpStatusCode.NotFound, "No such endpoint."),
            };
        }

        // /api/pools/{id}/drives/{volumeId}
        if (segments.Length == 5 && segments[3] == "drives")
        {
            return RemoveDrive(poolId, segments[4], request);
        }

        // /api/pools/{id}/protected/{ruleId}, and /api/pools/{id}/protected/run
        if (segments.Length == 5 && segments[3] == "protected")
        {
            return string.Equals(segments[4], "run", StringComparison.OrdinalIgnoreCase)
                ? RunDuplication(poolId, request)
                : StopProtecting(poolId, segments[4], request);
        }

        return WebResponse.Error(HttpStatusCode.NotFound, "No such endpoint.");
    }

    // ---- Folders kept on more than one drive ----

    private WebResponse ProtectedFolders(Guid poolId, WebRequest request)
    {
        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Ok(Duplication(poolId));
        }

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use GET to read or POST to protect a folder.");
        }

        var body = Parse(request.Body);

        var folder = body.TryGetProperty("folder", out var folderValue) && folderValue.ValueKind == JsonValueKind.String
            ? folderValue.GetString() ?? string.Empty
            : string.Empty;

        var everyDrive = body.TryGetProperty("everyDrive", out var every) && every.ValueKind == JsonValueKind.True;

        var copies = body.TryGetProperty("copies", out var copiesValue) && copiesValue.TryGetInt32(out var parsed)
            ? parsed
            : DuplicationRule.DefaultCopies;

        if (!everyDrive && copies < DuplicationRule.MinimumCopies)
        {
            return WebResponse.Error(
                HttpStatusCode.BadRequest,
                $"A protected folder keeps at least {DuplicationRule.MinimumCopies} copies; one copy is not a copy.");
        }

        var rule = new DuplicationRule
        {
            Folder = folder,
            Mode = everyDrive ? DuplicationMode.AllDrives : DuplicationMode.Copies,
            Copies = copies,
            VerifyByContent = body.TryGetProperty("verifyByContent", out var verify) && verify.ValueKind == JsonValueKind.True,
            Surplus = body.TryGetProperty("keepSurplus", out var keep) && keep.ValueKind == JsonValueKind.True
                ? SurplusCopies.Keep
                : SurplusCopies.Remove,
        };

        if (body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && Guid.TryParse(id.GetString(), out var ruleId))
        {
            rule.Id = ruleId;
        }

        _engine.SetDuplicationRule(poolId, rule);
        return WebResponse.Ok(Duplication(poolId));
    }

    private WebResponse StopProtecting(Guid poolId, string rawRuleId, WebRequest request)
    {
        if (!string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use DELETE to stop protecting a folder.");
        }

        if (!Guid.TryParse(rawRuleId, out var ruleId))
        {
            return WebResponse.Error(HttpStatusCode.BadRequest, $"'{rawRuleId}' is not a protected folder.");
        }

        if (!_engine.RemoveDuplicationRule(poolId, ruleId))
        {
            return WebResponse.Error(HttpStatusCode.NotFound, "No such protected folder in that pool.");
        }

        return WebResponse.Ok(Duplication(poolId));
    }

    private WebResponse RunDuplication(Guid poolId, WebRequest request) => Post(request, () =>
    {
        var result = _engine.DuplicateOnceAsync(poolId).GetAwaiter().GetResult();

        return new
        {
            copiesAdded = result.CopiesAdded,
            copiesRefreshed = result.CopiesRefreshed,
            copiesRemoved = result.CopiesRemoved,
            bytesWritten = result.BytesWritten,
            skipped = result.Skipped,
            problems = result.Problems,
            protectedFolders = Duplication(poolId),
        };
    });

    private object Duplication(Guid poolId)
    {
        var runtime = _engine.FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");
        var plan = runtime.PlanDuplication();
        var statuses = plan.Rules.ToDictionary(rule => rule.RuleId);

        return new
        {
            enabled = _engine.Config.Duplication.Enabled,
            copiesToAdd = plan.CopiesToAdd,
            copiesToRefresh = plan.CopiesToRefresh,
            copiesToRemove = plan.CopiesToRemove,
            bytesToCopy = plan.BytesToCopy,
            folders = runtime.DuplicationRules.Select(rule =>
            {
                statuses.TryGetValue(rule.Id, out var status);

                return (object)new
                {
                    id = rule.Id,
                    folder = rule.Folder,
                    enabled = rule.Enabled,
                    everyDrive = rule.Mode is DuplicationMode.AllDrives,
                    copies = rule.Copies,
                    verifyByContent = rule.VerifyByContent,
                    keepSurplus = rule.Surplus is SurplusCopies.Keep,
                    requestedCopies = status?.RequestedCopies ?? 0,
                    possibleCopies = status?.WantedCopies ?? 0,
                    files = status?.Files ?? 0,
                    protectedFiles = status?.ProtectedFiles ?? 0,
                    bytes = status?.Bytes ?? 0,
                    pendingBytes = status?.PendingBytes ?? 0,
                    satisfied = status?.IsSatisfied ?? false,
                    needsMoreDrives = status?.NeedsMoreDrives ?? false,
                    problems = status is null
                        ? Array.Empty<string>()
                        : status.Shortfalls
                            .Take(20)
                            .Select(s => $"{s.PoolPath}: {s.Copies} of {s.WantedCopies} copies — {s.Reason}")
                            .ToArray(),
                };
            }).ToArray(),
        };
    }

    private WebResponse DriveAction(Guid poolId, WebRequest request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use POST to add drives.");
        }

        var body = Parse(request.Body);
        var volumeIds = body.TryGetProperty("volumeIds", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList()
            : [];

        if (volumeIds.Count == 0)
        {
            return WebResponse.Error(HttpStatusCode.BadRequest, "No drives were given to add.");
        }

        var parsed = new List<VolumeId>(volumeIds.Count);
        foreach (var raw in volumeIds)
        {
            if (!VolumeId.TryParse(raw, out var volumeId))
            {
                return WebResponse.Error(HttpStatusCode.BadRequest, $"'{raw}' is not a drive identifier.");
            }

            parsed.Add(volumeId);
        }

        var adopt = body.TryGetProperty("adoptExistingContent", out var adoptValue)
            && adoptValue.ValueKind == JsonValueKind.True;

        _engine.AddDrives(poolId, parsed, adopt);
        return WebResponse.Ok(Pool(poolId));
    }

    private WebResponse RemoveDrive(Guid poolId, string rawVolumeId, WebRequest request)
    {
        if (!string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use DELETE to remove a drive.");
        }

        if (!VolumeId.TryParse(Uri.UnescapeDataString(rawVolumeId), out var volumeId))
        {
            return WebResponse.Error(HttpStatusCode.BadRequest, $"'{rawVolumeId}' is not a drive identifier.");
        }

        // Moving a drive's files off is the slow, heavy option, so it is never the default: it has
        // to be asked for explicitly, exactly as in the window.
        var moveFilesOff = QueryFlag(request.Query, "move");

        if (moveFilesOff && QueryFlag(request.Query, "plan"))
        {
            var plan = _engine.PlanDriveRemoval(poolId, volumeId);
            return WebResponse.Ok(new
            {
                fileCount = plan.Items.Count + plan.WithoutRoom.Count,
                totalBytes = plan.TotalBytes,
                destinationFreeBytes = plan.DestinationFreeBytes,
                fits = plan.Fits,
                withoutRoom = plan.WithoutRoom.Count,
            });
        }

        var result = _engine.RemoveDrive(
            poolId,
            volumeId,
            moveFilesOff ? DriveRemoval.Evacuate : DriveRemoval.Detach);

        return WebResponse.Ok(new
        {
            removed = result.Removed,
            movedFilesOff = result.Mode is DriveRemoval.Evacuate,
            moved = result.Evacuation?.MovedCount ?? 0,
            movedBytes = result.Evacuation?.MovedBytes ?? 0,
            skippedInUse = result.Evacuation?.SkippedInUse.Count ?? 0,
            failed = result.Evacuation?.Failed.Count ?? 0,
            message = result.Message,
            pools = Pools(),
        });
    }

    private WebResponse UpdateAction(string action, bool reading, CancellationToken cancellationToken)
    {
        if (updates is null)
        {
            return WebResponse.Error(
                HttpStatusCode.ServiceUnavailable,
                "This MergePool is not running from a managed install, so it cannot update itself.");
        }

        switch (action)
        {
            case "status" when reading:
                return WebResponse.Ok(UpdateStatus());

            case "check" when !reading:
                _ = updates.CheckAsync(_engine.Config.Updates, cancellationToken);
                return WebResponse.Ok(UpdateStatus());

            case "apply" when !reading:
                _ = updates.ApplyAsync(cancellationToken);
                return WebResponse.Ok(UpdateStatus());

            default:
                return WebResponse.Error(HttpStatusCode.NotFound, "No such endpoint.");
        }
    }

    private object UpdateStatus()
    {
        var state = updates!.State;

        return new
        {
            installed = state.InstalledVersion,
            available = state.Available?.Version,
            updateAvailable = state.UpdateAvailable,
            stage = state.Stage.ToString(),
            progress = state.Progress,
            lastError = state.LastError,
        };
    }

    private WebResponse Post(WebRequest request, Func<object> action) =>
        string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            ? WebResponse.Ok(action())
            : WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use POST for this endpoint.");

    private object Status()
    {
        var pools = _engine.Pools;

        return new
        {
            version = _engine.Version,
            uptimeSeconds = _engine.UptimeSeconds,
            draining = _engine.IsDraining,
            poolCount = pools.Count,
            mountedCount = pools.Count(pool => _engine.IsMounted(pool.PoolId)),
            problems = _engine.CheckHealth(),
            readOnly = false,
        };
    }

    private IReadOnlyList<object> Drives() =>
        _engine.GetVolumes()
            .Select(volume => (object)new
            {
                volumeId = volume.Id.ToVolumePath(),
                letter = volume.DriveLetter?.ToString(CultureInfo.InvariantCulture),
                label = volume.Label,
                fileSystem = volume.FileSystem,
                kind = volume.Kind.ToString(),
                totalBytes = volume.TotalBytes,
                freeBytes = volume.FreeBytes,
                poolable = volume.IsPoolable,
                poolId = _engine.FindPoolForVolume(volume.Id),
            })
            .ToList();

    private IReadOnlyList<object> Pools() =>
        _engine.Pools.Select(runtime => Describe(runtime)).ToList();

    private object Pool(Guid poolId)
    {
        var runtime = _engine.FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");
        return Describe(runtime);
    }

    private object Describe(PoolRuntime runtime)
    {
        var snapshot = runtime.FreshSnapshot;

        return new
        {
            poolId = runtime.PoolId,
            name = runtime.Definition.Name,
            mountPoint = _engine.GetMountPoint(runtime.PoolId) ?? runtime.Definition.MountPoint,
            mounted = _engine.IsMounted(runtime.PoolId),
            health = snapshot.Health.ToString(),
            totalBytes = snapshot.TotalBytes,
            freeBytes = snapshot.FreeBytes,
            poolUsedBytes = snapshot.PoolUsedBytes,
            poolCapacityBytes = snapshot.PoolCapacityBytes,
            foreignBytes = snapshot.ForeignBytes,
            fileCount = snapshot.PoolFileCount,
            measured = snapshot.IsUsageMeasured,
            drives = snapshot.Parts.Select(part =>
            {
                var metrics = _engine.Metrics.GetSnapshot(part.PartId);

                return new
                {
                    partId = part.PartId,
                    volumeId = part.Volume.ToVolumePath(),
                    letter = part.DriveLetter?.ToString(CultureInfo.InvariantCulture),
                    label = part.Label,
                    state = part.State.ToString(),
                    totalBytes = part.TotalBytes,
                    freeBytes = part.FreeBytes,
                    poolBytes = part.PoolBytes,
                    throttled = metrics.IsThrottled,
                    throttleReason = metrics.ThrottleReason.ToString(),
                    throughputBytesPerSecond = metrics.ThroughputBytesPerSecond,
                    baselineBytesPerSecond = metrics.BaselineBytesPerSecond,
                };
            }).ToList(),
        };
    }

    private static Guid ParseGuid(string value) =>
        Guid.TryParse(value, out var id) ? id : throw new ArgumentException($"'{value}' is not a pool identifier.");

    private static JsonElement Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ArgumentException("The request body is not valid JSON.");
        }
    }

    private static bool QueryFlag(string query, string name)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (!string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return parts.Length == 1 || parts[1] is "1" or "true" or "True";
        }

        return false;
    }
}
