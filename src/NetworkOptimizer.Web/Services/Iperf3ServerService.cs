using System.Diagnostics;
using System.Text.Json;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Background service that runs iperf3 in server mode and monitors for client-initiated tests.
/// Parses JSON output and records results via ClientSpeedTestService.
/// </summary>
public class Iperf3ServerService : BackgroundService
{
    private readonly ILogger<Iperf3ServerService> _logger;
    private readonly SpeedTestServiceRegistry _speedTestRegistry;
    private readonly IConfiguration _configuration;

    private Process? _iperf3Process;
    private const int Iperf3Port = Iperf3ServerArgs.DefaultPort;

    // Pause/resume support (used during WAN speed tests to free pipe handles)
    private volatile bool _isPaused;
    private TaskCompletionSource? _resumeTcs;

    public Iperf3ServerService(
        ILogger<Iperf3ServerService> logger,
        SpeedTestServiceRegistry speedTestRegistry,
        IConfiguration configuration)
    {
        _logger = logger;
        // Keep the registry, not the resolved client service: resolving
        // GetDefault() here would build the default speed-test bundle during
        // construction, and that bundle's UwnSpeedTestService depends back on
        // this service (via WanSpeedTestServiceBase) - a constructor cycle that
        // deadlocks host startup. The client service is fetched lazily at the
        // one use site, by which point this singleton is fully constructed and
        // the bundle resolves it without re-entering the constructor.
        _speedTestRegistry = speedTestRegistry;
        _configuration = configuration;
    }

    /// <summary>
    /// The default site's client speed test service, resolved on demand. The
    /// local iperf3 server lives on the default site's network; agent sites run
    /// their own iperf3 next to the agent (results relayed with their slug).
    /// </summary>
    private ClientSpeedTestService ClientSpeedTest => _speedTestRegistry.GetDefault().ClientSpeedTest;

    /// <summary>
    /// Whether the iperf3 server is currently running
    /// </summary>
    public bool IsRunning => _iperf3Process is { HasExited: false };

    /// <summary>
    /// Whether the iperf3 server was enabled but failed to start (e.g., port conflict)
    /// </summary>
    public bool StartupFailed { get; private set; }

    /// <summary>
    /// Message explaining why startup failed, if applicable
    /// </summary>
    public string? FailureMessage { get; private set; }

    /// <summary>
    /// Pause the iperf3 server (kills the process and prevents restart).
    /// Used during WAN speed tests to free pipe handles that interfere with GC compaction.
    /// </summary>
    public async Task PauseAsync()
    {
        if (_isPaused) return;
        _isPaused = true;
        _resumeTcs = new TaskCompletionSource();

        // Kill current iperf3 process
        if (_iperf3Process is { HasExited: false })
        {
            try { _iperf3Process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error killing iperf3 process during pause"); }
        }

        // Also kill orphans to ensure port is free when we resume
        await KillOrphanedIperf3ProcessesAsync();

        _logger.LogInformation("iperf3 server paused");
    }

    /// <summary>
    /// Resume the iperf3 server after a pause.
    /// Kills any orphaned iperf3 processes and waits for the port to be released.
    /// </summary>
    public async Task ResumeAsync()
    {
        if (!_isPaused) return;

        // Kill any orphaned iperf3 still holding the port, then wait for OS to release it
        await KillOrphanedIperf3ProcessesAsync();
        await Task.Delay(1000);

        _isPaused = false;
        _resumeTcs?.TrySetResult();
        _resumeTcs = null;
        _logger.LogInformation("iperf3 server resumed");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if iperf3 server mode is enabled
        var enabled = _configuration.GetValue("Iperf3Server:Enabled", false);
        if (!enabled)
        {
            _logger.LogInformation("iperf3 server mode is disabled. Enable via Iperf3Server:Enabled=true");
            return;
        }

        _logger.LogInformation("Starting iperf3 server on port {Port}", Iperf3Port);

        var consecutiveImmediateExits = 0;
        const int maxImmediateExitRetries = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait if paused (e.g., during WAN speed test to free pipe handles)
            if (_isPaused && _resumeTcs != null)
            {
                _logger.LogDebug("iperf3 server paused, waiting for resume signal");
                try
                {
                    await _resumeTcs.Task.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                consecutiveImmediateExits = 0;
                continue;
            }

            try
            {
                var ranSuccessfully = await RunIperf3ServerAsync(stoppingToken);

                if (ranSuccessfully)
                {
                    consecutiveImmediateExits = 0;
                }
                else
                {
                    consecutiveImmediateExits++;

                    // On first failure, try killing orphaned processes (port may be held by old instance)
                    if (consecutiveImmediateExits == 1)
                    {
                        await KillOrphanedIperf3ProcessesAsync();
                    }

                    if (consecutiveImmediateExits >= maxImmediateExitRetries)
                    {
                        _logger.LogError(
                            "iperf3 server failed to start {Count} consecutive times, giving up. Check if port {Port} is in use.",
                            consecutiveImmediateExits, Iperf3Port);
                        StartupFailed = true;
                        FailureMessage = $"Port {Iperf3Port} may already be in use by another iperf3 server. " +
                            "Stop any existing iperf3 service and restart the container.";
                        break;
                    }

                    // Exponential backoff: 1s, 2s, 4s, 8s, 16s
                    var delaySeconds = (int)Math.Pow(2, consecutiveImmediateExits - 1);
                    _logger.LogWarning(
                        "Waiting {Delay}s before retry (attempt {Attempt}/{Max})",
                        delaySeconds, consecutiveImmediateExits, maxImmediateExitRetries);
                    await Task.Delay(delaySeconds * 1000, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "iperf3 server crashed, restarting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("iperf3 server stopped");
    }

    /// <summary>
    /// Runs the iperf3 server process until it exits or cancellation is requested.
    /// </summary>
    /// <returns>True if the process ran for more than 2 seconds (successful), false if it exited immediately.</returns>
    private async Task<bool> RunIperf3ServerAsync(CancellationToken stoppingToken)
    {
        // Check cancellation before starting a new process
        stoppingToken.ThrowIfCancellationRequested();

        var iperf3Path = ProcessUtilities.GetIperf3Path();
        _logger.LogDebug("Using iperf3 at: {Path}", iperf3Path);

        var startInfo = new ProcessStartInfo
        {
            FileName = iperf3Path,
            Arguments = Iperf3ServerArgs.Build(Iperf3Port), // Server mode, JSON output (shared)
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _iperf3Process = new Process { StartInfo = startInfo };
        var startTime = DateTime.UtcNow;

        // Brace-count the -J stream into complete per-test JSON objects (shared with the agent).
        var accumulator = new JsonObjectAccumulator();
        _iperf3Process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null) return;
            accumulator.Feed(e.Data, json => _ = ProcessCompletedTestAsync(json));
        };

        _iperf3Process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogWarning("iperf3 server stderr: {Message}", e.Data);
            }
        };

        _iperf3Process.Start();
        _iperf3Process.BeginOutputReadLine();
        _iperf3Process.BeginErrorReadLine();

        _logger.LogInformation("iperf3 server started with PID {Pid}", _iperf3Process.Id);

        // Wait for process to exit or cancellation
        try
        {
            await _iperf3Process.WaitForExitAsync(stoppingToken);

            var runtime = DateTime.UtcNow - startTime;
            var exitCode = _iperf3Process.ExitCode;
            var ranSuccessfully = runtime.TotalSeconds >= 2;

            if (!ranSuccessfully)
            {
                _logger.LogWarning(
                    "iperf3 server exited immediately (exit code {ExitCode}, ran for {Runtime:F1}s) - port {Port} may already be in use",
                    exitCode, runtime.TotalSeconds, Iperf3Port);
            }
            else
            {
                _logger.LogInformation(
                    "iperf3 server exited (exit code {ExitCode}, ran for {Runtime:F1}s), restarting",
                    exitCode, runtime.TotalSeconds);
            }

            return ranSuccessfully;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stopping iperf3 server process");
            try
            {
                _iperf3Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error killing iperf3 process");
            }
            throw;
        }
        finally
        {
            _iperf3Process.Dispose();
            _iperf3Process = null;
        }
    }

    // Client-initiated iperf3 results (the local iperf3 -s captured a LAN client's test) are
    // parsed and stored by the shared recorder against the default site. Agent sites capture the
    // same way and relay to /api/public/speedtest/iperf3-results, which records against their site.
    private Task ProcessCompletedTestAsync(string json)
        => Iperf3ClientResultRecorder.RecordAsync(ClientSpeedTest, json, _logger);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping iperf3 server service");

        // First try to kill our tracked process
        if (_iperf3Process is { HasExited: false })
        {
            try
            {
                _iperf3Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error killing iperf3 process on stop");
            }
        }

        // Use pkill as a fallback to ensure cleanup on Unix systems
        // This handles cases where the process reference was lost or race conditions
        // Run twice with a delay to catch processes spawned during the shutdown race
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                try
                {
                    using var pkill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "pkill",
                        Arguments = "iperf3",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    pkill?.WaitForExit(2000);
                    if (pkill?.ExitCode == 0)
                    {
                        _logger.LogInformation("Killed iperf3 processes via pkill");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "pkill iperf3 failed");
                }
            }
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Kill any orphaned iperf3 server processes that may be left over from a previous run.
    /// This handles the case where the app was stopped but child processes weren't killed
    /// (common with launchd on macOS).
    /// </summary>
    private async Task KillOrphanedIperf3ProcessesAsync()
    {
        try
        {
            // Use pkill on Unix-like systems (macOS, Linux)
            if (!OperatingSystem.IsWindows())
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "pkill",
                    // Use -9 (SIGKILL) to ensure process dies, simple pattern matching
                    Arguments = "-9 iperf3",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        _logger.LogInformation("Killed orphaned iperf3 server process(es)");
                        // Brief delay to ensure port is released
                        await Task.Delay(500);
                    }
                    // Exit code 1 means no matching processes found, which is fine
                }
            }
            else
            {
                // On Windows, find and kill iperf3.exe processes in server mode
                // We check the command line for "-s" to avoid killing client instances
                foreach (var proc in Process.GetProcessesByName("iperf3"))
                {
                    try
                    {
                        proc.Kill();
                        _logger.LogInformation("Killed orphaned iperf3 server process (PID {Pid})", proc.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not kill iperf3 process {Pid}", proc.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking for orphaned iperf3 processes");
        }
    }

}
