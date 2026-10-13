using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TcpRouteTray;

internal static class ArtifactSelfTest
{
    public static void Run(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = metadata.TypeDefinitions.Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name)).ToHashSet();
        if (!types.Contains("LogWindow") || !types.Contains("TrayIconHost") || types.Contains("MainWindow") || types.Contains("AdvancedTomlWindow") || types.Contains("ConfigEditorForm"))
            throw new InvalidOperationException("Published client does not satisfy the tray-only window contract.");
        Console.WriteLine("Tray-only assembly verified: " + path);
    }
}
