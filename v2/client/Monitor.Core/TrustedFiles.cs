using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Monitor.Core;

/// <summary>Checks who owns an executable file. Results are cached per path, size and modification time.</summary>
public static class TrustedFiles
{
    private static readonly SecurityIdentifier[] SystemOwners =
    [
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Modified), bool> cache = new(StringTupleComparer.Instance);

    /// <summary>True if the file is owned by TrustedInstaller, SYSTEM or Administrators.</summary>
    public static bool IsSystemOwned(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            var key = (path, info.Length, info.LastWriteTimeUtc);
            if (cache.TryGetValue(key, out var known)) return known;
            if (cache.Count > 5000) cache.Clear();
            var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            bool trusted = owner is not null && SystemOwners.Contains(owner);
            cache[key] = trusted;
            return trusted;
        }
        catch
        {
            return false;
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Path, long Size, DateTime Modified)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Path, long Size, DateTime Modified) a, (string Path, long Size, DateTime Modified) b) =>
            a.Size == b.Size && a.Modified == b.Modified && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, long Size, DateTime Modified) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), k.Size, k.Modified);
    }
}
