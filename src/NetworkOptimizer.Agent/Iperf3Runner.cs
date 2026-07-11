using System.ComponentModel;
using System.Diagnostics;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Keeps an iperf3 server (default port 5201, JSON output) running alongside the speed test page so
/// site devices have a LAN throughput target. Each completed client-initiated test's <c>-J</c> JSON
/// is captured (brace-counted off stdout, mirroring the central server's managed iperf3 server) and
/// relayed to the central server via <c>relayResult</c>, so client-initiated iperf3 results land in
/// the site's database exactly like the default site's do. Uses the host's iperf3 binary; if it
/// isn't installed this logs once and gives up rather than looping.
/// </summary>
public static class Iperf3Runner
{
    public static async Task RunAsync(Func<string, CancellationToken, Task>? relayResult, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                // Shared server args (-s -p {port} -J) so the emitted per-test JSON matches what
                // CaptureResultsAsync (and the central Iperf3ServerService) brace-counts.
                var psi = new ProcessStartInfo("iperf3", Iperf3ServerArgs.Build())
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                process = Process.Start(psi);
                if (process == null)
                {
                    Console.Error.WriteLine("iperf3 could not be started - LAN iperf3 serving disabled");
                    return;
                }

                Console.WriteLine("iperf3 server running (default port 5201)");
                _ = CaptureResultsAsync(process.StandardOutput, relayResult, ct);
                _ = DrainAsync(process.StandardError, ct);
                await process.WaitForExitAsync(ct);
                Console.Error.WriteLine($"iperf3 exited with code {process.ExitCode}, restarting in 10 seconds");
            }
            catch (OperationCanceledException)
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return;
            }
            catch (Win32Exception)
            {
                Console.Error.WriteLine("iperf3 binary not found on PATH - install iperf3 to serve LAN iperf3 tests");
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"iperf3 error: {ex.Message}");
            }
            finally
            {
                process?.Dispose();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Brace-counts <c>iperf3 -s -J</c> stdout to isolate each completed test's JSON object and
    /// relays it, mirroring the central <c>Iperf3ServerService</c>'s capture exactly.
    /// </summary>
    private static async Task CaptureResultsAsync(StreamReader reader, Func<string, CancellationToken, Task>? relayResult, CancellationToken ct)
    {
        var accumulator = new JsonObjectAccumulator();
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) != null)
            {
                accumulator.Feed(line, json =>
                {
                    if (relayResult != null)
                        _ = relayResult(json, ct);
                });
            }
        }
        catch
        {
            // Process ended or cancelled - nothing to capture.
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) != null)
            {
            }
        }
        catch
        {
            // Process ended or cancelled - nothing to drain.
        }
    }
}
