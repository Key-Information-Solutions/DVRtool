using System.Runtime.InteropServices;

namespace DVRTool.Vendors.HikvisionSdk;

/// <summary>
/// The hand-off between HCNetSDK's data callback and whatever is demuxing the video: a
/// bounded, forward-only byte stream written by a native thread and read by a managed one.
/// </summary>
/// <remarks>
/// <para>
/// The contract that shapes everything here is that <see cref="Append"/> runs on the SDK's
/// own callback thread and <b>must not block</b>. That thread feeds the TCP session the
/// login is sharing, so stalling it does not merely delay this stream — it stalls the
/// connection. So there is no back-pressure: when the reader falls behind, bytes are
/// dropped rather than waited on.
/// </para>
/// <para>
/// Dropping takes the <em>oldest</em> chunks, not the newest. For live video the reader is
/// only ever interested in now, and discarding the front of the queue keeps latency bounded
/// at roughly <see cref="Capacity"/>; discarding the newest would leave a viewer watching a
/// delay that only grows. Either way the demuxer sees a gap and resynchronises at the next
/// keyframe, so the choice is about latency, not about corruption.
/// </para>
/// <para>
/// Non-seekable on purpose: <c>Length</c> and <c>Seek</c> throw, which is what tells
/// LibVLC's <c>StreamMediaInput</c> — and ffmpeg, and anything else well-behaved — that
/// this is a live source of unknown length rather than a file it may index.
/// </para>
/// </remarks>
public sealed class SdkMediaStream : Stream
{
    private readonly Queue<byte[]> _chunks = new();
    private readonly object _gate = new();

    private int _buffered;
    private byte[]? _partial;
    private int _partialOffset;
    private bool _completed;
    private Exception? _fault;

    /// <param name="capacityBytes">
    /// How much undelivered video to hold. 8 MB is about eight seconds of a 4 MP main
    /// stream — long enough to absorb a demuxer hiccup, short enough that a reader which has
    /// genuinely stopped does not leave a minute of stale video to catch up through.
    /// </param>
    /// <param name="stallTimeout">
    /// How long a read waits with nothing arriving before it reports end-of-stream. This is
    /// what turns "the device accepted the request and then sent nothing" — a wrong channel,
    /// a camera that is offline — into a finite failure rather than a hang.
    /// </param>
    public SdkMediaStream(int capacityBytes = 8 * 1024 * 1024, TimeSpan? stallTimeout = null)
    {
        Capacity = capacityBytes;
        StallTimeout = stallTimeout ?? TimeSpan.FromSeconds(15);
    }

    public int Capacity { get; }

    public TimeSpan StallTimeout { get; }

    /// <summary>Total media bytes handed over by the SDK.</summary>
    public long BytesReceived { get; private set; }

    /// <summary>
    /// Bytes discarded because the reader fell behind. Non-zero means the viewer saw
    /// artefacts, and is worth reporting rather than hiding.
    /// </summary>
    public long BytesDropped { get; private set; }

    /// <summary>True once any media byte has arrived — the real "is this working" signal.</summary>
    public bool HasData => BytesReceived > 0;

    /// <summary>Set when a read gave up waiting; distinguishes a stall from a clean stop.</summary>
    public bool Stalled { get; private set; }

    /// <summary>
    /// Copies one callback buffer in. Never blocks, and never throws — it is called from
    /// native code, where an escaping exception kills the process rather than failing a call.
    /// </summary>
    public void Append(IntPtr buffer, int length)
    {
        if (buffer == IntPtr.Zero || length <= 0)
            return;

        // Copied, not referenced: the SDK reuses this buffer the moment the callback returns.
        var chunk = new byte[length];
        try
        {
            Marshal.Copy(buffer, chunk, 0, length);
        }
        catch
        {
            return;
        }

        lock (_gate)
        {
            if (_completed)
                return;

            BytesReceived += length;
            _chunks.Enqueue(chunk);
            _buffered += length;

            // Never drops the chunk just enqueued: on a device whose chunks are larger than
            // Capacity, dropping the newest would deliver nothing at all, forever.
            while (_buffered > Capacity && _chunks.Count > 1)
            {
                var dropped = _chunks.Dequeue();
                _buffered -= dropped.Length;
                BytesDropped += dropped.Length;
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Ends the stream: buffered bytes still read out, then <see cref="Read"/> returns 0.
    /// </summary>
    /// <param name="fault">
    /// A failure to surface to the reader instead of a clean end. Reported on the next read
    /// after the buffer drains, so a stream that broke mid-flight does not look like one
    /// that finished.
    /// </param>
    public void Complete(Exception? fault = null)
    {
        lock (_gate)
        {
            _completed = true;
            _fault ??= fault;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
            throw new ArgumentException("offset + count is past the end of the buffer");
        if (count == 0)
            return 0;

        lock (_gate)
        {
            while (_partial is null && _chunks.Count == 0)
            {
                if (_completed)
                {
                    if (_fault is { } fault)
                    {
                        _fault = null; // reported once; a retry then sees a clean end
                        throw fault;
                    }
                    return 0;
                }
                if (!Monitor.Wait(_gate, StallTimeout))
                {
                    // Reported as end-of-stream rather than as an exception: the caller is
                    // usually a demuxer inside native code, and Stalled is what tells the
                    // operator why it stopped.
                    Stalled = true;
                    return 0;
                }
            }

            if (_partial is null)
            {
                _partial = _chunks.Dequeue();
                _partialOffset = 0;
                _buffered -= _partial.Length;
            }

            int take = Math.Min(_partial.Length - _partialOffset, count);
            Buffer.BlockCopy(_partial, _partialOffset, buffer, offset, take);
            _partialOffset += take;
            if (_partialOffset >= _partial.Length)
                _partial = null;
            return take;
        }
    }

    /// <summary>
    /// Waits for the first media byte. Callers use this to tell "no video is coming" apart
    /// from "video is on its way", which are the same silence up to this point.
    /// </summary>
    public async Task<bool> WaitForDataAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_gate)
            {
                if (BytesReceived > 0)
                    return true;
                if (_completed)
                    return false;
            }
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override void Flush() { }

    public override long Length =>
        throw new NotSupportedException("a live stream has no length");

    public override long Position
    {
        get => throw new NotSupportedException("a live stream has no position");
        set => throw new NotSupportedException("a live stream cannot seek");
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("a live stream cannot seek");

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("written only by Append, from the SDK callback");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Complete();
        base.Dispose(disposing);
    }
}
