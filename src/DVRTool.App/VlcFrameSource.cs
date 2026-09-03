using System.Runtime.InteropServices;
using System.Windows.Threading;
using DVRTool.Core;
using LibVLCSharp.Shared;

namespace DVRTool.App;

/// <summary>
/// A LibVLC player whose decoded frames come back to us instead of going to a window: the frame
/// source for the dewarp pane.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the piece that was missing.</b> The Live tab's player paints straight into a child
/// window and the decoded pixels never come back; a dewarp has to read them. LibVLC 3's video
/// callbacks (the <c>vmem</c> output) do that: we say what layout we want, hand over a buffer for
/// each frame, and are told when it has been filled. The player is otherwise the same one the
/// Live tab uses — same demuxer, same decoder, same <c>:avcodec-threads=1</c> — so the SDK stream
/// that works at fourteen sites works here too.
/// </para>
/// <para>
/// <b>What LibVLC actually does with the callbacks</b>, because the ring depends on it: on every
/// frame its output thread calls <i>lock</i> once for a buffer, copies the decoded picture into it,
/// calls <i>unlock</i>, and then, when the playback clock says so, <i>display</i>. Strictly one
/// buffer at a time, always in that order. That is why three slots are enough
/// (<see cref="DewarpFrameRing"/>) and why the frame handed to <i>display</i> is complete: nothing
/// is still writing it.
/// </para>
/// <para>
/// <b>I420 is requested regardless of what the decoder produces.</b> Software H.264 and H.265
/// decode to it natively, so for the streams this application sees the request costs nothing; for
/// anything else LibVLC converts, which is slower than accepting the decoder's format but keeps
/// exactly one plane layout on the far side. The buffer pitches are rounded up to 32 bytes
/// because the converters and the decoders' copy loops assume alignment they do not check for.
/// </para>
/// <para>
/// <b>Frames reach the UI thread coalesced.</b> <i>display</i> runs on LibVLC's thread; it marks
/// the frame ready and posts one request to the dispatcher if none is outstanding. If the UI is
/// still drawing the previous frame when the next arrives, the ring keeps only the newest and
/// <see cref="FramesSkipped"/> goes up — a live view wants the latest picture, never a backlog. The
/// delivery runs at render priority, so it never starves input.
/// </para>
/// <para>
/// <b>Never throw out of a callback.</b> An exception crossing back into native code ends the
/// process without a dialog. Every callback here catches, records the message in
/// <see cref="LastError"/>, and returns something LibVLC can live with.
/// </para>
/// </remarks>
public sealed class VlcFrameSource : IDisposable
{
    private const int PitchAlignment = 32;

    private readonly MediaPlayer _player;
    private readonly Dispatcher _dispatcher;
    private readonly Action<DewarpFrame> _sink;
    private readonly DewarpFrameRing _ring = new();
    private readonly Action _deliver;
    private readonly object _stopLock = new();

    // Held in fields so the delegates outlive the native registrations. LibVLC keeps only the
    // function pointers; a collected delegate is a crash on the next frame.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private int _pending;
    private volatile bool _disposed;

    /// <summary>
    /// Creates the player and registers the callbacks. Nothing plays until <see cref="Play"/>.
    /// </summary>
    /// <param name="libVlc">The application's LibVLC instance.</param>
    /// <param name="dispatcher">The UI thread's dispatcher; <paramref name="sink"/> runs on it.</param>
    /// <param name="sink">
    /// Receives each frame on the UI thread. The frame is valid until the sink is next called, so
    /// the consumer may keep it for redraws in between.
    /// </param>
    public VlcFrameSource(LibVLC libVlc, Dispatcher dispatcher, Action<DewarpFrame> sink)
    {
        ArgumentNullException.ThrowIfNull(libVlc);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _deliver = Deliver;

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;

        _player = new MediaPlayer(libVlc);
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
    }

    /// <summary>The player, for statistics. Do not attach it to a view.</summary>
    public MediaPlayer Player => _player;

    /// <summary>Frame width in luma pixels, or 0 before the decoder has reported a format.</summary>
    public int FrameWidth => _ring.Width;

    /// <summary>Frame height in luma pixels, or 0 before the decoder has reported a format.</summary>
    public int FrameHeight => _ring.Height;

    /// <summary>Frames the decoder finished, whether or not they were shown.</summary>
    public long FramesReceived => _ring.Completed;

    /// <summary>Frames handed to the sink.</summary>
    public long FramesShown => _ring.Taken;

    /// <summary>Finished frames the UI was too late for and that were replaced by a newer one.</summary>
    public long FramesSkipped => _ring.Dropped;

    /// <summary>The last exception message from a callback, or null. Diagnostic only.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts playing. Any previous media is stopped first by LibVLC.</summary>
    public void Play(Media media)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _player.Play(media);
    }

    /// <summary>
    /// Stops playback. <b>Blocks until LibVLC's threads have joined</b>, which can be a while for
    /// a stream that is mid-connect, so call it off the UI thread — <see cref="StopAsync"/>.
    /// After it returns no callback will run again until the next <see cref="Play"/>.
    /// </summary>
    public void Stop()
    {
        lock (_stopLock)
        {
            if (_disposed)
                return;
            try
            {
                _player.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>The same, on a worker thread.</summary>
    public Task StopAsync() => Task.Run(Stop);

    /// <summary>
    /// The frame most recently handed to the sink, still valid. False before the first frame or
    /// after the format changed underneath it.
    /// </summary>
    public bool TryGetLastFrame(out DewarpFrame frame)
    {
        int slot = _ring.PresentingSlot;
        if (slot < 0 || !_ring.IsConfigured)
        {
            frame = default;
            return false;
        }
        frame = _ring.FrameOf(slot);
        return true;
    }

    /// <summary>Stops, then releases the player. Blocking, like <see cref="Stop"/>.</summary>
    public void Dispose()
    {
        lock (_stopLock)
        {
            if (_disposed)
                return;
            try
            {
                _player.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
            _disposed = true;
            _player.Dispose();
        }
    }

    // ----- LibVLC's thread -----

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
        ref uint pitches, ref uint lines)
    {
        try
        {
            // Four bytes, no terminator: the chroma is a fourcc.
            Marshal.WriteByte(chroma, 0, (byte)'I');
            Marshal.WriteByte(chroma, 1, (byte)'4');
            Marshal.WriteByte(chroma, 2, (byte)'2');
            Marshal.WriteByte(chroma, 3, (byte)'0');

            int w = checked((int)width);
            int h = checked((int)height);
            if (w <= 0 || h <= 0)
                return 0;

            int yPitch = Align(w);
            int yLines = Align(h);
            int chromaPitch = Align((w + 1) / 2);
            int chromaLines = Align((h + 1) / 2);

            // pitches and lines are arrays of one entry per plane; LibVLC hands them over as a
            // reference to the first. A span over that reference is how the second and third are
            // reached without unsafe code.
            var pitchSpan = MemoryMarshal.CreateSpan(ref pitches, 3);
            var lineSpan = MemoryMarshal.CreateSpan(ref lines, 3);
            pitchSpan[0] = (uint)yPitch;
            pitchSpan[1] = pitchSpan[2] = (uint)chromaPitch;
            lineSpan[0] = (uint)yLines;
            lineSpan[1] = lineSpan[2] = (uint)chromaLines;

            _ring.Configure(w, h, yPitch, yLines, chromaPitch, chromaLines, YuvRange.Bt709Limited);
            return (uint)DewarpFrameRing.SlotCount;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return 0; // "could not allocate": LibVLC reports the output as failed rather than crashing
        }
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        // The buffers stay: the next format callback reuses them when the size is unchanged, and
        // the UI may still be showing the last frame.
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        try
        {
            int slot = _ring.AcquireForWrite();
            Marshal.WriteIntPtr(planes, 0, Marshal.UnsafeAddrOfPinnedArrayElement(_ring.YPlane(slot), 0));
            Marshal.WriteIntPtr(planes, IntPtr.Size, Marshal.UnsafeAddrOfPinnedArrayElement(_ring.UPlane(slot), 0));
            Marshal.WriteIntPtr(planes, 2 * IntPtr.Size, Marshal.UnsafeAddrOfPinnedArrayElement(_ring.VPlane(slot), 0));
            // Slot numbers are offset by one so that a null picture pointer never means slot 0.
            return slot + 1;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return IntPtr.Zero;
        }
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // Decoding into the buffer is complete here, but the frame is not due on screen until
        // display says so; nothing to do.
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        try
        {
            int slot = (int)(long)picture - 1;
            if (slot < 0 || _disposed)
                return;
            _ring.CompleteWrite(slot);
            // One outstanding delivery at a time. If the UI has not drained the previous one, the
            // ring already holds the newest frame and the queued delivery will pick it up.
            if (Interlocked.Exchange(ref _pending, 1) == 0)
                _dispatcher.BeginInvoke(DispatcherPriority.Render, _deliver);
        }
        catch (Exception ex)
        {
            // A display for a slot that a format change reset, or a lock that failed: skip the
            // frame, keep the stream.
            LastError = ex.Message;
        }
    }

    // ----- the UI thread -----

    private void Deliver()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_disposed)
            return;
        if (_ring.TryTakeLatest(out int slot))
            _sink(_ring.FrameOf(slot));
    }

    private static int Align(int value) =>
        (value + PitchAlignment - 1) / PitchAlignment * PitchAlignment;
}
