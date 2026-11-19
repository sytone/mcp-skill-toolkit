using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using McpCli;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
StandardHandles.PreventInheritance();
using var cancellation = new CancellationTokenSource();
var signalExitCode = 130;
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
using var terminate = !OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    signalExitCode = 143;
    cancellation.Cancel();
}) : null;
var settings = new RuntimeSettings();
if (args.Length > 0 && args[0] == "--daemon-pipe")
{
    try { return await RunPipeWorkerAsync(); }
    catch (Exception error) { Console.Error.WriteLine($"Daemon failed: {error.Message}"); return 1; }
}
if (args.Length > 0 && args[0] == "--daemon")
{
    try
    {
        if (args.Length != 3) throw new InvalidDataException("Invalid daemon invocation");
        var value = McpJson.Parse(await Console.In.ReadLineAsync(cancellation.Token) ?? "") as JsonObject ??
            throw new InvalidDataException("Invalid daemon configuration");
        Configuration.ValidateServer(args[2], value);
        return await Daemon.RunAsync(args[1], args[2], new(value), settings, cancellation.Token);
    }
    catch (Exception error) { Console.Error.WriteLine($"Daemon failed: {error.Message}"); return 1; }
}
var result = await Cli.RunAsync(args, Console.In, Console.Out, Console.Error, Console.IsInputRedirected,
    !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
    settings, cancellation.Token);
return cancellation.IsCancellationRequested ? signalExitCode : result;

async Task<int> RunPipeWorkerAsync()
{
    if (args.Length != 3 || !args[1].StartsWith("mcp-cli-startup-", StringComparison.Ordinal) ||
        !Guid.TryParseExact(args[1]["mcp-cli-startup-".Length..], "N", out _))
        throw new InvalidDataException("Invalid daemon startup pipe");
    await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
    startup.CancelAfter(5000);
    await pipe.ConnectAsync(startup.Token);
    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
    var ready = false;
    void WriteStartupLine(string message)
    {
        lock (pipe) pipe.Write(Encoding.UTF8.GetBytes(message + "\n"));
    }
    settings.Diagnostics = message =>
    {
        WriteStartupLine(McpJson.Serialize(new JsonObject { ["diagnostic"] = message }));
    };
    try
    {
        var value = McpJson.Parse(await reader.ReadLineAsync(startup.Token) ?? "") as JsonObject ??
            throw new InvalidDataException("Invalid daemon configuration");
        Configuration.ValidateServer(args[2], value);
        var config = new ServerConfig(value);
        return await Daemon.RunAsync(Daemon.PipeName(args[2], config, settings), args[2], config,
            settings, cancellation.Token, () =>
            {
                WriteStartupLine("DAEMON_READY");
                ready = true;
                pipe.Dispose();
            });
    }
    catch (Exception error)
    {
        if (!ready) settings.Diagnostics($"Daemon failed: {error.Message}");
        else Console.Error.WriteLine($"Daemon failed: {error.Message}");
        return 1;
    }
}

internal static class StandardHandles
{
    public static void PreventInheritance()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Process.Start redirects its three streams but otherwise inherits inheritable Win32 handles.
        // A cached worker must not keep the invoking shell's redirected output open after CLI exit.
        foreach (var standardHandle in new[] { -10, -11, -12 })
        {
            var handle = GetStdHandle(standardHandle);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1) && !SetHandleInformation(handle, 1, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
