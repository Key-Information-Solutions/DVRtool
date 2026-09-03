namespace DVRTool.Core;

/// <summary>How a decoded frame's planes are laid out.</summary>
public enum DewarpFrameFormat
{
    /// <summary>Planar 4:2:0 with separate U and V planes — a software decoder's output.</summary>
    I420,

    /// <summary>
    /// Planar luma with U and V interleaved in one half-height plane — what a hardware decoder's
    /// readback produces naturally.
    /// </summary>
    Nv12,
}

/// <summary>
/// One decoded frame, as planes the renderers read directly.
/// </summary>
/// <remarks>
/// <para>
/// Arrays rather than pointers, matching <see cref="DewarpSampler"/>'s stance: a buffer
/// allocated with <c>GC.AllocateArray&lt;byte&gt;(n, pinned: true)</c> lives on the pinned object
/// heap, so it has a stable address to hand a native decoder <i>and</i> is readable here with no
/// copy and no <c>unsafe</c>.
/// </para>
/// <para>
/// The frame does not own its buffers and does not copy them, so it is only valid for as long as
/// whoever decoded it says it is. Render inside the callback, or copy first.
/// </para>
/// </remarks>
public readonly struct DewarpFrame
{
    private DewarpFrame(DewarpFrameFormat format, int width, int height, YuvRange range,
        byte[] yPlane, int yPitch, byte[]? uPlane, byte[]? vPlane, byte[]? uvPlane, int chromaPitch)
    {
        Format = format;
        Width = width;
        Height = height;
        Range = range;
        YPlane = yPlane;
        YPitch = yPitch;
        UPlane = uPlane;
        VPlane = vPlane;
        UvPlane = uvPlane;
        ChromaPitch = chromaPitch;
    }

    /// <summary>Which plane layout this frame uses.</summary>
    public DewarpFrameFormat Format { get; }

    /// <summary>Frame width in luma pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in luma pixels.</summary>
    public int Height { get; }

    /// <summary>Which YUV matrix and range the frame is in.</summary>
    public YuvRange Range { get; }

    /// <summary>The luma plane.</summary>
    public byte[] YPlane { get; }

    /// <summary>Bytes per row of the luma plane.</summary>
    public int YPitch { get; }

    /// <summary>The U plane, for <see cref="DewarpFrameFormat.I420"/>.</summary>
    public byte[]? UPlane { get; }

    /// <summary>The V plane, for <see cref="DewarpFrameFormat.I420"/>.</summary>
    public byte[]? VPlane { get; }

    /// <summary>The interleaved chroma plane, for <see cref="DewarpFrameFormat.Nv12"/>.</summary>
    public byte[]? UvPlane { get; }

    /// <summary>Bytes per row of the chroma plane or planes.</summary>
    public int ChromaPitch { get; }

    /// <summary>A planar 4:2:0 frame with separate U and V planes.</summary>
    public static DewarpFrame I420(int width, int height,
        byte[] yPlane, int yPitch, byte[] uPlane, byte[] vPlane, int chromaPitch,
        YuvRange range = YuvRange.Bt709Limited)
    {
        ArgumentNullException.ThrowIfNull(yPlane);
        ArgumentNullException.ThrowIfNull(uPlane);
        ArgumentNullException.ThrowIfNull(vPlane);
        return new DewarpFrame(DewarpFrameFormat.I420, width, height, range,
            yPlane, yPitch, uPlane, vPlane, null, chromaPitch);
    }

    /// <summary>A frame with interleaved chroma.</summary>
    public static DewarpFrame Nv12(int width, int height,
        byte[] yPlane, int yPitch, byte[] uvPlane, int uvPitch,
        YuvRange range = YuvRange.Bt709Limited)
    {
        ArgumentNullException.ThrowIfNull(yPlane);
        ArgumentNullException.ThrowIfNull(uvPlane);
        return new DewarpFrame(DewarpFrameFormat.Nv12, width, height, range,
            yPlane, yPitch, null, null, uvPlane, uvPitch);
    }
}

/// <summary>
/// Everything about one dewarped pane that can change between frames.
/// </summary>
/// <param name="Calibration">
/// The lens and circle, as calibrated against whatever frame size the operator measured on. The
/// renderer rescales it to the frame it is actually handed, so a main-to-sub stream switch needs
/// no action here.
/// </param>
/// <param name="View">Where the pane is aimed and how wide it is.</param>
/// <param name="OutputWidth">Pane width in pixels.</param>
/// <param name="OutputHeight">Pane height in pixels.</param>
/// <param name="Bilinear">Blend neighbouring source pixels. Off is a debugging aid.</param>
/// <param name="OutsideColor">
/// What to paint where the pane looks past the rim of the image circle, as BGRA.
/// </param>
/// <param name="LodBias">
/// Nudges the accelerated path's mip selection: negative is sharper and noisier, positive is
/// softer. Ignored by the CPU renderer, whose levels are whole steps.
/// </param>
public readonly record struct DewarpRenderRequest(
    FisheyeCalibration Calibration,
    DewarpView View,
    int OutputWidth,
    int OutputHeight,
    bool Bilinear = true,
    uint OutsideColor = DewarpSampler.OpaqueBlack,
    double LodBias = 0);

/// <summary>
/// One dewarped pane's renderer: hand it frames and a view, and it draws.
/// </summary>
/// <remarks>
/// Deliberately says nothing about where the pixels end up. The CPU renderer's output is an
/// array that a <c>WriteableBitmap</c> takes; the Direct3D one's is a texture WPF shows through a
/// shared surface without ever leaving the adapter. Forcing both through one "give me the pixels"
/// method would mean reading the accelerated one back over the bus every frame and throwing away
/// most of what it bought. <see cref="CopyOutput"/> is therefore the still-image and test path,
/// not the display path.
/// </remarks>
public interface IDewarpRenderer : IDisposable
{
    /// <summary>Which renderer this is, for the GUI to show and the log to record.</summary>
    DewarpBackend Backend { get; }

    /// <summary>Pane width of the last <see cref="Render"/>, in pixels.</summary>
    int OutputWidth { get; }

    /// <summary>Pane height of the last <see cref="Render"/>, in pixels.</summary>
    int OutputHeight { get; }

    /// <summary>Draws one frame through one view.</summary>
    void Render(in DewarpFrame frame, in DewarpRenderRequest request);

    /// <summary>
    /// Copies the last rendered pane out as BGRA. Cheap on the CPU renderer; on an accelerated
    /// one this is a read back off the adapter, so it is for stills, exports and tests.
    /// </summary>
    void CopyOutput(uint[] destination, int stride);
}

/// <summary>
/// The CPU renderer: <see cref="DewarpMap"/> and <see cref="DewarpSampler"/> wired together with
/// the buffers held across frames.
/// </summary>
/// <remarks>
/// <para>
/// The pieces were already there and tested; what was missing was the thing that owns them, and
/// the reason it has to exist is allocation. A 2560×2560 panorama converts a 6.6 Mpx sub-rect to
/// BGRA — 26 MB — and halves it once more on top; allocating that per frame at 20 fps is 600
/// MB/s of garbage straight into the large object heap, which shows up as periodic hitching
/// rather than as a slow frame, and is therefore the kind of problem that gets blamed on the
/// network. So every buffer here grows and is then kept.
/// </para>
/// <para>
/// <b>The table is cached on its inputs, not rebuilt per frame.</b> That is the whole point of
/// <see cref="DewarpMap"/>: a stationary view pays the trigonometry once and every frame after it
/// is a memory walk. The key is the request and the frame size together, so a main-to-sub stream
/// switch invalidates it — which it must, since the circle moves.
/// </para>
/// <para>
/// Not thread-safe: one renderer per pane, driven from one place.
/// </para>
/// </remarks>
public sealed class CpuDewarpRenderer : IDewarpRenderer
{
    private uint[] _converted = [];
    private uint[] _scratchA = [];
    private uint[] _scratchB = [];
    private uint[] _output = [];

    private DewarpMap? _map;
    private DewarpRenderRequest _mapRequest;
    private int _mapSourceWidth;
    private int _mapSourceHeight;

    /// <inheritdoc />
    public DewarpBackend Backend => DewarpBackend.Cpu;

    /// <inheritdoc />
    public int OutputWidth { get; private set; }

    /// <inheritdoc />
    public int OutputHeight { get; private set; }

    /// <summary>
    /// How many times the source was box-halved for the last frame, from
    /// <see cref="DewarpMap.MipLevel"/>. Exposed because it is the one number that explains a
    /// soft-looking pane.
    /// </summary>
    public int MipLevel { get; private set; }

    /// <summary>The source region the last frame actually converted.</summary>
    public SourceRect LastBounds { get; private set; }

    /// <inheritdoc />
    public void Render(in DewarpFrame frame, in DewarpRenderRequest request)
    {
        int outWidth = Math.Max(1, request.OutputWidth);
        int outHeight = Math.Max(1, request.OutputHeight);
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        Grow(ref _output, outWidth * outHeight);

        var map = MapFor(request, frame.Width, frame.Height, outWidth, outHeight);
        var bounds = map.Bounds;
        LastBounds = bounds;
        MipLevel = map.MipLevel;

        if (bounds.IsEmpty)
        {
            // The pane looks entirely past the rim. Nothing to convert, and every table entry is
            // the sentinel, so filling directly is both correct and the cheapest answer.
            Array.Fill(_output, request.OutsideColor, 0, outWidth * outHeight);
            return;
        }

        Grow(ref _converted, (int)bounds.PixelCount);
        Convert(frame, bounds, request);

        var source = _converted;
        int width = bounds.Width, height = bounds.Height, stride = bounds.Width;
        for (int level = 0; level < MipLevel; level++)
        {
            int halfWidth = (width + 1) / 2;
            int halfHeight = (height + 1) / 2;
            // Ping-pong, so three levels need two scratch buffers rather than three: the first
            // halving reads the converted buffer, and each one after it reads the other scratch.
            ref uint[] target = ref (level % 2 == 0 ? ref _scratchA : ref _scratchB);
            Grow(ref target, halfWidth * halfHeight);
            DewarpSampler.BoxHalve(source, width, height, stride, target, halfWidth);
            source = target;
            width = halfWidth;
            height = halfHeight;
            stride = halfWidth;
        }

        DewarpSampler.Sample(source, width, height, stride, bounds, MipLevel,
            map, _output, outWidth, request.Bilinear, request.OutsideColor);
    }

    /// <inheritdoc />
    public void CopyOutput(uint[] destination, int stride)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int row = 0; row < OutputHeight; row++)
            Array.Copy(_output, row * OutputWidth, destination, row * stride, OutputWidth);
    }

    /// <summary>
    /// The pane itself, as BGRA rows of <see cref="OutputWidth"/>. Valid until the next
    /// <see cref="Render"/>, which overwrites it in place.
    /// </summary>
    public ReadOnlySpan<uint> Output => _output.AsSpan(0, OutputWidth * OutputHeight);

    /// <summary>Nothing native is held; present so the interface can be uniform.</summary>
    public void Dispose()
    {
        _converted = [];
        _scratchA = [];
        _scratchB = [];
        _output = [];
        _map = null;
    }

    private DewarpMap MapFor(in DewarpRenderRequest request,
        int sourceWidth, int sourceHeight, int outWidth, int outHeight)
    {
        // The bilinear and outside-colour options do not enter the table, so a change to either
        // must not throw it away; comparing the whole request would.
        if (_map is not null &&
            _mapSourceWidth == sourceWidth && _mapSourceHeight == sourceHeight &&
            _map.OutputWidth == outWidth && _map.OutputHeight == outHeight &&
            _mapRequest.Calibration == request.Calibration &&
            _mapRequest.View == request.View)
            return _map;

        _map = DewarpMap.Build(request.Calibration, request.View,
            outWidth, outHeight, sourceWidth, sourceHeight);
        _mapRequest = request;
        _mapSourceWidth = sourceWidth;
        _mapSourceHeight = sourceHeight;
        return _map;
    }

    private void Convert(in DewarpFrame frame, in SourceRect bounds,
        in DewarpRenderRequest request)
    {
        if (frame.Format == DewarpFrameFormat.Nv12)
        {
            DewarpSampler.ConvertNv12ToBgra(frame.YPlane, frame.YPitch,
                frame.UvPlane!, frame.ChromaPitch, bounds, frame.Range, _converted, bounds.Width);
            return;
        }
        DewarpSampler.ConvertI420ToBgra(frame.YPlane, frame.YPitch,
            frame.UPlane!, frame.VPlane!, frame.ChromaPitch, bounds, frame.Range,
            _converted, bounds.Width);
    }

    /// <summary>Grows a buffer to at least a length, keeping it if it is already big enough.</summary>
    private static void Grow(ref uint[] buffer, int length)
    {
        if (buffer.Length < length)
            buffer = new uint[Math.Max(length, 1)];
    }
}
