using SPOMover.Engine;

namespace SPOMover.Host;

/// <summary>
/// STA-3: a run interrupted by a host crash or a reboot is continued when the host starts;
/// on shutdown the active run is stopped gracefully (STA-4).
/// </summary>
internal sealed class RunHostService(RunService runs, IConfiguration configuration, ILogger<RunHostService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue("continueInterrupted", true))
        {
            try
            {
                runs.ContinueInterrupted();
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                logger.LogError(ex, "Could not continue the interrupted run");
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await runs.DisposeAsync();
}
