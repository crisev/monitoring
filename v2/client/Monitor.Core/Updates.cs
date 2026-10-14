using System.Security.Cryptography;

namespace Monitor.Core;

/// <summary>A newer client version a parent asked this PC to install (part of the device state).</summary>
public sealed class UpdateOffer
{
    /// <summary>Changes each time a parent clicks Update, so a version that failed can be tried again.</summary>
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>Written by the old service just before it starts update.ps1; read by whichever version starts next.</summary>
public sealed class UpdatePending
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string OfferId { get; set; } = "";
}

/// <summary>Written by update.ps1 when it is done (or rolled back).</summary>
public sealed class UpdateResult
{
    public string To { get; set; } = "";
    public bool Ok { get; set; }
    public string? Message { get; set; }
}

public sealed class UpdateException(string message) : Exception(message);

/// <summary>Download, check and unpack an update package. The installing itself is done by update.ps1.</summary>
public static class Updates
{
    /// <summary>Largest package accepted (the self-contained zip is well under this).</summary>
    public const long MaxPackageBytes = 500L * 1024 * 1024;

    public static string Dir => Path.Combine(Paths.DataDir, "updates");
    public static string PendingFile => Path.Combine(Paths.DataDir, "update-pending.json");
    /// <summary>The new service writes its version here once it has reached the server; update.ps1 waits for it.</summary>
    public static string OkFile => Path.Combine(Paths.DataDir, "update-ok.txt");
    public static string ResultFile => Path.Combine(Paths.DataDir, "update-result.json");
    public const string Script = "update.ps1";

    /// <summary>True if <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var c) && (!TryParse(current, out var cur) || c > cur);

    private static bool TryParse(string? v, out Version version) =>
        System.Version.TryParse((v ?? "").Split('+')[0].Trim(), out version!);

    /// <summary>Checks an offer before anything is downloaded.</summary>
    public static void Validate(UpdateOffer offer)
    {
        if (!TryParse(offer.Version, out _)) throw new UpdateException($"bad version '{offer.Version}'");
        if (!Uri.TryCreate(offer.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new UpdateException("the download address is not https");
        if (offer.Sha256.Length != 64 || !offer.Sha256.All(Uri.IsHexDigit)) throw new UpdateException("bad SHA-256");
        if (offer.Size <= 0 || offer.Size > MaxPackageBytes) throw new UpdateException($"bad size {offer.Size}");
    }

    /// <summary>True if the file exists with exactly this size and SHA-256.</summary>
    public static bool Matches(string path, string sha256, long size)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size) return false;
        using var stream = info.OpenRead();
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads to <paramref name="destination"/>, hashing as it goes. The file only gets its final name once
    /// the size and SHA-256 match; otherwise it is deleted and an <see cref="UpdateException"/> is thrown.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, UpdateOffer offer, string destination, CancellationToken ct)
    {
        Validate(offer);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            using (var res = await http.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!res.IsSuccessStatusCode) throw new UpdateException($"download failed ({(int)res.StatusCode})");
                await using var input = await res.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(partial);
                var buffer = new byte[81920];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > offer.Size) throw new UpdateException("the download is larger than announced");
                    hash.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            if (total != offer.Size) throw new UpdateException($"the download is {total} bytes, expected {offer.Size}");
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("the download's SHA-256 does not match: damaged or replaced file");
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>The folder with Monitor.Service.exe inside an unpacked package (…\MonitorV2\app), or null.</summary>
    public static string? FindAppDir(string root)
    {
        foreach (var candidate in new[] { Path.Combine(root, "MonitorV2", "app"), Path.Combine(root, "app") })
            if (File.Exists(Path.Combine(candidate, Paths.ServiceExe))) return candidate;
        return null;
    }
}
