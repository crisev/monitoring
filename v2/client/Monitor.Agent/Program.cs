using Monitor.Core;

namespace Monitor.Agent;

internal static class Program
{
    /// <summary>Set by a second start of the agent: the running one shows its status window.</summary>
    public const string ShowEventName = @"Local\MonitorV2.Agent.Show";

    /// <summary>
    /// Started by the service in each child's session (and restarted if it is closed).
    /// One instance per session; starting it again shows the status window.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\MonitorV2.Agent", out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) using (show) show.Set();
            return;
        }
        Log.Init(Paths.AgentLogDir, "agent");
        Log.Info($"Agent {Format.Version} started for {Environment.UserName}.");
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
