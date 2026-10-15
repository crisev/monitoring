using System.IO.Pipes;
using Monitor.Core;
using Monitor.Core.Pipe;

namespace Monitor.Agent;

/// <summary>Connection to the service's pipe, reconnecting every 2 seconds while it is unavailable.</summary>
internal sealed class ServiceLink(Action<PipeMessage> onMessage, Action<bool> onConnected) : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private volatile MessageStream? stream;

    public bool Connected => stream is not null;

    public void Start() => _ = Task.Run(() => RunAsync(cts.Token));

    public async Task SendAsync(PipeMessage message)
    {
        var s = stream;
        if (s is null) return;
        try
        {
            await s.WriteAsync(message, cts.Token);
        }
        catch
        {
            // The read loop notices and reconnects.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        bool loggedFailure = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5_000, ct);
                stream = new MessageStream(pipe);
                Log.Info("Connected to the service.");
                loggedFailure = false;
                onConnected(true);
                await SendAsync(new PipeMessage { Type = MessageTypes.Hello, Version = Format.Version });
                while (await stream.ReadAsync(ct) is { } msg) onMessage(msg);
                Log.Warn("The service closed the connection.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!loggedFailure) Log.Warn($"Cannot reach the service: {ex.Message}");
                loggedFailure = true;
            }
            finally
            {
                var old = stream;
                stream = null;
                old?.Dispose();
                if (old is not null) onConnected(false);
            }
            try
            {
                await Task.Delay(2_000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        stream?.Dispose();
    }
}
