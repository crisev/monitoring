using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Monitor.Core;
using Monitor.Core.Pipe;

namespace Monitor.Service;

/// <summary>One connected agent.</summary>
internal sealed class AgentConnection(int pid, int sessionId, MessageStream stream)
{
    public int Pid { get; } = pid;
    public int SessionId { get; } = sessionId;

    public async Task SendAsync(PipeMessage message)
    {
        try
        {
            await stream.WriteAsync(message);
        }
        catch
        {
            // The read loop notices the broken pipe and drops the connection.
        }
    }
}

/// <summary>
/// Named-pipe server for the agents. Users may connect but not create pipe instances, and only the
/// agent executable from the install folder is accepted as a client.
/// </summary>
internal sealed class AgentHost(string expectedAgentPath, Func<AgentConnection, PipeMessage, Task> onMessage, Action<string, object?> onTamper)
{
    private readonly ConcurrentDictionary<int, AgentConnection> connections = new();

    public IReadOnlyCollection<AgentConnection> Connections => [.. connections.Values];

    public async Task RunAsync(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = Create(first);
                first = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Someone else already owns the pipe name (should be impossible for a standard user before the service starts).
                Log.Error("Could not create the agent pipe", ex);
                if (first) onTamper("pipe_squatted", new { error = ex.Message });
                first = false;
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                continue;
            }
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch
            {
                await server.DisposeAsync();
                if (ct.IsCancellationRequested) break;
                continue;
            }
            _ = Task.Run(() => HandleAsync(server, ct), ct);
        }
    }

    private static NamedPipeServerStream Create(bool firstInstance)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // The account running the service (SYSTEM when installed) must be able to add pipe instances.
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(Paths.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 64 * 1024, 64 * 1024, security);
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var stream = new MessageStream(server);
        int pid = Native.PipeClientProcessId(server.SafePipeHandle) ?? 0;
        int? sessionId = pid > 0 ? Native.SessionOf(pid) : null;
        var path = pid > 0 ? Native.ProcessPath(pid) : null;
        if (sessionId is null || !string.Equals(path, expectedAgentPath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Rejected pipe client pid {pid} ({path ?? "unknown"}) in session {sessionId}");
            onTamper("unknown_pipe_client", new { path, session = sessionId });
            stream.Dispose();
            return;
        }

        var conn = new AgentConnection(pid, sessionId.Value, stream);
        connections[pid] = conn;
        Log.Info($"Agent connected: pid {pid}, session {sessionId}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await stream.ReadAsync(ct);
                if (msg is null) break;
                try
                {
                    await onMessage(conn, msg);
                }
                catch (Exception ex)
                {
                    Log.Error($"Agent message '{msg.Type}' failed", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            connections.TryRemove(pid, out _);
            stream.Dispose();
            Log.Info($"Agent disconnected: pid {pid}, session {sessionId}");
        }
    }
}
