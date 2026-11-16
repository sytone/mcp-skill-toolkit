using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace McpCli;

public static class Daemon
{
    public static string PipeName(string name, ServerConfig config, RuntimeSettings settings, string? applicationDirectory = null)
    {
        var identity = string.Join('\n', Environment.UserName, settings.HomeDirectory,
            applicationDirectory ?? AppContext.BaseDirectory, settings.WorkingDirectory, Cli.Version, name, config.Hash());
        return "mcp-cli-csharp-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..40];
    }

    public static async Task<IMcpConnection> ConnectAsync(string name, ServerConfig config,
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        var pipeName = PipeName(name, config, settings);
        if (!await PingAsync(pipeName, settings, cancellationToken))
        {
            await SpawnAsync(pipeName, name, config, settings, cancellationToken);
            if (!await PingAsync(pipeName, settings, cancellationToken))
                throw new IOException("Daemon did not become ready");
        }
        return new DaemonConnection(pipeName, config, settings);
    }

    private static async Task<bool> PingAsync(string pipeName, RuntimeSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync(pipeName, "ping", null, null, settings, 300, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException)
        {
            settings.Debug($"Daemon ping unavailable: {error.Message}");
            return false;
        }
    }

    private static async Task SpawnAsync(string pipeName, string name, ServerConfig config,
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot determine CLI executable path");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.Combine(AppContext.BaseDirectory, "mcp-cli.dll"));
        arguments.AddRange(["--daemon", pipeName, name]);
        using var process = Process.Start(StdioTransport.CreateStartInfo(executable, arguments, settings.WorkingDirectory))
            ?? throw new IOException("Could not start daemon");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(5000);
        var stderrTask = ForwardErrorsAsync(process.StandardError, settings, startup.Token);
        var ready = false;
        try
        {
            // Configuration (including credentials) is never placed in the process command line.
            await process.StandardInput.WriteLineAsync(McpJson.Serialize(config.Value).AsMemory(), startup.Token);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(startup.Token).AsTask().WaitAsync(startup.Token) is { } line)
            {
                if (line == "DAEMON_READY") { ready = true; break; }
                settings.Debug($"Daemon startup: {line}");
            }
            if (!ready)
            {
                // Another concurrent invocation may have won the first-instance race.
                await RequestAsync(pipeName, "ping", null, null, settings, 5000, startup.Token);
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Daemon spawn timeout");
        }
        finally
        {
            if (!ready && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await startup.CancelAsync();
            await stderrTask;
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }
    }

    private static async Task ForwardErrorsAsync(StreamReader reader, RuntimeSettings settings, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token).AsTask().WaitAsync(token) is { } line)
            {
                if (line == "DAEMON_READY") return;
                settings.Diagnostics(line);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException error) { settings.Debug($"Daemon diagnostics closed: {error.Message}"); }
    }

    public static async Task<JsonNode?> RequestAsync(string pipeName, string type, string? tool, JsonNode? args,
        RuntimeSettings settings, int timeoutMs, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var id = Guid.NewGuid().ToString("N");
            var request = new JsonObject
            {
                ["id"] = id, ["type"] = type, ["toolName"] = tool, ["args"] = args?.DeepClone(),
                ["timeoutMs"] = timeoutMs, ["maxRetries"] = settings.MaxRetries, ["retryDelayMs"] = settings.RetryDelayMs
            };
            await writer.WriteLineAsync(McpJson.Serialize(request).AsMemory(), timeout.Token);
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (McpJson.Parse(line) is not JsonObject response) throw new IOException("Invalid response from daemon");
                if (response["diagnostic"] is { } diagnostic)
                {
                    settings.Diagnostics(diagnostic.GetValue<string>());
                    continue;
                }
                if (response["id"]?.ToString() != id) throw new IOException("Invalid response ID from daemon");
                if (response["success"]?.GetValue<bool>() != true)
                    throw new IOException(response["error"]?["message"]?.GetValue<string>() ?? "Daemon request failed");
                return response["data"]?.DeepClone();
            }
            throw new IOException("Daemon connection closed before response");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Daemon request timeout");
        }
    }

    public static async Task<int> RunAsync(string pipeName, string name, ServerConfig config,
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        if (pipeName != PipeName(name, config, settings))
            throw new InvalidDataException("Daemon endpoint does not match server configuration");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NamedPipeServerStream NewListener(bool first = false) => new(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));
        var listener = NewListener(true);
        McpConnection? connection = null;
        var subscribers = new ConcurrentDictionary<Guid, Channel<JsonObject>>();
        var requests = new ConcurrentDictionary<Guid, Task>();
        var lastActivity = Environment.TickCount64;
        var active = 0;
        Task? idleTask = null;
        try
        {
            connection = await McpConnection.ConnectDirectAsync(name, config, settings, lifetime.Token);
            settings.Diagnostics = message =>
            {
                foreach (var channel in subscribers.Values)
                    channel.Writer.TryWrite(new JsonObject { ["diagnostic"] = message });
            };
            Console.Error.WriteLine("DAEMON_READY");
            Console.WriteLine("DAEMON_READY");
            idleTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(500, settings.DaemonTimeoutMs)));
                try
                {
                    while (await timer.WaitForNextTickAsync(lifetime.Token))
                        if (Volatile.Read(ref active) == 0 &&
                            Environment.TickCount64 - Interlocked.Read(ref lastActivity) >= settings.DaemonTimeoutMs)
                            await lifetime.CancelAsync();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }, CancellationToken.None);

            while (!lifetime.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(lifetime.Token);
                var accepted = listener;
                // Keep one instance alive throughout handoff, so another process cannot own this endpoint.
                listener = NewListener();
                var key = Guid.NewGuid();
                Interlocked.Increment(ref active);
                var task = HandleAsync(accepted, key);
                requests[key] = task;
                _ = task.ContinueWith(_ => requests.TryRemove(key, out var ignored),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            async Task HandleAsync(NamedPipeServerStream pipe, Guid key)
            {
                await using var owned = pipe;
                using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                requestLifetime.CancelAfter(5000);
                using var observation = CancellationTokenSource.CreateLinkedTokenSource(requestLifetime.Token);
                var channel = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
                subscribers[key] = channel;
                var sending = SendFramesAsync(pipe, channel.Reader, requestLifetime.Token);
                var observing = Task.CompletedTask;
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                try
                {
                    var line = await reader.ReadLineAsync(requestLifetime.Token);
                    JsonObject? request = null;
                    JsonObject response;
                    try
                    {
                        request = McpJson.Parse(line ?? "") as JsonObject ??
                            throw new InvalidDataException("Invalid JSON request");
                        var timeoutMs = request["timeoutMs"]?.GetValue<int>() ?? settings.TimeoutMs;
                        if (timeoutMs <= 0) throw new InvalidDataException("Daemon request timeout must be positive");
                        var retryOptions = new RetryOptions(timeoutMs,
                            request["maxRetries"]?.GetValue<int>() ?? settings.MaxRetries,
                            request["retryDelayMs"]?.GetValue<int>() ?? settings.RetryDelayMs);
                        requestLifetime.CancelAfter(timeoutMs);
                        observing = ObserveDisconnectAsync();
                        var type = request["type"]?.GetValue<string>();
                        JsonNode? data = type switch
                        {
                            "ping" => JsonValue.Create("pong"),
                            "getInstructions" => JsonValue.Create(await connection.GetInstructionsAsync(requestLifetime.Token)),
                            "listTools" => new JsonArray((await connection.ListToolsAsync(requestLifetime.Token, retryOptions)).Select(t =>
                                (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description,
                                    ["inputSchema"] = t.InputSchema.DeepClone() }).ToArray()),
                            "callTool" => await connection.CallToolAsync(
                                request["toolName"]?.GetValue<string>() ?? throw new InvalidDataException("toolName required"),
                                request["args"]?.DeepClone() ?? new JsonObject(), requestLifetime.Token, retryOptions),
                            "close" => JsonValue.Create("closing"),
                            _ => throw new InvalidDataException($"Unknown request type: {type}")
                        };
                        response = new JsonObject { ["id"] = request["id"]?.DeepClone(), ["success"] = true, ["data"] = data };
                        if (type == "close") lifetime.CancelAfter(100);
                    }
                    catch (Exception error)
                    {
                        response = new JsonObject { ["id"] = request?["id"]?.DeepClone(), ["success"] = false,
                            ["error"] = new JsonObject { ["code"] = "EXECUTION_ERROR", ["message"] = error.Message } };
                    }
                    channel.Writer.TryWrite(response);

                    async Task ObserveDisconnectAsync()
                    {
                        try
                        {
                            var extra = await reader.ReadLineAsync(observation.Token);
                            if (extra is not null) settings.Debug("Unexpected extra daemon request frame; closing client connection");
                            await requestLifetime.CancelAsync();
                        }
                        catch (OperationCanceledException) when (observation.IsCancellationRequested) { }
                        catch (IOException error)
                        {
                            settings.Debug($"Daemon client disconnected: {error.Message}");
                            await requestLifetime.CancelAsync();
                        }
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    settings.Debug($"Daemon client disconnected: {error.Message}");
                }
                finally
                {
                    await observation.CancelAsync();
                    await observing;
                    subscribers.TryRemove(key, out _);
                    channel.Writer.TryComplete();
                    try { await sending; }
                    finally
                    {
                        Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
                        Interlocked.Decrement(ref active);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            await listener.DisposeAsync();
            await Task.WhenAll(requests.Values);
            if (idleTask is not null) await idleTask;
            if (connection is not null) await McpConnection.SafeCloseAsync(connection, settings);
        }
        return 0;
    }

    private static async Task SendFramesAsync(Stream stream, ChannelReader<JsonObject> frames, CancellationToken token)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        try
        {
            await foreach (var frame in frames.ReadAllAsync(token))
                await writer.WriteLineAsync(McpJson.Serialize(frame).AsMemory(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { /* The requesting CLI has closed its pipe. */ }
    }

    private sealed class DaemonConnection(string pipe, ServerConfig config, RuntimeSettings settings) : IMcpConnection
    {
        public bool IsDaemon => true;
        public async Task<ToolInfo[]> ListToolsAsync(CancellationToken cancellationToken)
        {
            var result = await RequestAsync(pipe, "listTools", null, null, settings, settings.TimeoutMs, cancellationToken);
            if (result is not JsonArray tools) throw new InvalidDataException("Invalid daemon tools response");
            return tools.Select(ToolInfo.FromJson).Where(t => config.IsToolAllowed(t.Name)).ToArray();
        }
        public Task<JsonNode?> CallToolAsync(string name, JsonNode? arguments, CancellationToken cancellationToken)
        {
            if (!config.IsToolAllowed(name)) throw new InvalidOperationException($"Tool \"{name}\" is disabled by configuration");
            return RequestAsync(pipe, "callTool", name, arguments, settings, settings.TimeoutMs, cancellationToken);
        }
        public async Task<string?> GetInstructionsAsync(CancellationToken cancellationToken) =>
            (await RequestAsync(pipe, "getInstructions", null, null, settings, settings.TimeoutMs, cancellationToken))?.GetValue<string>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
