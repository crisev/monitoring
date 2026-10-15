using System.Diagnostics;
using Monitor.Core;

namespace Monitor.Service;

/// <summary>Finds and closes processes that are not allowed in School mode, in the given user sessions.</summary>
internal static class Enforcer
{
    public sealed record Violation(int Pid, int SessionId, string Name, string? Path);

    public static List<Violation> FindViolations(ProcessRules rules, IReadOnlySet<int> sessionIds)
    {
        var found = new List<Violation>();
        if (sessionIds.Count == 0) return found;
        var tree = Native.ProcessTree();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (!sessionIds.Contains(p.SessionId) || p.Id == Environment.ProcessId) continue;
                    var name = p.ProcessName;
                    var path = Native.ProcessPath(p.Id);
                    if (rules.AllowReason(p.Id, name, path, tree) is null)
                        found.Add(new Violation(p.Id, p.SessionId, name, path));
                }
                catch (InvalidOperationException)
                {
                    // Exited meanwhile.
                }
            }
        }
        return found;
    }

    /// <summary>Kills one process (not its children: each child is judged on its own).</summary>
    public static bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: false);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not close pid {pid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Kills every process with this name in the given sessions (e.g. Edge after leaving Gaming mode).</summary>
    public static int KillByName(string name, IReadOnlySet<int> sessionIds)
    {
        int n = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (sessionIds.Contains(p.SessionId) && Kill(p.Id)) n++;
            }
        }
        return n;
    }

    public static IEnumerable<int> RunningInSession(string processName, int sessionId)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                if (p.SessionId == sessionId) yield return p.Id;
            }
        }
    }
}
