namespace Monitor.Core;

/// <summary>Daily log files (prefix-yyyyMMdd.log), kept for 14 days, also echoed to the console.</summary>
public static class Log
{
    private static readonly object gate = new();
    private static string? dir;
    private static string prefix = "monitor";

    public static void Init(string directory, string filePrefix)
    {
        lock (gate)
        {
            dir = directory;
            prefix = filePrefix;
            try
            {
                Directory.CreateDirectory(directory);
                foreach (var f in Directory.GetFiles(directory, $"{filePrefix}-*.log"))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
            }
            catch
            {
                // Logging must never stop the program.
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var now = DateTime.Now; // display only; never used for decisions
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (gate)
        {
            try { Console.WriteLine(line); } catch { }
            if (dir is null) return;
            try { File.AppendAllText(Path.Combine(dir, $"{prefix}-{now:yyyyMMdd}.log"), line + Environment.NewLine); } catch { }
        }
    }
}

public static class Format
{
    /// <summary>3725 → "1h 02m", 125 → "2m", 42 → "42s".</summary>
    public static string Duration(int seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds >= 3600) return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
        if (seconds >= 60) return $"{seconds / 60}m";
        return $"{seconds}s";
    }

    /// <summary>Like <see cref="Duration"/> but with seconds under an hour: 125 → "2m 05s".</summary>
    public static string Countdown(int seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds >= 3600) return Duration(seconds);
        return seconds >= 60 ? $"{seconds / 60}m {seconds % 60:00}s" : $"{seconds}s";
    }

    public static string Version =>
        typeof(Format).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
