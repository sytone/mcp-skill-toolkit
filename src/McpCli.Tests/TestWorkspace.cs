using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]

namespace McpCli.Tests;

public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

public sealed class TestWorkspace : IAsyncDisposable
{
    public static string RepositoryRoot { get; } = FindRoot();
    private static string BuildConfiguration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    public static string CliPath => System.Environment.GetEnvironmentVariable("MCP_CLI_TEST_EXECUTABLE") ??
        Path.Combine(RepositoryRoot, "src", "McpCli", "bin", BuildConfiguration, "net10.0", "mcp-cli.dll");
    public static string ServerPath => Path.Combine(RepositoryRoot, "src", "McpCli.TestServer", "bin", BuildConfiguration, "net10.0", "McpCli.TestServer.dll");
    public string Root { get; } = Path.Combine(RepositoryRoot, ".test-tmp", "csharp-" + Guid.NewGuid().ToString("N"));
    public string ConfigPath => Path.Combine(Root, "mcp_servers.json");
    private readonly System.Collections.Concurrent.ConcurrentBag<Process> processes = [];
    private readonly List<(string Name, ServerConfig Config)> daemons = [];
    public Dictionary<string, string?> EnvironmentVariables { get; } = new()
    {
        ["MCP_NO_DAEMON"] = "1", ["MCP_MAX_RETRIES"] = "0", ["MCP_TIMEOUT"] = "10",
        ["MCP_DEBUG"] = null, ["MCP_CONFIG_PATH"] = null, ["MCP_STRICT_ENV"] = null, ["MCP_CONCURRENCY"] = null,
        ["MCP_RETRY_DELAY"] = null, ["MCP_DAEMON_TIMEOUT"] = "2", ["NO_COLOR"] = "1"
    };

    public TestWorkspace() => Directory.CreateDirectory(Root);
    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "psakefile.ps1"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    public ServerConfig StdioConfig(JsonObject? environment = null)
    {
        environment ??= new JsonObject();
        environment["FAKE_PID_PATH"] = Path.Combine(Root, "server-pids.txt");
        return new(new JsonObject
        {
            ["command"] = "dotnet", ["args"] = new JsonArray(ServerPath, "stdio"),
            ["cwd"] = Root, ["env"] = environment
        });
    }

    public async Task WriteConfigAsync(params (string Name, ServerConfig Config)[] servers)
    {
        var config = new JsonObject();
        foreach (var (name, server) in servers) config[name] = server.Value.DeepClone();
        await File.WriteAllTextAsync(ConfigPath, new JsonObject { ["mcpServers"] = config }.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { MaxDepth = int.MaxValue }));
    }

    public Process StartCli(IEnumerable<string> arguments, bool withConfig = true)
    {
        var start = ProcessInfo(CliPath);
        if (withConfig) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(ConfigPath); }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in EnvironmentVariables)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        var process = Process.Start(start)!;
        processes.Add(process);
        return process;
    }

    public async Task<CliResult> RunAsync(string[] arguments, string? stdin = null, bool withConfig = true)
    {
        var process = StartCli(arguments, withConfig);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(30000);
        await process.WaitForExitAsync(timeout.Token);
        return new(process.ExitCode, await stdout, await stderr);
    }

    public void TrackDaemon(string name, ServerConfig server) => daemons.Add((name, server));

    public async Task<ServerConfig> StartHttpAsync(Dictionary<string, string>? environment = null)
    {
        var start = ProcessInfo(ServerPath);
        start.ArgumentList.Add("http");
        if (environment is not null) foreach (var (name, value) in environment) start.Environment[name] = value;
        var process = Process.Start(start)!;
        processes.Add(process);
        using var timeout = new CancellationTokenSource(10000);
        var address = await process.StandardOutput.ReadLineAsync(timeout.Token);
        if (address is null) throw new IOException("HTTP fixture failed: " + await process.StandardError.ReadToEndAsync(timeout.Token));
        return new(new JsonObject { ["url"] = address });
    }

    private ProcessStartInfo ProcessInfo(string assembly)
    {
        var assemblyFile = Path.GetExtension(assembly).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(assemblyFile ? "dotnet" : assembly)
        {
            WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (assemblyFile) start.ArgumentList.Add(assembly);
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        var settings = new RuntimeSettings
        {
            WorkingDirectory = Root,
            Diagnostics = _ => { }
        };
        foreach (var (name, server) in daemons)
        {
            var pipe = Daemon.PipeName(name, server, settings, Path.GetDirectoryName(CliPath) + Path.DirectorySeparatorChar);
            try { await Daemon.RequestAsync(pipe, "close", null, null, settings, 1000, CancellationToken.None); }
            catch (Exception error) when (error is IOException or TimeoutException) { /* Already exited or never started. */ }
        }
        foreach (var process in processes)
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(5000);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            process.Dispose();
        }
        var pidPath = Path.Combine(Root, "server-pids.txt");
        if (File.Exists(pidPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(pidPath))
            {
                try
                {
                    using var process = Process.GetProcessById(int.Parse(line));
                    using var timeout = new CancellationTokenSource(5000);
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (ArgumentException) { /* Fixture already exited. */ }
            }
        }
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(Root, recursive: true); break; }
            catch (IOException error) when ((error.HResult & 0xffff) == 32 && attempt < 19)
            {
                // Windows can signal process exit before releasing the process's current-directory handle.
                await Task.Delay(100);
            }
        }
    }
}
