using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SaveEditor.Tests;

/// <summary>Verifies uploads produce a log line on both success and failure,
/// without asserting anything about a specific logging backend/sink.</summary>
public class ServerLoggingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServerLoggingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task SuccessfulUpload_LogsFileNameAndFormat()
    {
        var capture = new CapturingLoggerProvider();
        var client = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(lb => lb.AddProvider(capture))
        ).CreateClient();

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("{\"gold\":1}")), "file", "test.json" },
        };

        var response = await client.PostAsync("/api/upload", content);
        response.EnsureSuccessStatusCode();

        Assert.Contains(capture.Messages, m => m.Contains("Upload succeeded") && m.Contains("test.json") && m.Contains("json"));
    }

    [Fact]
    public async Task FailedUpload_LogsFileName()
    {
        var capture = new CapturingLoggerProvider();
        var client = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(lb => lb.AddProvider(capture))
        ).CreateClient();

        // Valid "SQLite format 3\0" header, corrupt body: SqliteFormat.Read
        // throws SaveFormatException (see the T4 fix), a realistic upload error.
        byte[] data = [.. Encoding.UTF8.GetBytes("SQLite format 3\0"), .. new byte[64]];
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(data), "file", "broken.db" },
        };

        var response = await client.PostAsync("/api/upload", content);
        Assert.False(response.IsSuccessStatusCode);

        Assert.Contains(capture.Messages, m => m.Contains("Upload failed") && m.Contains("broken.db"));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose() { }

        private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }
}
