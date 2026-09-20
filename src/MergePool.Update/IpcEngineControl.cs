using MergePool.Ipc.Client;
using MergePool.Ipc.Protocol;

namespace MergePool.Update;

/// <summary>
/// Drives the engine over the named pipe during an upgrade. Deliberately tolerant: a service that
/// is already down is not an error when draining, and the health check retries while the new
/// service is still starting up.
/// </summary>
public sealed class IpcEngineControl(string pipeName = "MergePool.Engine") : IEngineControl
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = Connect(timeout);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await client.InvokeAsync<ServiceStatusResult>(Methods.UpgradeDrain, null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or IpcException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<EngineHealth?> ResumeAndCheckAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var client = Connect(TimeSpan.FromSeconds(5));
                var hello = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

                // The new service starts its pools itself; resume is idempotent and also clears the
                // draining flag if the previous instance set it.
                await client.InvokeAsync<ServiceStatusResult>(Methods.UpgradeResume, null, cancellationToken)
                    .ConfigureAwait(false);

                var health = await client.InvokeAsync<HealthCheckResult>(Methods.HealthCheck, null, cancellationToken)
                    .ConfigureAwait(false);

                return new EngineHealth(health.Healthy, health.Problems, hello.ServiceVersion);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or IpcException or UnauthorizedAccessException)
            {
                // The service is probably still coming up.
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private IpcClient Connect(TimeSpan timeout) => new(new IpcClientOptions
    {
        PipeName = pipeName,
        ClientName = "MergePool.Updater",
        ConnectTimeout = timeout,
        RequestTimeout = timeout,
    });
}
