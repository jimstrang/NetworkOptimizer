namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Builds the iperf3 SERVER command-line arguments, shared by the central managed iperf3 server
/// (<c>Iperf3ServerService</c>) and the on-site agent (<c>Iperf3Runner</c>) so both invoke it
/// identically: server mode with JSON output (<c>-J</c>) so every completed client-initiated test
/// is emitted as a JSON object and can be captured.
/// </summary>
public static class Iperf3ServerArgs
{
    /// <summary>Default iperf3 server port.</summary>
    public const int DefaultPort = 5201;

    /// <summary>Server mode on <paramref name="port"/> with per-test JSON output.</summary>
    public static string Build(int port = DefaultPort) => $"-s -p {port} -J";
}
