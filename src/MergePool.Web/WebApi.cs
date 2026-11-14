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
            ("status", 2) when reading => WebResponse.Ok(Status(request)),
            ("health", 2) when reading => WebResponse.Ok(Health()),
            ("drives", 2) when reading => WebResponse.Ok(new { drives = Drives(), letters = FreeLetters() }),
            ("settings", 2) when reading => WebResponse.Ok(Settings()),
            ("settings", 2) => SetPlacement(request),
            ("settings", 3) when segments[2] == "duplication" && !reading => SetDuplicationOptions(request),
            ("settings", 3) when segments[2] == "updates" && !reading => SetUpdateOptions(request),
            ("pools", 2) when reading => WebResponse.Ok(new { pools = Pools() }),
            ("pools", 2) => CreatePool(request),
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

                // The recovery action: takes the mount down and brings it straight back. Pool data is
                // never involved — every file stays where it is — but open handles on the pool do
                // break, which is why it is its own deliberate action rather than part of mount.
                "remount" => Post(request, () =>
                {
                    _engine.Remount(poolId);
                    return Pool(poolId);
                }),

                "drives" => DriveAction(poolId, request),
                "protected" => ProtectedFolders(poolId, request),
                "share" => ShareAction(poolId, request),
                _ => WebResponse.Error(HttpStatusCode.NotFound, "No such endpoint."),
            };
        }

        // /api/pools/{id}
        if (segments.Length == 3 && string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return RemovePool(poolId, request);
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

    // ---- Making and unmaking pools ----

    /// <summary>
    /// Creates a pool from the drives given. The same action as the window's Create pool, including
    /// the opt-in to move each drive's existing files into it.
    /// </summary>
    private WebResponse CreatePool(WebRequest request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use POST to create a pool.");
        }

        var body = Parse(request.Body);

        var name = Text(body, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return WebResponse.Error(HttpStatusCode.BadRequest, "A pool needs a name.");
        }

        var mountPoint = Text(body, "mountPoint");
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return WebResponse.Error(HttpStatusCode.BadRequest, "A pool needs a drive letter to appear at.");
        }

        // A bare letter is how both the window and this page spell it.
        if (mountPoint.Length == 1 && char.IsLetter(mountPoint[0]))
        {
            mountPoint += ":";
        }

        if (!body.TryGetProperty("volumeIds", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return WebResponse.Error(HttpStatusCode.BadRequest, "A pool needs at least one drive.");
        }

        var volumes = new List<VolumeId>();
        foreach (var item in list.EnumerateArray())
        {
            if (!VolumeId.TryParse(item.GetString() ?? string.Empty, out var volumeId))
            {
                return WebResponse.Error(HttpStatusCode.BadRequest, $"'{item}' is not a drive identifier.");
            }

            volumes.Add(volumeId);
        }

        if (volumes.Count == 0)
        {
            return WebResponse.Error(HttpStatusCode.BadRequest, "A pool needs at least one drive.");
        }

        var adopt = Flag(body, "adoptExistingContent");
        var runtime = _engine.CreatePool(name, mountPoint, volumes, adopt);

        return WebResponse.Ok(new { created = Describe(runtime), pools = Pools() });
    }

    /// <summary>
    /// Forgets a pool. Every file stays on the drive it was on, in that drive's pool part folder —
    /// deleting the parts is not offered here, because an irreversible delete is not something to
    /// put behind a browser button.
    /// </summary>
    private WebResponse RemovePool(Guid poolId, WebRequest request)
    {
        _ = request;
        _engine.RemovePool(poolId, deletePoolParts: false);

        return WebResponse.Ok(new
        {
            removed = true,
            message = "The pool is gone. Every file is still on its drive, in that drive's pool part folder.",
            pools = Pools(),
        });
    }

    // ---- Settings ----

    /// <summary>
    /// Every tunable the engine has, in the same names the window and the pipe use, so one
    /// vocabulary covers all three.
    /// </summary>
    private object Settings()
    {
        var placement = _engine.Config.Placement;
        var duplication = _engine.Config.Duplication;
        var updates = _engine.Config.Updates;
        var web = _engine.Config.Web;

        return new
        {
            placement = new
            {
                freeSpaceWeight = placement.FreeSpaceWeight,
                speedWeight = placement.SpeedWeight,
                minFreeBytes = placement.MinFreeBytes,
                writeHeadroomBytes = placement.WriteHeadroomBytes,
                throttleEnterRatio = placement.ThrottleEnterRatio,
                throttleExitRatio = placement.ThrottleExitRatio,
                throttleCooldownSeconds = placement.ThrottleCooldownSeconds,
                latencyThrottleRatio = placement.LatencyThrottleRatio,
                throttleEnterSamples = placement.ThrottleEnterSamples,
                throttleExitSamples = placement.ThrottleExitSamples,
                minSamplesForThrottleDetection = placement.MinSamplesForThrottleDetection,
                ewmaAlpha = placement.EwmaAlpha,
                baselineAlpha = placement.BaselineAlpha,
                baselineRiseAlpha = placement.BaselineRiseAlpha,
                referenceThroughputBytesPerSecond = placement.ReferenceThroughputBytesPerSecond,
                minSpeedFactor = placement.MinSpeedFactor,
                maxSpeedFactor = placement.MaxSpeedFactor,
                minThroughputSampleBytes = placement.MinThroughputSampleBytes,
                idleProbeIntervalSeconds = placement.IdleProbeIntervalSeconds,
                idleProbeBytes = placement.IdleProbeBytes,
                rebalanceEnabled = placement.Rebalance.Enabled,
                rebalanceMaxBytesPerSecond = placement.Rebalance.MaxBytesPerSecond,
                rebalanceTriggerImbalance = placement.Rebalance.TriggerImbalance,
                rebalanceTargetImbalance = placement.Rebalance.TargetImbalance,
                rebalanceMaxFileBytes = placement.Rebalance.MaxFileBytes,
                rebalancePauseWhileBusy = placement.Rebalance.PauseWhileBusy,
            },
            duplication = new
            {
                enabled = duplication.Enabled,
                maxBytesPerSecond = duplication.MaxBytesPerSecond,
                scanIntervalMinutes = duplication.ScanIntervalMinutes,
                copyHeadroomBytes = duplication.CopyHeadroomBytes,
                pauseWhileBusy = duplication.PauseWhileBusy,
            },
            updates = new
            {
                automaticChecks = updates.AutomaticChecks,
                automaticInstall = updates.AutomaticInstall,
                checkIntervalHours = updates.CheckIntervalHours,
                includePrereleases = updates.IncludePrereleases,
            },

            // Read-only on purpose. Turning changes off, or moving the port, from the browser would
            // cut the browser off from the setting that undoes it — a one-way door with no handle on
            // this side. These belong in the window.
            webInterface = new
            {
                port = web.Port,
                scope = web.AccessScope.ToString(),
                allowChanges = web.AllowChanges,
                manageFirewallRule = web.ManageFirewallRule,
                readOnlyHere = true,
                why = "The web interface's own settings are changed in MergePool's window. Turning "
                    + "changes off or moving the port from here would lock this page out of undoing it.",
            },
        };
    }

    private WebResponse SetPlacement(WebRequest request)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use POST to change settings.");
        }

        var body = Parse(request.Body);

        _engine.UpdatePlacement(placement =>
        {
            placement.FreeSpaceWeight = Number(body, "freeSpaceWeight", placement.FreeSpaceWeight);
            placement.SpeedWeight = Number(body, "speedWeight", placement.SpeedWeight);
            placement.MinFreeBytes = Bytes(body, "minFreeBytes", placement.MinFreeBytes);
            placement.WriteHeadroomBytes = Bytes(body, "writeHeadroomBytes", placement.WriteHeadroomBytes);
            placement.ThrottleEnterRatio = Number(body, "throttleEnterRatio", placement.ThrottleEnterRatio);
            placement.ThrottleExitRatio = Number(body, "throttleExitRatio", placement.ThrottleExitRatio);
            placement.ThrottleCooldownSeconds = Number(body, "throttleCooldownSeconds", placement.ThrottleCooldownSeconds);
            placement.LatencyThrottleRatio = Number(body, "latencyThrottleRatio", placement.LatencyThrottleRatio);
            placement.ThrottleEnterSamples = Count(body, "throttleEnterSamples", placement.ThrottleEnterSamples);
            placement.ThrottleExitSamples = Count(body, "throttleExitSamples", placement.ThrottleExitSamples);
            placement.MinSamplesForThrottleDetection =
                Count(body, "minSamplesForThrottleDetection", placement.MinSamplesForThrottleDetection);
            placement.EwmaAlpha = Number(body, "ewmaAlpha", placement.EwmaAlpha);
            placement.BaselineAlpha = Number(body, "baselineAlpha", placement.BaselineAlpha);
            placement.BaselineRiseAlpha = Number(body, "baselineRiseAlpha", placement.BaselineRiseAlpha);
            placement.ReferenceThroughputBytesPerSecond =
                Number(body, "referenceThroughputBytesPerSecond", placement.ReferenceThroughputBytesPerSecond);
            placement.MinSpeedFactor = Number(body, "minSpeedFactor", placement.MinSpeedFactor);
            placement.MaxSpeedFactor = Number(body, "maxSpeedFactor", placement.MaxSpeedFactor);
            placement.MinThroughputSampleBytes =
                Bytes(body, "minThroughputSampleBytes", placement.MinThroughputSampleBytes);
            placement.IdleProbeIntervalSeconds =
                Number(body, "idleProbeIntervalSeconds", placement.IdleProbeIntervalSeconds);
            placement.IdleProbeBytes = (int)Bytes(body, "idleProbeBytes", placement.IdleProbeBytes);
            placement.Rebalance.Enabled = Switch(body, "rebalanceEnabled", placement.Rebalance.Enabled);
            placement.Rebalance.MaxBytesPerSecond =
                Bytes(body, "rebalanceMaxBytesPerSecond", placement.Rebalance.MaxBytesPerSecond);
            placement.Rebalance.TriggerImbalance =
                Number(body, "rebalanceTriggerImbalance", placement.Rebalance.TriggerImbalance);
            placement.Rebalance.TargetImbalance =
                Number(body, "rebalanceTargetImbalance", placement.Rebalance.TargetImbalance);
            placement.Rebalance.MaxFileBytes = Bytes(body, "rebalanceMaxFileBytes", placement.Rebalance.MaxFileBytes);
            placement.Rebalance.PauseWhileBusy =
                Switch(body, "rebalancePauseWhileBusy", placement.Rebalance.PauseWhileBusy);

            // The same clamp the pipe applies, for the same reason: a value typed here that would
            // stop the engine placing files is brought into range rather than stored.
            placement.Clamped();
        });

        return WebResponse.Ok(Settings());
    }

    private WebResponse SetDuplicationOptions(WebRequest request)
    {
        var body = Parse(request.Body);

        _engine.UpdateDuplicationSettings(settings =>
        {
            settings.Enabled = Switch(body, "enabled", settings.Enabled);
            settings.MaxBytesPerSecond = Bytes(body, "maxBytesPerSecond", settings.MaxBytesPerSecond);
            settings.ScanIntervalMinutes = Number(body, "scanIntervalMinutes", settings.ScanIntervalMinutes);
            settings.CopyHeadroomBytes = Bytes(body, "copyHeadroomBytes", settings.CopyHeadroomBytes);
            settings.PauseWhileBusy = Switch(body, "pauseWhileBusy", settings.PauseWhileBusy);
            settings.Clamped();
        });

        return WebResponse.Ok(Settings());
    }

    private WebResponse SetUpdateOptions(WebRequest request)
    {
        var body = Parse(request.Body);

        _engine.UpdateUpdateOptions(options =>
        {
            options.AutomaticChecks = Switch(body, "automaticChecks", options.AutomaticChecks);
            options.AutomaticInstall = Switch(body, "automaticInstall", options.AutomaticInstall);
            options.CheckIntervalHours = Number(body, "checkIntervalHours", options.CheckIntervalHours);
            options.IncludePrereleases = Switch(body, "includePrereleases", options.IncludePrereleases);
            options.Clamped();
        });

        return WebResponse.Ok(Settings());
    }

    // ---- Sharing a pool on the network ----

    private WebResponse ShareAction(Guid poolId, WebRequest request)
    {
        var runtime = _engine.FindPool(poolId) ?? throw new KeyNotFoundException($"No pool {poolId:D}.");

        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Ok(Share(runtime));
        }

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use GET to read or POST to change sharing.");
        }

        var body = Parse(request.Body);

        _engine.UpdatePoolShare(poolId, share =>
        {
            share.Enabled = Switch(body, "enabled", share.Enabled);
            share.Name = Text(body, "name") is { Length: > 0 } name ? name : share.Name;
            share.ReadOnly = Switch(body, "readOnly", share.ReadOnly);

            if (body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("allowedAccounts", out var accounts)
                && accounts.ValueKind == JsonValueKind.Array)
            {
                share.AllowedAccounts = [.. accounts
                    .EnumerateArray()
                    .Select(item => (item.GetString() ?? string.Empty).Trim())
                    .Where(account => account.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
            }
        });

        return WebResponse.Ok(Share(runtime));
    }

    private object Share(PoolRuntime runtime)
    {
        var options = runtime.Definition.Share;

        return new
        {
            poolId = runtime.PoolId,
            enabled = options.Enabled,
            name = options.Name,
            effectiveName = options.ShareNameFor(runtime.Definition.Name),
            readOnly = options.ReadOnly,
            allowedAccounts = options.AllowedAccounts.ToArray(),

            // The service creates and removes the share itself, on its own schedule, so what this
            // reports is the setting; whether the share exists yet is the window's to show, which
            // reads it from the service that does the creating.
            problem = options.Problem(runtime.Definition.Name),
        };
    }

    // ---- Reading a JSON body without ceremony ----

    private static string Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Flag(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>A boolean that keeps its current value when the request does not mention it.</summary>
    private static bool Switch(JsonElement body, string name, bool current) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : current;

    private static double Number(JsonElement body, string name, double current) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var parsed)
            ? parsed
            : current;

    private static long Bytes(JsonElement body, string name, long current) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : current;

    private static int Count(JsonElement body, string name, int current) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : current;

    private WebResponse Post(WebRequest request, Func<object> action) =>
        string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            ? WebResponse.Ok(action())
            : WebResponse.Error(HttpStatusCode.MethodNotAllowed, "Use POST for this endpoint.");

    private object Status(WebRequest request)
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

            // What the page may offer. It used to be hard-coded false, so a look-but-not-touch
            // interface still drew every button and only said no once one was pressed.
            readOnly = !request.AllowChanges,
        };
    }

    /// <summary>
    /// Whether each pool's I/O is actually answering. The health check above reads configuration
    /// and resolves drives; neither notices a mount whose callbacks have stopped completing.
    /// </summary>
    private object Health() => new
    {
        pools = _engine.IoHealth.Select(pool => (object)new
        {
            poolId = pool.PoolId,
            name = pool.Name,
            state = pool.Health.State.ToString(),
            summary = pool.Health.Describe(),
            stalledSeconds = pool.Health.StalledFor?.TotalSeconds,
            stalls = pool.Health.Stalls,
            failures = pool.Health.Failures,
            recentFaults = pool.Health.RecentFaults.TakeLast(5).Select(fault => fault.ToString()).ToArray(),
        }).ToArray(),
    };

    /// <summary>
    /// Letters no drive is using, for the create-a-pool form. Mirrors the window: D to Z, minus
    /// whatever is taken.
    /// </summary>
    private IReadOnlyList<string> FreeLetters()
    {
        var taken = _engine.GetVolumes()
            .Where(volume => volume.DriveLetter is not null)
            .Select(volume => volume.DriveLetter!.Value)
            .ToHashSet();

        return [.. Enumerable
            .Range('D', 'Z' - 'D' + 1)
            .Select(c => (char)c)
            .Where(c => !taken.Contains(c))
            .Select(c => c.ToString(CultureInfo.InvariantCulture))];
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
