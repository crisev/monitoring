using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Monitor.Core.Api;

namespace Monitor.Core.Pipe;

// The agent and the service talk over the named pipe \\.\pipe\MonitorV2, one JSON object per line.
//
//   agent -> service: hello, sample (every 5 s), game (GAME ON/OFF), screenshot (reply to capture), bye (session ending)
//   service -> agent: status (every second), toast, capture, closeBrowsers

public static class MessageTypes
{
    public const string Hello = "hello";
    public const string Sample = "sample";
    public const string Game = "game";
    public const string Screenshot = "screenshot";
    public const string Bye = "bye";

    public const string Status = "status";
    public const string Toast = "toast";
    public const string Capture = "capture";
    public const string CloseBrowsers = "closeBrowsers";
}

public sealed class PipeMessage
{
    public string Type { get; set; } = "";

    // agent -> service
    public string? Version { get; set; }
    public Sample? Sample { get; set; }
    /// <summary>game: true = GAME ON, false = GAME OFF.</summary>
    public bool? On { get; set; }
    /// <summary>screenshot: base64 JPEG.</summary>
    public string? Jpeg { get; set; }

    // service -> agent
    public AgentStatus? Status { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public bool? Warning { get; set; }
}

/// <summary>What the child was doing during the last sampling period.</summary>
public sealed class Sample
{
    public double Seconds { get; set; }
    public ForegroundApp? Foreground { get; set; }
    public List<AudioApp> Audio { get; set; } = [];
    /// <summary>The agent's own view; the service decides using Windows session state instead.</summary>
    public bool Locked { get; set; }
}

public sealed class ForegroundApp
{
    public int Pid { get; set; }
    public string App { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>Domain of the active Edge tab, read from the address bar.</summary>
    public string? Site { get; set; }
}

public sealed class AudioApp
{
    public int Pid { get; set; }
    public string App { get; set; } = "";
}

/// <summary>Shown in the tray menu and status dialog.</summary>
public sealed class AgentStatus
{
    /// <summary>"school" or "gaming".</summary>
    public string Mode { get; set; } = "school";
    public bool Monitored { get; set; } = true;
    public bool Enrolled { get; set; }
    public bool Online { get; set; }
    /// <summary>A GAME ON/OFF request is being sent.</summary>
    public bool Busy { get; set; }
    public int GameBalanceSeconds { get; set; }
    public int GameUsedTodaySeconds { get; set; }
    public int LeaseRemainingSeconds { get; set; }
    public int? ScreenLimitSeconds { get; set; }
    public int? ScreenRemainingSeconds { get; set; }
    public int? OfflineRemainingSeconds { get; set; }
    public string? Notice { get; set; }

    public bool IsGaming => Mode == "gaming";
}

/// <summary>Reads and writes <see cref="PipeMessage"/>s as JSON lines on a pipe.</summary>
public sealed class MessageStream : IDisposable
{
    private readonly PipeStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public MessageStream(PipeStream pipe)
    {
        this.pipe = pipe;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        writer = new StreamWriter(pipe, utf8, bufferSize: 64 * 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
    }

    public bool IsConnected => pipe.IsConnected;

    /// <summary>The next message, or null when the other side closed the pipe.</summary>
    public async Task<PipeMessage?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var msg = JsonSerializer.Deserialize<PipeMessage>(line, ApiClient.Json);
                if (msg is not null) return msg;
            }
            catch (JsonException)
            {
                // Ignore malformed lines.
            }
        }
    }

    public async Task WriteAsync(PipeMessage message, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(message, ApiClient.Json);
        await writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public void Dispose()
    {
        reader.Dispose();
        writer.Dispose();
        pipe.Dispose();
        writeLock.Dispose();
    }
}
