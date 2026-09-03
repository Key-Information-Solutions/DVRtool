namespace DVRTool.Core;

/// <summary>
/// The buffers between a video decoder writing frames on its own thread and a UI thread drawing
/// them: three fixed slots, one being written, one finished and waiting, one on screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why three, and why a ring rather than a queue.</b> LibVLC 3's <c>vmem</c> output is
/// strictly sequential — its <c>Prepare</c> locks one buffer, copies the decoded picture into it,
/// unlocks, and only then calls <c>display</c>, all on the one video-output thread — so at most
/// one slot is ever being written. The UI keeps the frame it last drew alive until it takes the
/// next, so that a view change (a drag, a resize) can redraw without a new frame. That leaves one
/// slot for a finished frame waiting to be shown, and a decoder that gets ahead of the display
/// overwrites <i>that</i> slot rather than queueing behind it: a live view wants the newest
/// picture, never a backlog of old ones. Three slots therefore suffice exactly, and a fourth would
/// only add latency.
/// </para>
/// <para>
/// <b>The buffers are pinned for the decoder's benefit.</b> Allocated on the pinned object heap
/// (<c>GC.AllocateArray(pinned: true)</c>), so their addresses can be handed to native code and
/// stay valid for the life of the ring, while the same arrays are readable from managed code
/// with no copy and no <c>unsafe</c> — the stance <see cref="DewarpFrame"/> takes.
/// </para>
/// <para>
/// <b>Thread-safe</b>: the decoder-side methods (<see cref="AcquireForWrite"/>,
/// <see cref="CompleteWrite"/>) and the UI-side one (<see cref="TryTakeLatest"/>) may be called
/// from different threads concurrently. Every state change is under one lock and holds it for a
/// handful of instructions; no buffer is ever copied under it.
/// </para>
/// <para>
/// I420 only, because that is the layout DVRTool asks its decoder for. The ring's job is
/// ownership, not format negotiation.
/// </para>
/// </remarks>
public sealed class DewarpFrameRing
{
    /// <summary>How many slots the ring holds. Fixed; see the type remarks for why three.</summary>
    public const int SlotCount = 3;

    private enum SlotState
    {
        Free,
        Writing,
        Ready,
        Presenting,
    }

    private readonly object _gate = new();
    private readonly SlotState[] _states = new SlotState[SlotCount];
    private readonly byte[][] _y = new byte[SlotCount][];
    private readonly byte[][] _u = new byte[SlotCount][];
    private readonly byte[][] _v = new byte[SlotCount][];

    private int _width;
    private int _height;
    private int _yPitch;
    private int _chromaPitch;
    private YuvRange _range = YuvRange.Bt709Limited;
    private int _ready = -1;
    private int _presenting = -1;

    /// <summary>Frame width in luma pixels, or 0 before <see cref="Configure"/>.</summary>
    public int Width => _width;

    /// <summary>Frame height in luma pixels, or 0 before <see cref="Configure"/>.</summary>
    public int Height => _height;

    /// <summary>Bytes per row of each slot's luma plane.</summary>
    public int YPitch => _yPitch;

    /// <summary>Bytes per row of each slot's U and V planes.</summary>
    public int ChromaPitch => _chromaPitch;

    /// <summary>True once buffers exist.</summary>
    public bool IsConfigured => _width > 0;

    /// <summary>Frames the decoder finished. Counts overwritten ones too.</summary>
    public long Completed { get; private set; }

    /// <summary>
    /// Finished frames the UI never saw, because the next one arrived first. A steadily rising
    /// number means the UI thread cannot keep up with the stream, not that the network is losing
    /// anything.
    /// </summary>
    public long Dropped { get; private set; }

    /// <summary>Frames handed to the UI by <see cref="TryTakeLatest"/>.</summary>
    public long Taken { get; private set; }

    /// <summary>
    /// Allocates (or reallocates) the three slots for a frame size. Any frame in flight is
    /// forgotten, which is right: a decoder that reports a new format has nothing older to show.
    /// </summary>
    /// <param name="width">Visible width in luma pixels.</param>
    /// <param name="height">Visible height in luma pixels.</param>
    /// <param name="yPitch">Bytes per luma row; at least <paramref name="width"/>.</param>
    /// <param name="yLines">Luma rows to allocate; at least <paramref name="height"/>.</param>
    /// <param name="chromaPitch">Bytes per chroma row; at least half the width, rounded up.</param>
    /// <param name="chromaLines">Chroma rows to allocate; at least half the height, rounded up.</param>
    /// <param name="range">Which YUV matrix and range the decoder writes.</param>
    public void Configure(int width, int height, int yPitch, int yLines, int chromaPitch,
        int chromaLines, YuvRange range = YuvRange.Bt709Limited)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "A frame needs a positive size.");
        if (yPitch < width)
            throw new ArgumentOutOfRangeException(nameof(yPitch), "The luma pitch is narrower than the frame.");
        if (yLines < height)
            throw new ArgumentOutOfRangeException(nameof(yLines), "Fewer luma rows than the frame has.");
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        if (chromaPitch < chromaWidth)
            throw new ArgumentOutOfRangeException(nameof(chromaPitch), "The chroma pitch is narrower than the frame.");
        if (chromaLines < chromaHeight)
            throw new ArgumentOutOfRangeException(nameof(chromaLines), "Fewer chroma rows than the frame has.");

        lock (_gate)
        {
            int ySize = checked(yPitch * yLines);
            int cSize = checked(chromaPitch * chromaLines);
            for (int i = 0; i < SlotCount; i++)
            {
                // Reallocated only when the size changes: a stream that renegotiates the same
                // format (LibVLC does, on every restart) should not churn 30 MB of pinned heap.
                if (_y[i] is null || _y[i].Length != ySize)
                    _y[i] = GC.AllocateArray<byte>(ySize, pinned: true);
                if (_u[i] is null || _u[i].Length != cSize)
                {
                    _u[i] = GC.AllocateArray<byte>(cSize, pinned: true);
                    _v[i] = GC.AllocateArray<byte>(cSize, pinned: true);
                }
                _states[i] = SlotState.Free;
            }
            _width = width;
            _height = height;
            _yPitch = yPitch;
            _chromaPitch = chromaPitch;
            _range = range;
            _ready = -1;
            _presenting = -1;
        }
    }

    /// <summary>
    /// The slot the decoder should write its next frame into. Never the slot on screen and never
    /// the finished frame waiting to be shown.
    /// </summary>
    /// <remarks>
    /// A slot still marked as being written is reclaimed here rather than skipped. The decoder is
    /// sequential, so a slot in that state at the time of a new acquisition was abandoned — a
    /// lock whose display never came — and leaving it would leak the ring one slot at a time.
    /// </remarks>
    public int AcquireForWrite()
    {
        lock (_gate)
        {
            ThrowIfUnconfigured();
            int abandoned = -1;
            for (int i = 0; i < SlotCount; i++)
            {
                if (_states[i] == SlotState.Free)
                {
                    _states[i] = SlotState.Writing;
                    return i;
                }
                if (_states[i] == SlotState.Writing)
                    abandoned = i;
            }
            if (abandoned >= 0)
                return abandoned;
            // Unreachable with three slots and one writer, but a wrong answer here would hand the
            // decoder the frame on screen, so it is an error rather than a guess.
            throw new InvalidOperationException("Every slot of the frame ring is in use.");
        }
    }

    /// <summary>
    /// Marks a slot's frame finished and makes it the one <see cref="TryTakeLatest"/> returns. A
    /// finished frame nobody took yet is discarded and counted in <see cref="Dropped"/>.
    /// </summary>
    public void CompleteWrite(int slot)
    {
        lock (_gate)
        {
            ThrowIfUnconfigured();
            if (slot < 0 || slot >= SlotCount || _states[slot] != SlotState.Writing)
                throw new ArgumentException("That slot is not being written.", nameof(slot));
            if (_ready >= 0)
            {
                _states[_ready] = SlotState.Free;
                Dropped++;
            }
            _states[slot] = SlotState.Ready;
            _ready = slot;
            Completed++;
        }
    }

    /// <summary>
    /// Takes the newest finished frame for display, releasing the one previously taken. False
    /// when nothing new has finished since the last call.
    /// </summary>
    /// <remarks>
    /// The frame returned stays valid — the decoder will not be handed its slot — until the next
    /// successful call. That is what lets a view change redraw the picture already on screen
    /// without waiting for the camera.
    /// </remarks>
    public bool TryTakeLatest(out int slot)
    {
        lock (_gate)
        {
            if (_ready < 0)
            {
                slot = -1;
                return false;
            }
            if (_presenting >= 0)
                _states[_presenting] = SlotState.Free;
            slot = _ready;
            _states[slot] = SlotState.Presenting;
            _presenting = slot;
            _ready = -1;
            Taken++;
            return true;
        }
    }

    /// <summary>The slot most recently returned by <see cref="TryTakeLatest"/>, or −1.</summary>
    public int PresentingSlot
    {
        get { lock (_gate) return _presenting; }
    }

    /// <summary>A slot's contents as a frame. The planes belong to the ring; see the remarks.</summary>
    public DewarpFrame FrameOf(int slot)
    {
        lock (_gate)
        {
            ThrowIfUnconfigured();
            if (slot < 0 || slot >= SlotCount)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return DewarpFrame.I420(_width, _height, _y[slot], _yPitch, _u[slot], _v[slot],
                _chromaPitch, _range);
        }
    }

    /// <summary>A slot's luma buffer, for handing its address to the decoder.</summary>
    public byte[] YPlane(int slot) => Plane(_y, slot);

    /// <summary>A slot's U buffer.</summary>
    public byte[] UPlane(int slot) => Plane(_u, slot);

    /// <summary>A slot's V buffer.</summary>
    public byte[] VPlane(int slot) => Plane(_v, slot);

    private byte[] Plane(byte[][] planes, int slot)
    {
        lock (_gate)
        {
            ThrowIfUnconfigured();
            if (slot < 0 || slot >= SlotCount)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return planes[slot];
        }
    }

    private void ThrowIfUnconfigured()
    {
        if (_width <= 0)
            throw new InvalidOperationException("The frame ring has no buffers yet; call Configure first.");
    }
}
