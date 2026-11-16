using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpCli;

public sealed class StdioTransport(string name, ServerConfig config, RuntimeSettings settings) : RpcTransport
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeLock = new(1);
    private readonly StringBuilder stderr = new();
    private Process? process;
    private Task? readerTask;
    private Task? errorTask;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = CreateStartInfo(config.Command, config.Args, config.Cwd ?? settings.WorkingDirectory, config.Map("env"));
        try
        {
            process = Process.Start(start) ?? throw new IOException($"Could not start {config.Command}");
        }
        catch (Win32Exception error)
        {
            throw new IOException($"ENOENT: Could not start command \"{config.Command}\": {error.Message}", error);
        }
        errorTask = ReadErrorsAsync(process.StandardError);
        readerTask = ReadMessagesAsync(process.StandardOutput);
        return Task.CompletedTask;
    }

    public static ProcessStartInfo CreateStartInfo(string command, IEnumerable<string> args, string cwd,
        IEnumerable<KeyValuePair<string, string>>? environment = null)
    {
        var arguments = args.ToArray();
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        // Windows batch entry points require cmd.exe; native executables never go through a shell.
        var resolved = ResolveCommand(command, cwd, start.Environment);
        var extension = Path.GetExtension(resolved);
        if (OperatingSystem.IsWindows() && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            if (arguments.Prepend(resolved).Any(a => a.Contains('\r') || a.Contains('\n')))
                throw new InvalidDataException("Windows .cmd/.bat arguments cannot contain newlines; use a native executable for these arguments");
            string EscapeMeta(string value) => Regex.Replace(value, @"([()\[\]%!^""`<>&|;, *?])", "^$1");
            string EscapeArgument(string value)
            {
                value = Regex.Replace(value, @"(\\*)""", "$1$1\\\"");
                value = Regex.Replace(value, @"(\\*)$", "$1$1");
                value = EscapeMeta("\"" + value + "\"");
                return Regex.IsMatch(resolved, @"node_modules[\\/]\.bin[\\/]", RegexOptions.IgnoreCase) ? EscapeMeta(value) : value;
            }
            start.Arguments = "/d /s /v:off /c \"" + EscapeMeta(resolved) + " " +
                string.Join(" ", arguments.Select(EscapeArgument)) + "\"";
        }
        else
        {
            start.FileName = resolved;
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static string ResolveCommand(string command, string cwd, IDictionary<string, string?> environment)
    {
        var hasPath = command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar);
        var path = (environment.TryGetValue("PATH", out var searchPath) ? searchPath ?? "" : "").Split(Path.PathSeparator);
        IEnumerable<string> directories = hasPath ? [cwd] : OperatingSystem.IsWindows() ? new[] { cwd }.Concat(path) : path;
        var extensions = OperatingSystem.IsWindows() && Path.GetExtension(command).Length == 0 ?
            (environment.TryGetValue("PATHEXT", out var pathExt) ? pathExt ?? ".COM;.EXE;.BAT;.CMD" : ".COM;.EXE;.BAT;.CMD").Split(';') : [""];
        foreach (var directory in directories)
        {
            foreach (var suffix in extensions)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), command + suffix), cwd);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return hasPath ? Path.GetFullPath(command, cwd) : command;
    }

    private async Task ReadMessagesAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length != 0) await DeliverAsync(line, lifetime.Token);
            }
            if (!lifetime.IsCancellationRequested)
            {
                if (errorTask is not null) await errorTask;
                string captured;
                lock (stderr) captured = stderr.ToString().Trim();
                Failed(new IOException("MCP server connection closed" +
                    (captured.Length == 0 ? "" : $"\n\nServer stderr:\n{captured}")));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Failed(error);
        }
    }

    private async Task ReadErrorsAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer, lifetime.Token)) != 0)
            {
                var text = new string(buffer, 0, count);
                // Keep diagnostics bounded while continuing to stream authentication prompts.
                lock (stderr)
                {
                    stderr.Append(text);
                    if (stderr.Length > 65536) stderr.Remove(0, stderr.Length - 65536);
                }
                settings.Diagnostics($"[{name}] {text.TrimEnd('\r', '\n')}");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException error) { settings.Debug($"Failed to read server stderr: {error.Message}"); }
    }

    public override async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (process is null) throw new InvalidOperationException("Stdio transport has not started");
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { writeLock.Release(); }
    }

    public override async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (process is not null)
        {
            process.StandardInput.Close();
            try
            {
                using var timeout = new CancellationTokenSource(2000);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (readerTask is not null) await readerTask;
            if (errorTask is not null) await errorTask;
            process.Dispose();
        }
        lifetime.Dispose();
        writeLock.Dispose();
    }
}
