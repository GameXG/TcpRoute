using System.Runtime.InteropServices;
using System.IO;

namespace TcpRouteTray;

internal static class WintunDependenciesSelfTest
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TcpRoute2-Wintun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var (machine, architecture) in new[] { (0x8664, Architecture.X64), (0xAA64, Architecture.Arm64) })
            {
                var path = Path.Combine(directory, "wintun.dll");
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    writer.Write((ushort)0x5A4D); writer.BaseStream.Position = 0x3C; writer.Write(0x40);
                    writer.BaseStream.Position = 0x40; writer.Write(0x00004550); writer.Write((ushort)machine);
                }
                WintunDependencies.Validate(path, architecture);
                try { WintunDependencies.Validate(path, architecture == Architecture.X64 ? Architecture.Arm64 : Architecture.X64); throw new Exception("Mismatched DLL was accepted."); }
                catch (InvalidDataException) { }
            }
            File.WriteAllText(Path.Combine(directory, "wintun.dll"), "invalid");
            try { WintunDependencies.Validate(Path.Combine(directory, "wintun.dll"), Architecture.X64); throw new Exception("Malformed DLL was accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
