using System.Runtime.InteropServices;
using System.IO;

namespace TcpRouteTray;

internal static class WintunDependencies
{
    public static void Validate(string path, Architecture architecture)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Place the architecture-matched wintun.dll beside TcpRoute2.exe.", path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Invalid Wintun DLL.");
        stream.Position = 0x3C;
        var header = reader.ReadInt32();
        if (header < 64 || header > stream.Length - 6) throw new InvalidDataException("Invalid Wintun PE header.");
        stream.Position = header;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Invalid Wintun PE signature.");
        var expected = architecture switch { Architecture.X64 => 0x8664, Architecture.Arm64 => 0xAA64, _ => throw new PlatformNotSupportedException("Only Windows x64 and ARM64 are supported.") };
        if (reader.ReadUInt16() != expected) throw new InvalidDataException("Wintun DLL architecture does not match this client.");
    }
}
