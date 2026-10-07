using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Monitor.Core.Pipe;

namespace Monitor.Agent;

/// <summary>
/// Every 5 seconds: the foreground app, its window title, the domain of the active Edge tab (from the
/// address bar, via UI Automation) and the apps playing sound. Runs on its own thread, because
/// UI Automation calls can take a moment.
/// </summary>
internal sealed partial class Sampler(Func<PipeMessage, Task> send)
{
    public const int IntervalMs = 5_000;
    private static readonly string[] Ignored = ["Idle", "LockApp", "LogonUI"];

    private readonly int sessionId = Process.GetCurrentProcess().SessionId;
    private readonly Dictionary<IntPtr, AutomationElement> addressBars = [];

    public void Run(CancellationToken ct)
    {
        long last = Environment.TickCount64;
        while (!ct.WaitHandle.WaitOne(IntervalMs))
        {
            long now = Environment.TickCount64;
            double seconds = Math.Min((now - last) / 1000.0, 2 * IntervalMs / 1000.0);
            last = now;
            try
            {
                send(new PipeMessage { Type = MessageTypes.Sample, Sample = Take(seconds) }).Wait(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Monitor.Core.Log.Warn($"Sampling failed: {ex.Message}");
            }
        }
    }

    private Sample Take(double seconds)
    {
        var sample = new Sample { Seconds = seconds, Locked = Native.IsInputDesktopLocked() };
        if (!sample.Locked) sample.Foreground = Foreground();
        foreach (var pid in Audio.ProcessesPlayingAudio())
        {
            if (Name(pid) is { } app) sample.Audio.Add(new AudioApp { Pid = pid, App = app });
        }
        return sample;
    }

    private ForegroundApp? Foreground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var name = Name(pid);
        if (name is null) return null;
        if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
            && Native.ChildWindowProcessOtherThan(hwnd, pid) is int appPid && Name(appPid) is { } appName)
        {
            pid = appPid;
            name = appName;
        }
        if (Ignored.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;

        var title = Native.WindowText(hwnd);
        string? site = null;
        if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase))
        {
            site = EdgeSite(hwnd);
            title = EdgeSuffix().Replace(title, "");
        }
        return new ForegroundApp { Pid = pid, App = name, Title = title, Site = site };
    }

    /// <summary>Process name of a process in this session, or null.</summary>
    private string? Name(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.SessionId == sessionId ? p.ProcessName : null;
        }
        catch
        {
            return null;
        }
    }

    private string? EdgeSite(IntPtr hwnd)
    {
        try
        {
            if (!addressBars.TryGetValue(hwnd, out var bar))
            {
                var root = AutomationElement.FromHandle(hwnd);
                var edit = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                bar = root.FindFirst(TreeScope.Descendants, new AndCondition(edit,
                          new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews")))
                      ?? root.FindFirst(TreeScope.Descendants, edit);
                if (bar is null) return null;
                if (addressBars.Count > 50) addressBars.Clear();
                addressBars[hwnd] = bar;
            }
            return bar.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                ? HostOf(((ValuePattern)pattern).Current.Value)
                : null;
        }
        catch
        {
            addressBars.Remove(hwnd);
            return null;
        }
    }

    /// <summary>"pbinfo.ro/probleme/1" → "pbinfo.ro"; text being typed (with spaces) or edge:// pages → null.</summary>
    internal static string? HostOf(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Contains(' ')) return null;
        if (!value.Contains("://")) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    [GeneratedRegex(@"\s+-\s+Microsoft\W*Edge\s*$")]
    private static partial Regex EdgeSuffix();
}
