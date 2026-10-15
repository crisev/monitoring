using Monitor.Core;

namespace Monitor.Core.Tests;

public class ExecutableImageTests
{
    /// <summary>A minimal PE header: "MZ", e_lfanew, "PE\0\0", COFF header, optional-header magic and subsystem.</summary>
    private static byte[] Pe(ushort subsystem, ushort magic = 0x20B, int peOffset = 0x80, int size = 0x200)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3C);
        if (peOffset + 4 + 20 + 70 <= size)
        {
            "PE\0\0"u8.ToArray().CopyTo(bytes, peOffset);
            BitConverter.GetBytes(magic).CopyTo(bytes, peOffset + 24);
            BitConverter.GetBytes(subsystem).CopyTo(bytes, peOffset + 24 + 68);
        }
        return bytes;
    }

    private static ushort? Read(byte[] bytes) => ExecutableImage.ReadSubsystem(new MemoryStream(bytes));

    [Theory]
    [InlineData((ushort)0x10B)] // PE32 (32-bit)
    [InlineData((ushort)0x20B)] // PE32+ (64-bit)
    public void Reads_console_and_windowed_subsystems(ushort magic)
    {
        Assert.Equal(ExecutableImage.WindowsConsole, Read(Pe(ExecutableImage.WindowsConsole, magic)));
        Assert.Equal(ExecutableImage.WindowsGui, Read(Pe(ExecutableImage.WindowsGui, magic)));
    }

    [Fact]
    public void Rejects_files_that_are_not_executables()
    {
        Assert.Null(Read("hello, this is a text file that is long enough to have a header....."u8.ToArray()));
        Assert.Null(Read(new byte[10]));
        var badSignature = Pe(ExecutableImage.WindowsConsole);
        badSignature[0x80] = (byte)'X';
        Assert.Null(Read(badSignature));
        Assert.Null(Read(Pe(ExecutableImage.WindowsConsole, magic: 0x1234)));
        Assert.Null(Read(Pe(ExecutableImage.WindowsConsole, peOffset: 0x1F0))); // header cut off
        Assert.Null(Read(Pe(ExecutableImage.WindowsConsole, peOffset: 1_000_000)));
    }

    [Fact]
    public void Checks_files_on_disk_and_notices_when_they_change()
    {
        var path = Path.Combine(Path.GetTempPath(), $"monitor-test-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, Pe(ExecutableImage.WindowsConsole));
            Assert.True(ExecutableImage.IsConsoleProgram(path));
            File.WriteAllBytes(path, Pe(ExecutableImage.WindowsGui, size: 0x400)); // rebuilt: different size
            Assert.False(ExecutableImage.IsConsoleProgram(path));
            Assert.False(ExecutableImage.IsConsoleProgram(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
