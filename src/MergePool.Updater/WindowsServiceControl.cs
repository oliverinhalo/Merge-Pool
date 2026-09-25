using System.Runtime.Versioning;
using System.ServiceProcess;
using MergePool.Update;

namespace MergePool.Updater;

/// <summary>Starts and stops the MergePool Windows service.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControl(string serviceName = "MergePool") : IServiceControl
{
    public ServiceState GetState()
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                _ => ServiceState.Transitioning,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
    }

    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var controller = new ServiceController(serviceName);
                if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                {
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    return;
                }

                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            },
            cancellationToken);

    public Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var controller = new ServiceController(serviceName);
                if (controller.Status is ServiceControllerStatus.Running)
                {
                    return;
                }

                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            },
            cancellationToken);
}
