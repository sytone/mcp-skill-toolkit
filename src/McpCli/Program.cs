using System.Runtime.InteropServices;
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
    catch (Exception error)
    {
        // The parent can recover by using a competing worker or a direct connection.
        // It owns the terminal error if fallback also fails. Server stderr is still streamed.
        settings.Debug($"Daemon failed: {error.Message}");
        return 1;
    }
}
var result = await Cli.RunAsync(args, Console.In, Console.Out, Console.Error, Console.IsInputRedirected,
    !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
    settings, cancellation.Token);
return cancellation.IsCancellationRequested ? signalExitCode : result;

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
