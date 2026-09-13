using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

// No testing packages or application services required. Tests spawn only temporary, owned fixtures.
if (args.Length > 0 && args[0] == "delay")
{
    await Task.Delay(Timeout.Infinite);
    return;
}
if (args.Length > 0 && args[0] == "server")
{
    var port = int.Parse(args[1]);
    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    Console.WriteLine($"Now listening on: http://localhost:{port}");
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buffer = new byte[4096];
        var bytesRead = await stream.ReadAsync(buffer);
        if (bytesRead == 0) continue;
        await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
    }
}

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

Check(ServiceRunner.DiscoverLocalUrl("Read the documentation at https://angular.dev") is null, "documentation URL rejected");
Check(ServiceRunner.DiscoverLocalUrl("Now listening on: http://0.0.0.0:5432") == "http://localhost:5432", "wildcard startup URL normalized");
Check(ServiceRunner.DiscoverLocalUrl("\u001b[32m  ➜  Local:   http://localhost:4200/\u001b[0m") == "http://localhost:4200", "Angular URL parsed without ANSI");
Check(ServiceRunner.DiscoverLocalUrl("Local: https://example.com") is null, "remote URL rejected");
Check(ServiceRunner.StripTerminalCodes("\u001b]0;title\ahello\u001b[0m") == "hello", "OSC title and color removed");

var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
var current = Process.GetCurrentProcess();
var fake = new InspectedProcess(1, 0, "node.exe", @"C:\node\node.exe", "node \"" + directory + "\\node_modules\\@angular\\cli\\bin\\ng.js\" serve", 1);
Check(ProcessInspector.BelongsToDirectory(fake, directory), "project-local Angular command recognized");
Check(!ProcessInspector.BelongsToDirectory(fake with { CommandLine = fake.CommandLine.Replace(directory, directory + "-other") }, directory), "sibling path prefix cannot match");
Check(!ProcessInspector.BelongsToDirectory(fake with { Name = "cmd.exe" }, directory), "external shells cannot be adopted");
Check(!ProcessInspector.BelongsToDirectory(fake with { Name = "Code.exe" }, directory), "editor arguments cannot be adopted");

using var portReservation = new TcpListener(IPAddress.Loopback, 0);
portReservation.Start();
var freePort = ((IPEndPoint)portReservation.LocalEndpoint).Port;
var inventory = await ProcessInspector.ReadAsync(true);
Check(inventory.InspectionError is null, "native process and TCP inspection succeeded");
Check(inventory.ListeningPorts.Any(p => p.Port == freePort && p.ProcessId == current.Id), "TCP listener PID identified");
Check(inventory.Processes.Any(p => p.Id == current.Id && p.StartedUtcTicks == current.StartTime.ToUniversalTime().Ticks), "process creation identity matches System.Diagnostics");

var profile = new ServiceProfile
{
    Name = "Fixture", Url = $"http://localhost:{freePort}", UiPath = "/swagger",
    StartCommand = $"\"{Environment.ProcessPath}\" server {freePort}",
    CleanCommand = "echo clean complete", SetupCommand = "echo setup complete"
};
using var runner = new ServiceRunner(profile, directory);
runner.LogReceived += log => Console.WriteLine("LOG: " + log.Message);
await runner.StartAsync();
Check(runner.Snapshot.State == ServiceState.Conflict && !runner.HasManagedProcess, "foreign listener blocks duplicate start");
await runner.ForceStopAsync();
Check(ProcessInspector.IsSameProcess(new(current.Id, current.StartTime.ToUniversalTime().Ticks)), "force stop cannot kill a foreign listener or launcher ancestor");
portReservation.Stop();

await runner.SetupAsync();
Check(runner.Snapshot.State == ServiceState.Stopped, "setup command completes and returns to stopped");
await runner.CleanAsync();
Check(runner.Snapshot.State == ServiceState.Stopped, "clean command completes and returns to stopped");
await runner.StartAsync();
await WaitForState(runner, ServiceState.Running);
Check(runner.HasManagedProcess && runner.Snapshot.IsManaged, "started process tree is tracked as managed");
Check(runner.Snapshot.ActiveUrl == $"http://localhost:{freePort}/swagger", "UI path retained on discovered startup URL");
Check(runner.Snapshot.Detail.Contains("HTTP 404"), "HTTP 404 still proves server is responding");
var firstPids = runner.Snapshot.ProcessIds.ToArray();
await runner.StartAsync();
Check(runner.Snapshot.ProcessIds.SequenceEqual(firstPids), "duplicate start does not launch extra process tree");
await runner.CleanAsync();
Check(runner.Snapshot.State == ServiceState.Running, "clean is blocked while service is active");
await runner.RestartAsync();
await WaitForState(runner, ServiceState.Running);
Check(runner.Snapshot.ProcessIds.All(id => !firstPids.Contains(id)), "restart replaces and awaits previous tree");
await runner.StopManagedAsync();
Check(runner.Snapshot.State == ServiceState.Stopped && !runner.HasManagedProcess, "normal close stops only owned trees");

// An independently started runtime has project-path evidence, so refresh can adopt it for status.
using var secondReservation = new TcpListener(IPAddress.Loopback, 0);
secondReservation.Start();
var externalPort = ((IPEndPoint)secondReservation.LocalEndpoint).Port;
secondReservation.Stop();
using var external = Process.Start(new ProcessStartInfo
{
    FileName = Environment.ProcessPath!, Arguments = $"server {externalPort}",
    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true
})!;
try
{
    await WaitForState(runner, ServiceState.Running);
    Check(!runner.HasManagedProcess && runner.Snapshot.ProcessIds.Contains(external.Id), "external project executable detected without being claimed as managed");
    Check(runner.Snapshot.ActiveUrl == $"http://localhost:{externalPort}/swagger", "external listener replaces the previous managed run's URL");
    await runner.StopManagedAsync();
    Check(!external.HasExited, "normal close leaves external service alive");
    await runner.ForceStopAsync();
    await external.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(runner.Snapshot.State == ServiceState.Stopped, "explicit force stop terminates verified external service");
}
finally { if (!external.HasExited) external.Kill(true); }

profile.SetupCommand = $"\"{Environment.ProcessPath}\" delay";
var setup = runner.SetupAsync();
await WaitForBusy(runner);
var stopWatch = Stopwatch.StartNew();
await runner.ForceStopAsync();
await setup.WaitAsync(TimeSpan.FromSeconds(10));
Check(stopWatch.Elapsed < TimeSpan.FromSeconds(10) && !runner.HasManagedProcess,
    "force stop cancels long-running maintenance without waiting for the command");
setup = runner.SetupAsync();
await runner.StopManagedAsync();
await setup.WaitAsync(TimeSpan.FromSeconds(10));
Check(!runner.HasManagedProcess, "immediate close request cannot lose cancellation before maintenance CTS exists");
profile.CleanCommand = "exit /b 17";
await runner.CleanAsync();
Check(runner.Snapshot.State == ServiceState.Error && runner.Snapshot.Detail.Contains("17"), "nonzero command exit is surfaced as an error");
Console.WriteLine($"All {checks} process engine checks passed.");

static async Task WaitForState(ServiceRunner runner, ServiceState desired)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    do
    {
        await runner.RefreshAsync();
        if (runner.Snapshot.State == desired) return;
        await Task.Delay(200);
    } while (DateTime.UtcNow < deadline);
    throw new Exception($"Expected {desired}; got {runner.Snapshot.State}: {runner.Snapshot.Detail}");
}

static async Task WaitForBusy(ServiceRunner runner)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (runner.Snapshot.State != ServiceState.Busy)
    {
        if (DateTime.UtcNow > deadline) throw new Exception("Maintenance never became busy.");
        await Task.Delay(30);
    }
}
