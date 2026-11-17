namespace McpCli.Tests;

[TestClass]
public sealed class RetryTests
{
    [TestMethod]
    [DataRow("502 Bad Gateway", true)]
    [DataRow("HTTP 503", true)]
    [DataRow("status code 429", true)]
    [DataRow("network error occurred", true)]
    [DataRow("connection reset by peer", true)]
    [DataRow("request timeout", true)]
    [DataRow("ECONNREFUSED", false)]
    [DataRow("EPIPE while validating input", false)]
    [DataRow("Error at line 502 in file", false)]
    [DataRow("Port 5029 is in use", false)]
    [DataRow("social network tool failed", false)]
    [DataRow("Invalid JSON", false)]
    [DataRow("401 Unauthorized", false)]
    public void RecognizesTransientFailuresWithoutNumericFalsePositives(string message, bool expected) =>
        Assert.AreEqual(expected, Retry.IsTransient(new IOException(message)));

    [TestMethod]
    public async Task RetriesOnlyUpToConfiguredLimitAndSurfacesLastError()
    {
        var attempts = 0;
        var settings = new RuntimeSettings
        {
            Environment = name => name switch { "MCP_MAX_RETRIES" => "2", "MCP_RETRY_DELAY" => "1", _ => null }
        };
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => Retry.RunAsync<int>(_ =>
        {
            attempts++;
            throw new IOException("HTTP 503 Service Unavailable");
        }, "test", settings, CancellationToken.None));
        Assert.AreEqual(3, attempts);
        StringAssert.Contains(error.Message, "503");
    }

    [TestMethod]
    public async Task TimeoutBudgetCancelsTheActualOperation()
    {
        var cancelled = false;
        var settings = new RuntimeSettings { Environment = name => name == "MCP_TIMEOUT" ? "1" : null };
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => Retry.RunAsync(async token =>
        {
            try { await Task.Delay(10000, token); }
            finally { cancelled = token.IsCancellationRequested; }
            return 1;
        }, "test", settings, CancellationToken.None));
        Assert.IsTrue(cancelled);
    }

    [TestMethod]
    [DataRow(System.Net.Sockets.SocketError.ConnectionRefused)]
    [DataRow(System.Net.Sockets.SocketError.ConnectionReset)]
    [DataRow(System.Net.Sockets.SocketError.TimedOut)]
    public void ParityActualSocketErrorsRemainRetryable(System.Net.Sockets.SocketError code)
    {
        var error = new System.Net.Sockets.SocketException((int)code);
        Assert.IsTrue(Retry.IsTransient(error));
        Assert.IsTrue(Retry.IsTransient(new IOException("Transport failed", error)));
    }

    [TestMethod]
    public void ParityNativeBrokenPipeErrorsAreRetryableButErrorTextIsNot()
    {
        Assert.IsTrue(Retry.IsTransient(new System.Net.Sockets.SocketException(32)));
        Assert.IsTrue(Retry.IsTransient(new System.Net.Sockets.SocketException(
            (int)System.Net.Sockets.SocketError.Shutdown)));
        if (OperatingSystem.IsWindows())
        {
            var brokenPipe = new System.ComponentModel.Win32Exception(109);
            Assert.IsTrue(Retry.IsTransient(brokenPipe));
            Assert.IsTrue(Retry.IsTransient(new IOException("Pipe closed", brokenPipe)));
        }
        Assert.IsFalse(Retry.IsTransient(new IOException("EPIPE while validating input")));
    }

    [TestMethod]
    public async Task ParityRetryPolicyRetriesANativeBrokenPipeFailure()
    {
        var attempts = 0;
        var settings = new RuntimeSettings
        {
            Environment = name => name switch { "MCP_MAX_RETRIES" => "1", "MCP_RETRY_DELAY" => "1", _ => null }
        };
        var result = await Retry.RunAsync(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<int>(new System.ComponentModel.Win32Exception(109))
                : Task.FromResult(42);
        }, "test", settings, CancellationToken.None);
        Assert.AreEqual(42, result);
        Assert.AreEqual(2, attempts);
    }
}
