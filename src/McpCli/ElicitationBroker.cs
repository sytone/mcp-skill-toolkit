using System.Text.Json.Nodes;

namespace McpCli;

public sealed class ApprovalRequiredException(JsonObject request) : Exception("User approval is required")
{
    public JsonObject Request { get; } = request;
}

public sealed class ElicitationBroker(string server, RuntimeSettings settings, CancellationToken lifetime) : IAsyncDisposable
{
    private sealed class Operation(string tool, CancellationToken lifetime)
    {
        public string Tool { get; } = tool;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public TaskCompletionSource<JsonNode?> Next { get; set; } = NewCompletion();
        public Pending? Approval { get; set; }
    }

    private sealed record Pending(string Id, JsonObject Request, Func<JsonObject, CancellationToken, Task> Reply);
    private readonly object gate = new();
    private Operation? current;
    private Task execution = Task.CompletedTask;
    public bool HasPending { get { lock (gate) return current is not null; } }
    private static TaskCompletionSource<JsonNode?> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<JsonNode?> CallAsync(string tool, Func<CancellationToken, Task<JsonNode?>> execute,
        int timeoutMs, CancellationToken cancellationToken)
    {
        Operation operation;
        lock (gate)
        {
            if (current is not null)
                throw new InvalidOperationException("A call is already running or awaiting approval on this server; resume its approval ID first");
            operation = new(tool, lifetime);
            operation.Lifetime.CancelAfter(timeoutMs);
            current = operation;
            execution = ExecuteAsync(operation, execute);
        }
        try { return await operation.Next.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancel(operation);
            throw;
        }
    }

    public void Request(JsonNode? parameters, Func<JsonObject, CancellationToken, Task> reply)
    {
        // Validate the request without inventing any form answers.
        new ElicitationPolicy("cancel", null).Respond(parameters);
        lock (gate)
        {
            if (current is null || current.Lifetime.IsCancellationRequested)
                throw new McpException(-32602, "Elicitation in ask mode requires an active tool call");
            if (current.Approval is not null)
                throw new McpException(-32602, "Only one outstanding elicitation per tool call is supported");
            var id = Guid.NewGuid().ToString("N");
            var request = parameters!.DeepClone().AsObject();
            current.Approval = new(id, request, reply);
            current.Next.TrySetException(new ApprovalRequiredException(new JsonObject
            {
                ["status"] = "approval_required", ["approvalId"] = id, ["server"] = server,
                ["tool"] = current.Tool, ["request"] = request.DeepClone()
            }));
        }
    }

    public async Task<JsonNode?> ResumeAsync(string tool, string id, ElicitationPolicy policy,
        CancellationToken cancellationToken)
    {
        Operation operation;
        Pending pending;
        JsonObject response;
        Task<JsonNode?> next;
        lock (gate)
        {
            operation = current ?? throw new InvalidOperationException("Approval ID is unknown or expired; the tool was not replayed");
            pending = operation.Approval ??
                throw new InvalidOperationException("No approval is pending; this approval ID may already have been used");
            if (pending.Id != id || operation.Tool != tool)
                throw new InvalidOperationException("Approval ID does not match the pending tool request");
            response = policy.Respond(pending.Request);
            operation.Approval = null;
            operation.Next = NewCompletion();
            next = operation.Next.Task;
        }
        try { await pending.Reply(response, operation.Lifetime.Token); }
        catch
        {
            Cancel(operation);
            throw;
        }
        return await next.WaitAsync(cancellationToken);
    }

    private async Task ExecuteAsync(Operation operation, Func<CancellationToken, Task<JsonNode?>> execute)
    {
        try
        {
            var result = await execute(operation.Lifetime.Token);
            lock (gate) operation.Next.TrySetResult(result);
        }
        catch (Exception error)
        {
            var failure = error is OperationCanceledException ?
                new TimeoutException("Pending approval/tool call expired or was cancelled; the tool was not replayed") : error;
            lock (gate) operation.Next.TrySetException(failure);
            settings.Debug($"Retained tool call ended: {failure.Message}");
        }
        finally
        {
            Pending? pending;
            lock (gate)
            {
                pending = operation.Approval;
                operation.Approval = null;
            }
            if (pending is not null)
            {
                using var timeout = new CancellationTokenSource(1000);
                try { await pending.Reply(new JsonObject { ["action"] = "cancel" }, timeout.Token); }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
                {
                    settings.Debug($"Could not cancel expired elicitation: {error.Message}");
                }
            }
            lock (gate)
            {
                if (ReferenceEquals(current, operation)) current = null;
                operation.Lifetime.Dispose();
            }
        }
    }

    private void Cancel(Operation operation)
    {
        lock (gate)
            if (ReferenceEquals(current, operation)) operation.Lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task running;
        lock (gate)
        {
            current?.Lifetime.Cancel();
            running = execution;
        }
        await running;
    }
}
