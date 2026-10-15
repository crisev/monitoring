using System.Net;
using System.Security.Cryptography;
using Monitor.Core;

namespace Monitor.Core.Tests;

public class UpdatesTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mv2-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7))];
    private static readonly string PayloadSha = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    public UpdatesTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static UpdateOffer Offer(string? sha = null, long? size = null, string url = "https://github.com/x/y/MonitorV2.zip") => new()
    {
        Id = "0.1.12@1",
        Version = "0.1.12",
        Url = url,
        Sha256 = sha ?? PayloadSha,
        Size = size ?? Payload.Length,
    };

    private sealed class FakeHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }

    [Theory]
    [InlineData("0.1.12", "0.1.5", true)]
    [InlineData("0.1.5", "0.1.12", false)]
    [InlineData("0.1.12", "0.1.12+abcdef", false)]
    [InlineData("0.2.0", "0.1.99", true)]
    [InlineData("0.1.12", "", true)]
    [InlineData("nonsense", "0.1.0", false)]
    public void Compares_versions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Updates.IsNewer(candidate, current));

    [Fact]
    public async Task Downloads_and_verifies()
    {
        var dest = Path.Combine(dir, "pkg.zip");
        using var http = new HttpClient(new FakeHandler(Payload));
        await Updates.DownloadAsync(http, Offer(), dest, CancellationToken.None);
        Assert.True(Updates.Matches(dest, PayloadSha.ToUpperInvariant(), Payload.Length));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Refuses_a_file_with_another_hash_and_leaves_nothing_behind()
    {
        var dest = Path.Combine(dir, "pkg.zip");
        var tampered = (byte[])Payload.Clone();
        tampered[1000] ^= 0xFF;
        using var http = new HttpClient(new FakeHandler(tampered));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Updates.DownloadAsync(http, Offer(), dest, CancellationToken.None));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Refuses_a_file_of_another_size()
    {
        using var http = new HttpClient(new FakeHandler(Payload));
        await Assert.ThrowsAsync<UpdateException>(() =>
            Updates.DownloadAsync(http, Offer(size: Payload.Length - 1), Path.Combine(dir, "a.zip"), CancellationToken.None));
        await Assert.ThrowsAsync<UpdateException>(() =>
            Updates.DownloadAsync(http, Offer(size: Payload.Length + 1), Path.Combine(dir, "b.zip"), CancellationToken.None));
    }

    [Theory]
    [InlineData("http://github.com/x.zip")]
    [InlineData("file:///C:/x.zip")]
    public void Refuses_non_https_addresses(string url) =>
        Assert.Throws<UpdateException>(() => Updates.Validate(Offer(url: url)));

    [Fact]
    public void Refuses_a_bad_hash_or_size()
    {
        Assert.Throws<UpdateException>(() => Updates.Validate(Offer(sha: "abc")));
        Assert.Throws<UpdateException>(() => Updates.Validate(Offer(size: 0)));
        Assert.Throws<UpdateException>(() => Updates.Validate(Offer(size: Updates.MaxPackageBytes + 1)));
    }

    [Fact]
    public void Finds_the_app_folder_in_a_package()
    {
        Assert.Null(Updates.FindAppDir(dir));
        var app = Path.Combine(dir, "MonitorV2", "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, Paths.ServiceExe), "x");
        Assert.Equal(app, Updates.FindAppDir(dir));
    }
}
