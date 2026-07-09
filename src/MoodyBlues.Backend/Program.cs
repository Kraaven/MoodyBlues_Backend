using MoodyBlues.Backend.Config;
using MoodyBlues.Backend.Logging;
using MoodyBlues.Backend.Server;

var config = ServerConfig.FromEnvironment();

using var runtimeLogs = new RuntimeLogs(config.LogDir, config.BinaryLogFilename, config.EventLogFilename);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    ConsoleLog.Info("Shutdown requested (Ctrl+C) ...");
    cts.Cancel();
};

var server = new MoodyBluesServer(config, runtimeLogs);

try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Graceful shutdown via Ctrl+C.
}

ConsoleLog.Info("MoodyBlues backend stopped.");
