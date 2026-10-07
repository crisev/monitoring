using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Monitor.Core.Api;

namespace Monitor.Core;

public static class Paths
{
    public const string ServiceName = "MonitorV2";
    public const string PipeName = "MonitorV2";
    public const string AgentExe = "Monitor.Agent.exe";
    public const string ServiceExe = "Monitor.Service.exe";

    /// <summary>MONITORV2_DATA: a different data folder, for development only (not locked down).</summary>
    public static string? DataDirOverride { get; } =
        Environment.GetEnvironmentVariable("MONITORV2_DATA") is { Length: > 0 } dir ? dir : null;

    /// <summary>C:\ProgramData\MonitorV2: SYSTEM and Administrators only.</summary>
    public static string DataDir { get; } =
        DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MonitorV2");

    public static string DeviceFile => Path.Combine(DataDir, "device.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>%LOCALAPPDATA%\MonitorV2 of the user running the agent.</summary>
    public static string AgentLogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorV2");

    /// <summary>The folder this program runs from (C:\Program Files\MonitorV2 once installed).</summary>
    public static string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
}

/// <summary>Who this PC is (device.json). Written once, at enrollment.</summary>
public sealed class DeviceIdentity
{
    public string ServerUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

/// <summary>What the service needs to carry on after a restart or without network (state.json).</summary>
public sealed class SavedState
{
    public ClockSnapshot Clock { get; set; } = new();
    public DeviceConfig? Config { get; set; }
}

public static class Store
{
    private static readonly JsonSerializerOptions Options = new(ApiClient.Json) { WriteIndented = true };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes to a temporary file first, so a crash never leaves a half-written file.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Creates the data folder and limits it to SYSTEM and Administrators (no inherited Users access).</summary>
    public static void SecureDataDir()
    {
        var dir = new DirectoryInfo(Paths.DataDir);
        if (!dir.Exists) dir.Create();
        if (Paths.DataDirOverride is not null) return;
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        dir.SetAccessControl(acl);
    }
}
