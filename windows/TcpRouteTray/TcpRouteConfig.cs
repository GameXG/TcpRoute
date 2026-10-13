using System;
using System.Text.RegularExpressions;

namespace TcpRouteTray;

internal sealed record TcpRouteConfig(string Id, string Name, string Path, string Content)
{
    public TcpRouteRuntimeConfig ToRuntime(bool allowLan)
    {
        var listen = TcpRouteConfigParser.GetListenAddress(Content);
        var host = allowLan ? "0.0.0.0" : "127.0.0.1";
        var runtimeContent = TcpRouteConfigParser.ReplaceListenAddress(Content, host, listen.Port);
        return new TcpRouteRuntimeConfig(Name, runtimeContent, listen.Port);
    }
}

internal readonly record struct TcpRouteRuntimeConfig(string SourceName, string Content, int Port);

internal readonly record struct ListenAddress(string Host, int Port);

internal static partial class TcpRouteConfigParser
{
    private static readonly Regex AddrRegex = new(
        @"(?m)^(?<indent>\s*)addr\s*=\s*(?<quote>[""'])(?<value>[^""']+)\k<quote>",
        RegexOptions.Compiled);

    public static ListenAddress GetListenAddress(string content)
    {
        var match = AddrRegex.Match(StripLineComments(content));
        if (!match.Success)
        {
            throw new InvalidOperationException("TcpRoute2 config must contain an addr value.");
        }

        return ParseAddress(match.Groups["value"].Value);
    }

    public static string ReplaceListenAddress(string content, string host, int port)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var uncommentedLine = StripLineComment(lines[index]);
            var match = AddrRegex.Match(uncommentedLine);
            if (!match.Success)
            {
                continue;
            }

            lines[index] = $"{match.Groups["indent"].Value}addr=\"{host}:{port}\"";
            return string.Join(newline, lines);
        }

            throw new InvalidOperationException("TcpRoute2 config must contain an addr value.");
    }

    private static ListenAddress ParseAddress(string address)
    {
        if (address.StartsWith("[", StringComparison.Ordinal))
        {
            var end = address.IndexOf(']');
            if (end < 0 || end + 2 > address.Length || address[end + 1] != ':')
            {
                throw new InvalidOperationException($"Invalid TcpRoute2 listen address: {address}");
            }

            return new ListenAddress(address[1..end], ParsePort(address[(end + 2)..], address));
        }

        var colon = address.LastIndexOf(':');
        if (colon < 0)
        {
            return new ListenAddress("", ParsePort(address, address));
        }

        return new ListenAddress(address[..colon], ParsePort(address[(colon + 1)..], address));
    }

    private static int ParsePort(string rawPort, string originalAddress)
    {
        if (!int.TryParse(rawPort, out var port) || port < 1 || port > 65535)
        {
            throw new InvalidOperationException($"Invalid TcpRoute2 listen address: {originalAddress}");
        }

        return port;
    }

    private static string StripLineComments(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = StripLineComment(lines[i]);
        }

        return string.Join('\n', lines);
    }

    private static string StripLineComment(string line)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaped = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && !escaped)
            {
                escaped = true;
                continue;
            }

            if (character == '"' && !inSingleQuote && !escaped)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == '\'' && !inDoubleQuote && !escaped)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '#' && !inSingleQuote && !inDoubleQuote)
            {
                return line[..index];
            }

            escaped = false;
        }

        return line;
    }
}
