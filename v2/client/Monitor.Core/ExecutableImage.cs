using System.Collections.Concurrent;

namespace Monitor.Core;

/// <summary>
/// Reads the subsystem field from a Windows executable's PE header: whether it is a console program or a
/// windowed one. Used to tell the student's own programs (console programs built in Code::Blocks or VS Code)
/// apart from browsers and games that are started through an IDE. Results are cached per path, size and
/// modification time.
/// </summary>
public static class ExecutableImage
{
    public const ushort WindowsGui = 2;
    public const ushort WindowsConsole = 3;

    /// <summary>The PE header normally starts within the first few hundred bytes; anything far beyond is not a real header.</summary>
    private const int MaxPeOffset = 64 * 1024;

    private static readonly ConcurrentDictionary<string, ushort?> cache = new(StringComparer.Ordinal);

    /// <summary>True if the file is a Windows console program. False if it is windowed, unreadable or not an executable.</summary>
    public static bool IsConsoleProgram(string path) => ReadSubsystem(path) == WindowsConsole;

    public static ushort? ReadSubsystem(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var key = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{path.ToUpperInvariant()}";
            if (cache.TryGetValue(key, out var known)) return known;
            if (cache.Count > 5000) cache.Clear();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var subsystem = ReadSubsystem(stream);
            cache[key] = subsystem;
            return subsystem;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The subsystem value, or null if the stream does not hold a valid PE32/PE32+ header.</summary>
    public static ushort? ReadSubsystem(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return null; // "MZ"
            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            // Signature (4) + COFF header (20) + optional header up to and including Subsystem (68 + 2).
            if (peOffset < 0x40 || peOffset > MaxPeOffset || peOffset + 4 + 20 + 70 > stream.Length) return null;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
            stream.Position = peOffset + 4 + 20;
            ushort magic = reader.ReadUInt16();
            if (magic != 0x10B && magic != 0x20B) return null; // PE32 or PE32+
            stream.Position = peOffset + 4 + 20 + 68; // Subsystem: same offset in PE32 and PE32+
            return reader.ReadUInt16();
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
