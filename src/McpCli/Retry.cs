using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace McpCli;

public static class Retry
{
    public static bool IsTransient(Exception error)
    {
        if (error is HttpRequestException { StatusCode: { } status } && (int)status is 429 or 502 or 503 or 504)
            return true;
        if (error is SocketException socket && socket.SocketErrorCode is
            SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.TimedOut or
            SocketError.HostNotFound or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.TryAgain)
            return true;
        if (error.InnerException is not null && IsTransient(error.InnerException)) return true;
        return Regex.IsMatch(error.Message,
            @"^(502|503|504|429)\b|\b(http|status(\s+code)?)\s*(502|503|504|429)\b|" +
            @"\b(502|503|504|429)\s+(bad gateway|service unavailable|gateway timeout|too many requests)|" +
            @"network\s*(error|fail|unavailable|timeout)|connection\s*(reset|refused|timeout)|\btimeout\b|" +
            @"\b(ECONNREFUSED|ECONNRESET|ETIMEDOUT|ENOTFOUND|EPIPE|ENETUNREACH|EHOSTUNREACH|EAI_AGAIN)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, string operation,
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(settings.TimeoutMs);
        var clock = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{operation}: timeout budget exhausted after {clock.ElapsedMilliseconds}ms");
            }
            catch (Exception error) when (error is not OperationCanceledException &&
                attempt < settings.MaxRetries && IsTransient(error) &&
                settings.TimeoutMs - clock.ElapsedMilliseconds > 1000)
            {
                var maximum = Math.Min(10000, Math.Max(0, settings.TimeoutMs - 5000) / 2.0);
                var delay = Math.Min(settings.RetryDelayMs * Math.Pow(2, attempt), maximum);
                delay = Math.Round(delay * (0.75 + Random.Shared.NextDouble() * 0.5));
                delay = Math.Max(0, Math.Min(delay, settings.TimeoutMs - clock.ElapsedMilliseconds - 1000));
                settings.Debug($"{operation} failed (attempt {attempt + 1}/{settings.MaxRetries + 1}): " +
                    $"{error.Message}. Retrying in {delay}ms...");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), budget.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"{operation}: timeout budget exhausted");
                }
            }
        }
    }
}
