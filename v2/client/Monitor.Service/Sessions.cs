using System.Security.Principal;
using Monitor.Core;

namespace Monitor.Service;

/// <summary>A Windows session with a logged-on user.</summary>
/// <param name="Monitored">A child's session: monitored and enforced. Administrators are left alone.</param>
internal sealed record UserSession(int Id, string User, Native.WtsState State, bool Locked, bool IsAdmin, string? Sid, bool Monitored)
{
    /// <summary>The user is at this session's desktop right now (connected and unlocked).</summary>
    public bool InUse => State == Native.WtsState.Active && !Locked;
}

/// <summary>Lists logged-on users from the service (SYSTEM), using session state, not anything the agent reports.</summary>
/// <param name="monitorAdmins">Testing only: treat administrators like the child.</param>
internal sealed class SessionMonitor(bool monitorAdmins)
{
    private readonly Dictionary<(int Id, string User), (bool IsAdmin, string? Sid)> cache = [];

    public IReadOnlyList<UserSession> Refresh()
    {
        var list = new List<UserSession>();
        var seen = new HashSet<(int, string)>();
        foreach (var (id, state) in Native.EnumerateSessions())
        {
            if (id == 0) continue; // services
            if (state is not (Native.WtsState.Active or Native.WtsState.Connected or Native.WtsState.Disconnected)) continue;
            var user = Native.QuerySessionString(id, domain: false);
            if (user.Length == 0) continue; // logon screen
            var domain = Native.QuerySessionString(id, domain: true);
            var name = domain.Length > 0 ? $"{domain}\\{user}" : user;
            var key = (id, name);
            seen.Add(key);
            if (!cache.TryGetValue(key, out var info))
            {
                info = Inspect(id);
                cache[key] = info;
                Log.Info($"Session {id}: {name} ({(info.IsAdmin ? "administrator, not monitored" : "standard user, monitored")})");
            }
            list.Add(new UserSession(id, name, state, Native.IsSessionLocked(id), info.IsAdmin, info.Sid, !info.IsAdmin || monitorAdmins));
        }
        foreach (var stale in cache.Keys.Where(k => !seen.Contains(k)).ToList()) cache.Remove(stale);
        return list;
    }

    /// <summary>
    /// Administrator check, as in the original app: with UAC the user's token is the filtered one, where the
    /// Administrators group is present but deny-only, so group claims (including deny-only) are checked.
    /// </summary>
    private static (bool IsAdmin, string? Sid) Inspect(int sessionId)
    {
        try
        {
            using var token = Native.QueryUserToken(sessionId);
            if (token is null) return (false, null);
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var sid = identity.User?.Value;
            int elevation = Native.GetElevationType(token);
            bool admin = elevation is Native.TokenElevationTypeFull or Native.TokenElevationTypeLimited
                || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                || identity.Claims.Any(c => IsAdminSid(c.Value));
            return (admin, sid);
        }
        catch (Exception ex)
        {
            // When in doubt, monitor.
            Log.Error($"Could not inspect session {sessionId}", ex);
            return (false, null);
        }
    }

    private static bool IsAdminSid(string sid) =>
        sid == "S-1-5-32-544"                     // BUILTIN\Administrators
        || (sid.StartsWith("S-1-5-21-") && (sid.EndsWith("-512") || sid.EndsWith("-519"))); // Domain/Enterprise Admins
}
