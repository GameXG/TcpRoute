using System.IO;

namespace TcpRouteTray;

internal static class SocksListenerProbe
{
    public static async Task VerifyAsync(Stream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var response = new byte[2];
        await stream.ReadExactlyAsync(response, token);
        if (response[0] != 5 || response[1] != 0)
            throw new InvalidOperationException("The local listener did not accept SOCKS5 authentication.");
        // UDP ASSOCIATE completes a local SOCKS request without dialing an upstream.
        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
        var reply = new byte[4];
        await stream.ReadExactlyAsync(reply, token);
        if (reply[0] != 5 || reply[1] != 0 || reply[2] != 0)
            throw new InvalidOperationException("The local listener did not complete the SOCKS5 readiness probe.");
        var length = reply[3] switch {
            1 => 4,
            4 => 16,
            3 => await ReadDomainLengthAsync(stream, token),
            _ => throw new InvalidDataException("The SOCKS5 readiness reply has an invalid address type.")
        };
        await stream.ReadExactlyAsync(new byte[length + 2], token);
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token);
        return length[0];
    }
}
