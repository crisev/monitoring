using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monitor.Core;
using Monitor.Core.Api;
using Monitor.Service;

// Monitor.Service.exe                      run (as the Windows service, or in a console for development)
// Monitor.Service.exe --dry-run            same, but only log what would be closed or shut down
// Monitor.Service.exe --monitor-admins     testing: treat administrator accounts like the child's
// Monitor.Service.exe enroll <url> <code>  enroll this PC with a one-time code from the web app (as administrator)
// Monitor.Service.exe clear-policies       remove the Edge policies (used by uninstall.ps1)
// Monitor.Service.exe status               show enrollment and saved state

var command = args.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant();
bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
bool monitorAdmins = args.Contains("--monitor-admins", StringComparer.OrdinalIgnoreCase);

switch (command)
{
    case "enroll":
        return await Enroll(args.Where(a => !a.StartsWith("--")).Skip(1).ToArray());
    case "clear-policies":
        EdgePolicies.ClearAll();
        Console.WriteLine("Edge policies removed.");
        return 0;
    case "status":
        return Status();
    case null:
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use: enroll <url> <code> | clear-policies | status");
        return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.ClearProviders(); // Monitor.Core.Log writes our own log files
builder.Services.AddWindowsService(o => o.ServiceName = Paths.ServiceName);
builder.Services.AddSingleton(new ServiceOptions(dryRun, monitorAdmins));
builder.Services.AddHostedService<MonitorWorker>();
try
{
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Error("Service crashed", ex);
    return 1; // non-zero, so the service recovery settings restart it
}

static async Task<int> Enroll(string[] rest)
{
    if (rest.Length != 2)
    {
        Console.Error.WriteLine("Usage: Monitor.Service.exe enroll <server-url> <code>");
        return 2;
    }
    var (url, code) = (rest[0].TrimEnd('/'), rest[1]);
    try
    {
        Store.SecureDataDir();
        using var api = new ApiClient(url, null, $"MonitorV2/{Format.Version}");
        var res = await api.EnrollAsync(code);
        Store.Save(Paths.DeviceFile, new DeviceIdentity
        {
            ServerUrl = url,
            Token = res.Token,
            DeviceId = res.DeviceId,
            DeviceName = res.DeviceName,
        });
        Console.WriteLine($"Enrolled as \"{res.DeviceName}\" ({res.DeviceId}). Saved to {Paths.DeviceFile}.");
        return 0;
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Run this as administrator.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Enrollment failed: {ex.Message}");
        return 1;
    }
}

static int Status()
{
    var id = Store.Load<DeviceIdentity>(Paths.DeviceFile);
    Console.WriteLine(id is null
        ? "Not enrolled."
        : $"Device \"{id.DeviceName}\" ({id.DeviceId}), server {id.ServerUrl}, token {(id.Token.Length > 0 ? "present" : "missing")}.");
    var state = Store.Load<SavedState>(Paths.StateFile);
    if (state is not null)
    {
        var c = state.Clock;
        Console.WriteLine($"Screen time left: {(c.ServerScreenRemainingSeconds is int r ? Format.Duration(Math.Max(0, r - (int)c.PendingScreenSeconds)) : "no limit")}; " +
            $"not yet reported: {Format.Duration((int)c.PendingScreenSeconds)}; offline used: {Format.Duration((int)c.OfflineUsedSeconds)} of {Format.Duration(c.OfflineBudgetSeconds)}; " +
            $"game balance: {Format.Duration(c.GameBalanceSeconds)}; config v{state.Config?.Version}.");
    }
    return 0;
}
