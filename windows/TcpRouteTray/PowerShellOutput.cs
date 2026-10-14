using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TcpRouteTray;

internal static partial class PowerShellOutput
{
    public static string CleanError(string error)
    {
        var text = Marker().Replace(error, "").Trim();
        var xmlStart = text.IndexOf("<Objs", StringComparison.OrdinalIgnoreCase);
        if (xmlStart < 0) return MessageOrDefault(text);
        var plain = text[..xmlStart].Trim();
        if (plain.Length > 0) return plain;
        var xmlEnd = text.LastIndexOf("</Objs>", StringComparison.OrdinalIgnoreCase);
        if (xmlEnd < xmlStart) return MessageOrDefault("");
        try
        {
            using var reader = XmlReader.Create(new StringReader(text[xmlStart..(xmlEnd + 7)]),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var errors = document.Descendants().Where(node => node.Name.LocalName == "S" &&
                string.Equals((string?)node.Attribute("S"), "Error", StringComparison.OrdinalIgnoreCase))
                .Select(node => EscapedCharacter().Replace(node.Value, match =>
                    ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString()));
            return MessageOrDefault(string.Join("", errors).Trim());
        }
        catch (XmlException) { return MessageOrDefault(""); }
    }

    private static string MessageOrDefault(string message) => string.IsNullOrWhiteSpace(message)
        ? "PowerShell network configuration failed." : message;

    [GeneratedRegex(@"(?m)^\s*#<\s*CLIXML[^\r\n]*\r?\n?")]
    private static partial Regex Marker();

    [GeneratedRegex(@"_x([0-9a-fA-F]{4})_")]
    private static partial Regex EscapedCharacter();
}
