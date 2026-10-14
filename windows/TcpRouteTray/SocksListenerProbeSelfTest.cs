using System.IO;

namespace TcpRouteTray;

internal static class SocksListenerProbeSelfTest
{
    public static void Run()
    {
        using var stream = new ProbeStream(new byte[] { 5, 0, 5, 0, 0, 1, 127, 0, 0, 1, 0, 123 });
        SocksListenerProbe.VerifyAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        var expected = new byte[] { 5, 1, 0, 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 };
        if (!stream.Written.SequenceEqual(expected)) throw new Exception("Readiness probe did not complete a local SOCKS5 request.");
        using var rejected = new ProbeStream(new byte[] { 5, 255 });
        try { SocksListenerProbe.VerifyAsync(rejected, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Rejected authentication was accepted."); }
        catch (InvalidOperationException) { }
        using var truncated = new ProbeStream(new byte[] { 5 });
        try { SocksListenerProbe.VerifyAsync(truncated, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Truncated reply was accepted."); }
        catch (EndOfStreamException) { }
    }

    private sealed class ProbeStream(byte[] replies) : Stream
    {
        private readonly MemoryStream _replies = new(replies);
        private readonly MemoryStream _requests = new();
        public byte[] Written => _requests.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _replies.Read(buffer, offset, Math.Min(1, count));
        public override void Write(byte[] buffer, int offset, int count) => _requests.Write(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _replies.Dispose(); _requests.Dispose(); } base.Dispose(disposing); }
    }
}
