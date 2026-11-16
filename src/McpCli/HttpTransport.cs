using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace McpCli;

public sealed class HttpTransport(ServerConfig config, RuntimeSettings settings) : RpcTransport
{
    private readonly HttpClient client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource lifetime = new();
    private string? sessionId;
    private Task? eventTask;
    private sealed class EventState
    {
        public string? LastId { get; set; }
        public int? RetryMs { get; set; }
        public bool ReceivedResponse { get; set; }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("MCP HTTP URL must be an absolute http or https URL");
        return Task.CompletedTask;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, JsonObject? message = null)
    {
        var request = new HttpRequestMessage(method, config.Url);
        if (message is not null) request.Content = new StringContent(McpJson.Serialize(message), Encoding.UTF8, "application/json");
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        if (ProtocolVersion is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        foreach (var (name, value) in config.Map("headers"))
        {
            if (request.Headers.TryGetValues(name, out _)) request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.Remove(name);
                if (!request.Content.Headers.TryAddWithoutValidation(name, value))
                    throw new InvalidDataException($"Invalid HTTP request header: {name}");
            }
        }
        request.Headers.Remove("Accept");
        if (method != HttpMethod.Get) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (message is not null) request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    public override async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, message);
        using var response = await SendHttpAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) sessionId = values.First();
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Accepted) return;
        if (!message.ContainsKey("method") || message["id"] is null) return;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType == "text/event-stream")
        {
            var expectedId = RpcConnection.CorrelationId(message["id"]);
            var state = new EventState();
            try { await ReadEventsAsync(response, expectedId, state, cancellationToken); }
            catch (IOException error) when (state.LastId is not null)
            {
                settings.Debug($"Resuming interrupted HTTP response: {error.Message}");
            }
            if (!state.ReceivedResponse)
            {
                if (state.LastId is null) throw new IOException("MCP HTTP event stream closed before the response");
                await ResumeAsync(expectedId, state, cancellationToken);
            }
        }
        else if (contentType == "application/json")
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (McpJson.Parse(json) is JsonArray batch)
                foreach (var item in batch) await DeliverAsync(McpJson.Serialize(item), cancellationToken);
            else await DeliverAsync(json, cancellationToken);
        }
        else throw new InvalidDataException($"Unexpected MCP HTTP content type: {contentType ?? "(missing)"}");
    }

    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage original, CancellationToken token)
    {
        var current = original;
        HttpRequestMessage? redirected = null;
        var body = original.Content is null ? null : await original.Content.ReadAsByteArrayAsync(token);
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
                    return response;
                using (response)
                {
                    if (redirects == 20) throw new HttpRequestException("Too many HTTP redirects");
                    var target = new Uri(current.RequestUri!, response.Headers.Location);
                    if (target.Scheme is not ("http" or "https")) throw new HttpRequestException("Invalid HTTP redirect scheme");
                    var changeToGet = ((int)response.StatusCode is 301 or 302 && current.Method == HttpMethod.Post) ||
                        ((int)response.StatusCode == 303 && current.Method != HttpMethod.Get && current.Method != HttpMethod.Head);
                    var next = new HttpRequestMessage(changeToGet ? HttpMethod.Get : current.Method, target);
                    var sameOrigin = Uri.Compare(current.RequestUri, target, UriComponents.SchemeAndServer,
                        UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
                    foreach (var header in current.Headers)
                    {
                        if (!sameOrigin && header.Key.ToLowerInvariant() is "authorization" or "proxy-authorization" or "cookie" or "cookie2")
                            continue;
                        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                        next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    if (changeToGet) body = null;
                    if (body is not null)
                    {
                        next.Content = new ByteArrayContent(body);
                        foreach (var header in current.Content!.Headers)
                            next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    redirected?.Dispose();
                    redirected = next;
                    current = next;
                }
            }
        }
        finally { redirected?.Dispose(); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            null, response.StatusCode);
    }

    public override void Initialized() => eventTask = ListenAsync();

    private async Task ListenAsync()
    {
        try
        {
            await ResumeAsync(null, new EventState(), lifetime.Token, immediate: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // The optional notification stream must not invalidate independent POST responses.
            settings.Debug($"HTTP notification stream closed: {error.Message}");
        }
    }

    private async Task ResumeAsync(string? expectedId, EventState state, CancellationToken token, bool immediate = false)
    {
        var failedAttempts = 0;
        while (!state.ReceivedResponse)
        {
            if (!immediate)
                await Task.Delay(state.RetryMs ?? (int)Math.Min(30000, 1000 * Math.Pow(1.5, failedAttempts)), token);
            immediate = false;
            try
            {
                using var request = CreateRequest(HttpMethod.Get);
                if (!string.IsNullOrEmpty(state.LastId)) request.Headers.TryAddWithoutValidation("Last-Event-ID", state.LastId);
                using var response = await SendHttpAsync(request, token);
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed && expectedId is null) return;
                await EnsureSuccessAsync(response, token);
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    throw new InvalidDataException("Expected text/event-stream from MCP GET endpoint");
                await ReadEventsAsync(response, expectedId, state, token);
                failedAttempts = 0;
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                if (++failedAttempts >= 2) throw;
                settings.Debug($"HTTP SSE reconnect failed: {error.Message}");
            }
        }
    }

    private async Task ReadEventsAsync(HttpResponseMessage response, string? expectedId,
        EventState state, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var eventType = "";
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length != 0 && eventType is "" or "message")
                {
                    var json = data.ToString().TrimEnd('\n');
                    try
                    {
                        await DeliverAsync(json, cancellationToken);
                        if (McpJson.Parse(json) is JsonObject obj && obj["id"] is not null &&
                            !obj.ContainsKey("method") && (expectedId is null || RpcConnection.CorrelationId(obj["id"]) == expectedId))
                        {
                            state.ReceivedResponse = true;
                            if (expectedId is not null) return;
                        }
                    }
                    catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or FormatException)
                    {
                        settings.Debug($"Ignoring malformed SSE message: {error.Message}");
                    }
                }
                data.Clear();
                eventType = "";
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "data": data.Append(value).Append('\n'); break;
                case "event": eventType = value; break;
                case "id" when !value.Contains('\0'): state.LastId = value; break;
                case "retry" when value.All(char.IsAsciiDigit) && int.TryParse(value, out var retry): state.RetryMs = retry; break;
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (eventTask is not null) await eventTask;
        // Closing an SDK transport does not terminate the remote MCP session with DELETE.
        client.Dispose();
        lifetime.Dispose();
    }
}
