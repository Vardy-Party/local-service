using System.Threading.Channels;

namespace VardyParty.LocalService.Compute;

/// <summary>
/// One CONNECT stream between the host pop-proxy and the phone.
/// </summary>
internal sealed class RelayLink
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly Func<byte[], Task> _send;
    private readonly Func<Task> _close;
    private int _closed;

    public RelayLink(int id, Func<byte[], Task> send, Func<Task> close)
    {
        Id = id;
        _send = send;
        _close = close;
        Stream = new LinkStream(this);
    }

    public int Id { get; }
    public TaskCompletionSource<bool> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Stream Stream { get; }

    public void SignalOpen(bool opened) => Opened.TrySetResult(opened);

    public void Push(byte[] payload) => _incoming.Writer.TryWrite(payload);

    public void Complete() => _incoming.Writer.TryComplete();

    internal bool IncomingCompleted => _incoming.Reader.Completion.IsCompleted;

    internal static RelayLink CreateTest(int id) =>
        new(id, _ => Task.CompletedTask, () => Task.CompletedTask);

    private sealed class LinkStream(RelayLink link) : Stream
    {
        private byte[]? _pending;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_pending is null || _offset >= _pending.Length)
            {
                if (!await link._incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (!link._incoming.Reader.TryRead(out _pending))
                {
                    continue;
                }

                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = buffer;
            while (remaining.Length > 0)
            {
                var take = Math.Min(remaining.Length, RelayFrames.MaxPayload);
                await link._send(remaining[..take].ToArray()).ConfigureAwait(false);
                remaining = remaining[take..];
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref link._closed, 1) == 0)
            {
                link.Complete();
                _ = link._close();
            }

            base.Dispose(disposing);
        }
    }
}
