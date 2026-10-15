using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Monitor.Core.Api;

public sealed class ApiException(HttpStatusCode status, string body)
    : Exception($"Server returned {(int)status} {status}: {Truncate(body)}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

public enum StartOutcome { Started, NoBalance }

/// <summary>Client for /api/device/*. Every call except enroll sends the device token.</summary>
public sealed class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http;

    public ApiClient(string serverUrl, string? token, string userAgent, TimeSpan? timeout = null)
    {
        http = new HttpClient
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<EnrollResponse> EnrollAsync(string code, CancellationToken ct = default) =>
        await PostJsonAsync<EnrollResponse>("api/device/enroll", new { code }, ct);

    public async Task<DeviceState> HeartbeatAsync(HeartbeatRequest body, CancellationToken ct = default) =>
        await PostJsonAsync<DeviceState>("api/device/heartbeat", body, ct);

    public async Task<(StartOutcome Outcome, DeviceState State)> StartGameAsync(CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync("api/device/game/start", new { }, Json, ct);
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var err = await res.Content.ReadFromJsonAsync<StartRefused>(Json, ct);
            if (err?.State is not null) return (StartOutcome.NoBalance, err.State);
        }
        return (StartOutcome.Started, await ReadAsync<DeviceState>(res, ct));
    }

    public async Task<DeviceState> StopGameAsync(string? sessionId, CancellationToken ct = default) =>
        await PostJsonAsync<DeviceState>("api/device/game/stop", new { sessionId }, ct);

    public async Task<DeviceConfig> GetConfigAsync(CancellationToken ct = default)
    {
        using var res = await http.GetAsync("api/device/config", ct);
        return await ReadAsync<DeviceConfig>(res, ct);
    }

    public async Task PostActivityAsync(IReadOnlyList<ActivityItem> items, CancellationToken ct = default) =>
        await PostJsonAsync<JsonElement>("api/device/activity", new { items }, ct);

    public async Task PostEventsAsync(IReadOnlyList<DeviceEvent> events, CancellationToken ct = default) =>
        await PostJsonAsync<JsonElement>("api/device/events", new { events }, ct);

    public async Task PostScreenshotAsync(byte[] jpeg, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var res = await http.PostAsync("api/device/screenshot", content, ct);
        await ReadAsync<JsonElement>(res, ct);
    }

    private async Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync(path, body, Json, ct);
        return await ReadAsync<T>(res, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
            throw new ApiException(res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public void Dispose() => http.Dispose();

    private sealed class StartRefused
    {
        public string? Error { get; set; }
        public DeviceState? State { get; set; }
    }
}
