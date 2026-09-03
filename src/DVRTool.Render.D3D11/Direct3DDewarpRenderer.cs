using System.Reflection;
using System.Runtime.InteropServices;
using DVRTool.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DVRTool.Render.D3D11;

/// <summary>
/// The accelerated dewarp path: the frame's planes go up to the adapter once, and colour
/// conversion, mip generation, the projection and the gather all happen there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, given the CPU renderer is fast.</b> The CPU path lands a 2560×2560
/// panorama in about 7 ms across twenty-four cores — genuinely usable, which is why it was worth
/// building. But that figure is 17 ms on four cores, it is the whole machine's worth of cores for
/// one pane rather than one of sixteen in a grid, and every pan or zoom also rebuilds a
/// <see cref="DewarpMap"/> on top of it. Here the per-frame work is one upload and one draw, the
/// view costs 176 bytes of constants, and the mip level is chosen per pixel instead of per pane,
/// so a wide view aliases less as well as arriving sooner.
/// </para>
/// <para>
/// <b>The plan is convert-nothing.</b> The CPU pipeline converts a sub-rect to BGRA, box-halves
/// it and then gathers, and its <see cref="DewarpMap.Bounds"/> optimization exists because
/// converting a whole 6.6 Mpx frame per frame is the dominant cost at that size. None of that
/// applies here: the planes are uploaded as they arrive, <c>GenerateMips</c> builds the chain, and
/// the shader converts exactly the pixels it samples. So there is no sub-rect to track and no
/// bounding box to get wrong — the whole frame goes up every time, which at 2560×2560 NV12 and
/// 20 fps is under 200 MB/s across a bus that carries several gigabytes.
/// </para>
/// <para>
/// <b>Colour conversion is deliberately done after filtering, and that is not a shortcut.</b> The
/// CPU converts first and blends BGRA; this blends Y, U and V and converts after. Those give the
/// same answer, because YUV-to-RGB is an affine map and an affine map commutes with a weighted
/// average — the only difference is where the clamp lands, which matters solely for samples
/// already outside 0-255. It is what lets the mip chain live on the planes.
/// </para>
/// <para>
/// Not thread-safe, and tied to the thread that built it: one renderer per pane.
/// </para>
/// </remarks>
public sealed class Direct3DDewarpRenderer : IDewarpRenderer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _planarShader;
    private readonly ID3D11PixelShader _interleavedShader;
    private readonly ID3D11SamplerState _trilinear;
    private readonly ID3D11SamplerState _point;
    private readonly ID3D11SamplerState _trilinearTopLevel;
    private readonly ID3D11SamplerState _pointTopLevel;
    private readonly ID3D11Buffer _constants;
    private readonly float[] _constantScratch = new float[DewarpShaderConstants.FloatCount];
    private readonly bool _ownsDevice;

    private PlaneTexture? _luma;
    private PlaneTexture? _chromaInterleaved;
    private PlaneTexture? _chromaU;
    private PlaneTexture? _chromaV;
    private int _frameWidth;
    private int _frameHeight;
    private DewarpFrameFormat _frameFormat = DewarpFrameFormat.I420;
    private int _uploadedWidth;
    private int _uploadedHeight;
    private YuvRange _uploadedRange = YuvRange.Bt709Limited;
    private bool _hasFrame;
    private bool _mipsGenerated;

    private IDXGISwapChain1? _swapChain;
    private int _swapWidth;
    private int _swapHeight;
    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _targetView;
    private ID3D11Texture2D? _readback;
    private bool _disposed;

    private Direct3DDewarpRenderer(ID3D11Device device, ID3D11DeviceContext context,
        bool ownsDevice, string adapterDescription)
    {
        _device = device;
        _context = context;
        _ownsDevice = ownsDevice;
        AdapterDescription = adapterDescription;

        string source = LoadShaderSource();
        _vertexShader = _device.CreateVertexShader(
            Compile(source, "VsMain", "vs_5_0", planarChroma: false).Span);
        _planarShader = _device.CreatePixelShader(
            Compile(source, "PsMain", "ps_5_0", planarChroma: true).Span);
        _interleavedShader = _device.CreatePixelShader(
            Compile(source, "PsMain", "ps_5_0", planarChroma: false).Span);

        _trilinear = _device.CreateSamplerState(Sampler(Filter.MinMagMipLinear));
        _point = _device.CreateSamplerState(Sampler(Filter.MinMagMipPoint));
        // The safety net for a mip chain that has not been built: see Minifies.
        _trilinearTopLevel = _device.CreateSamplerState(Sampler(Filter.MinMagMipLinear, 0));
        _pointTopLevel = _device.CreateSamplerState(Sampler(Filter.MinMagMipPoint, 0));
        _constants = _device.CreateBuffer(new BufferDescription(
            DewarpShaderConstants.ByteCount, BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    /// <inheritdoc />
    public DewarpBackend Backend => DewarpBackend.Gpu;

    /// <inheritdoc />
    public int OutputWidth { get; private set; }

    /// <inheritdoc />
    public int OutputHeight { get; private set; }

    /// <summary>The adapter this renderer is running on, for the GUI to name.</summary>
    public string AdapterDescription { get; }

    /// <summary>
    /// The Direct3D 11 device this renderer created, exposed so a caller can ask it about its own
    /// capabilities or share resources with it. Its lifetime is this renderer's.
    /// </summary>
    public ID3D11Device Device => _device;

    /// <summary>
    /// Creates a renderer on the default hardware adapter, or throws.
    /// </summary>
    /// <remarks>
    /// Throwing rather than returning null is on purpose: every caller of this is already inside
    /// a try that falls back to <see cref="CpuDewarpRenderer"/>, and the exception message is what
    /// <see cref="DewarpBackendPolicy"/> puts in front of the operator.
    /// </remarks>
    public static Direct3DDewarpRenderer Create()
    {
        var (device, context, description) = Direct3DProbe.CreateDevice();
        try
        {
            return new Direct3DDewarpRenderer(device, context, ownsDevice: true, description);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Render(in DewarpFrame frame, in DewarpRenderRequest request)
    {
        Upload(frame);
        RenderPane(request);
    }

    /// <summary>
    /// Puts one decoded frame's planes on the adapter, without drawing. The mip chain is built
    /// lazily by the first pane that needs it — see <see cref="Minifies"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Split out from <see cref="Render"/> because it is the only cost this renderer has, and
    /// it is per frame rather than per pane.</b> Measured at 2560×2560 on an RTX 5090: the draw
    /// itself is <b>0.02–0.04 ms</b> for a 1600×900 pane — effectively free, which is the whole
    /// premise — and the remaining <b>~1.6 ms</b> is getting the 9.8 MB of planes across, most of
    /// it a memcpy on the calling thread. A <see cref="DewarpViewMode.Quad"/> view is four panes
    /// of one frame and a grid of one fisheye is up to sixteen, so paying that once instead of per
    /// pane decides whether the accelerated path wins on the cases that matter most: sixteen
    /// 480×270 panes measured <b>7.1 ms</b> as sixteen <see cref="Render"/> calls and
    /// <b>0.62 ms</b> as one <see cref="Upload"/> plus sixteen <see cref="RenderPane"/> calls,
    /// against the CPU renderer's 6.6 ms.
    /// </para>
    /// <para>
    /// The frame stays on the adapter until the next upload, so a pane can be redrawn — for a
    /// resize, or on every tick of a drag — with no frame traffic at all. That is what makes
    /// dragging cost 0.77 ms against the CPU renderer's 7.95 ms: nothing is re-uploaded and no
    /// sample table is rebuilt.
    /// </para>
    /// <para>
    /// <b>This upload is the only cost left, and it is not a blocker.</b> It exists because
    /// LibVLC 3.0 hands frames back through <c>vmem</c>, in system memory, so they have to be
    /// pushed to the adapter to be used there — about 5 % of a 33 ms frame period at 2560×2560,
    /// and the live run of 2026-09-03 on Site C showed 30 fps decoded, 30 shown, none skipped.
    /// Deleting it means the decoder writing GPU-visible memory: LibVLC 4's Direct3D 11 output
    /// callbacks, a hardware decoder producing NV12 textures, or handing <c>vmem</c> a mapped
    /// staging texture as its buffer. Any of those is a change to the frame source, not to this
    /// renderer.
    /// </para>
    /// </remarks>
    public void Upload(in DewarpFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePlanes(frame);
        UploadPlanes(frame);
        _uploadedWidth = frame.Width;
        _uploadedHeight = frame.Height;
        _uploadedRange = frame.Range;
        _hasFrame = true;
        // Stale rather than rebuilt: the chain is built on the first pane that actually needs it,
        // which for a zoomed-in view is never.
        _mipsGenerated = false;
    }

    /// <summary>
    /// Draws one pane from the frame most recently given to <see cref="Upload"/>.
    /// </summary>
    public void RenderPane(in DewarpRenderRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasFrame)
            throw new InvalidOperationException(
                "No frame has been uploaded yet; call Upload or Render first.");

        // EnsureTarget is what settles the pane size, not the request: with a presenter-owned
        // target the surface WPF is already showing decides, and quietly rendering the requested
        // size into a differently sized surface would stretch the picture rather than fail.
        EnsureTarget(Math.Max(1, request.OutputWidth), Math.Max(1, request.OutputHeight));

        // The calibration describes whatever frame the operator measured against; the shader is
        // being aimed at this one. Same rescale the CPU table does, and for the same reason: PS
        // Kia's fisheyes are 2560x2560 main and 720x720 sub, so skipping it aims the view at
        // empty space the moment someone switches streams.
        var calibration = request.Calibration.ScaledTo(_uploadedWidth, _uploadedHeight);
        var geometry = FisheyeProjection.For(
            calibration, request.View, OutputWidth, OutputHeight);
        var constants = geometry.ShaderConstants(
            _uploadedRange, request.OutsideColor, request.LodBias);
        constants.WriteTo(_constantScratch);
        _context.UpdateSubresource(_constantScratch, _constants);

        bool needsMips = Minifies(constants);
        if (needsMips)
            EnsureMips();
        Draw(_frameFormat, request.Bilinear, mipsAvailable: _mipsGenerated);
    }

    /// <summary>
    /// Whether any part of this pane gathers from more than one source pixel, and therefore needs
    /// the mip chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth asking because building the chain is a real share of the only per-frame cost this
    /// renderer has, and a zoomed-in pane — which is most of what an operator looks at, since
    /// zooming in is the point of a fisheye — reads nothing but the top level. A grid of 289
    /// points costs a few tens of microseconds against a chain that costs a large fraction of a
    /// millisecond.
    /// </para>
    /// <para>
    /// <b>A false negative here cannot show stale pixels, only aliasing.</b> That is what the
    /// paired sampler states are for: when the chain has not been built,
    /// <see cref="Draw"/> binds one clamped to level 0, so a pixel whose footprint the grid
    /// happened to miss samples the top level rather than a level that was never written. Getting
    /// that wrong would be the worst class of bug available here — last frame's contents, or
    /// uninitialised memory, in part of a live view — so the guard is structural rather than a
    /// matter of the grid being fine enough.
    /// </para>
    /// </remarks>
    private static bool Minifies(in DewarpShaderConstants constants)
    {
        const int steps = 16;
        double width = Math.Max(1, constants.OutputWidth - 1);
        double height = Math.Max(1, constants.OutputHeight - 1);
        for (int row = 0; row <= steps; row++)
        {
            for (int column = 0; column <= steps; column++)
            {
                if (constants.LodFor(column * width / steps, row * height / steps) > 0)
                    return true;
            }
        }
        return false;
    }

    private void EnsureMips()
    {
        if (_mipsGenerated)
            return;
        _context.GenerateMips(_luma!.View);
        if (_chromaInterleaved is not null)
            _context.GenerateMips(_chromaInterleaved.View);
        if (_chromaU is not null)
            _context.GenerateMips(_chromaU.View);
        if (_chromaV is not null)
            _context.GenerateMips(_chromaV.View);
        _mipsGenerated = true;
    }

    /// <inheritdoc />
    public void CopyOutput(uint[] destination, int stride)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_target is null || OutputWidth == 0 || OutputHeight == 0)
            return;

        EnsureReadback(OutputWidth, OutputHeight);
        _context.CopyResource(_readback!, _target);
        var mapped = _context.Map(_readback!, 0, MapMode.Read);
        try
        {
            // Marshal.Copy rather than a span cast: the row pitch the driver hands back is its
            // own business and is routinely wider than the pane, so the rows are copied one at a
            // time. int[] and uint[] are the same 32 bits, and BitConverter would allocate.
            var row = new int[OutputWidth];
            for (int y = 0; y < OutputHeight; y++)
            {
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, row, 0, OutputWidth);
                for (int x = 0; x < OutputWidth; x++)
                    destination[y * stride + x] = unchecked((uint)row[x]);
            }
        }
        finally
        {
            _context.Unmap(_readback!, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _readback?.Dispose();
        _targetView?.Dispose();
        _target?.Dispose();
        _swapChain?.Dispose();
        _luma?.Dispose();
        _chromaInterleaved?.Dispose();
        _chromaU?.Dispose();
        _chromaV?.Dispose();
        _constants.Dispose();
        _pointTopLevel.Dispose();
        _trilinearTopLevel.Dispose();
        _point.Dispose();
        _trilinear.Dispose();
        _interleavedShader.Dispose();
        _planarShader.Dispose();
        _vertexShader.Dispose();
        if (_ownsDevice)
        {
            _context.Dispose();
            _device.Dispose();
        }
    }

    /// <summary>
    /// Points this renderer at a window, so a finished pane is shown without ever coming back
    /// across the bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A DXGI swap chain on a hosted child window, rather than WPF's <c>D3DImage</c>.</b> The
    /// alternative is real, and is what most WPF/Direct3D interop does: render into a Direct3D 9Ex
    /// surface shared with the 11 device and hand that to <c>D3DImage</c>, which is a true WPF
    /// <c>ImageSource</c> and composites like any other element. It was rejected here for three
    /// reasons. It drags in a whole second graphics API and its device-lost handling for nothing
    /// but the bridge; its front buffer is not dependably available in exactly the remote sessions
    /// the CPU fallback already exists for; and <b>this application has already accepted the
    /// trade</b> - the Live tab's video is a <c>VideoView</c>, which is itself a child window
    /// painting over WPF content. A dewarped pane sits in the same sort of place as that video.
    /// </para>
    /// <para>
    /// The cost of the choice is airspace: WPF content cannot be drawn on top of this pane, so
    /// overlays belong beside it rather than over it. The gain is that the pane never leaves the
    /// adapter. Reading it back instead would add about as much again as the draw and the upload
    /// together, on the CPU, for a picture that is already in the right memory.
    /// </para>
    /// </remarks>
    public void AttachToWindow(IntPtr windowHandle, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        DetachFromWindow();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForHwnd(_device, windowHandle,
            new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, width),
                Height = (uint)Math.Max(1, height),
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Vortice.DXGI.Usage.RenderTargetOutput,
                // Two buffers and the flip model: the bitblt model it replaced copies the whole
                // back buffer through the compositor on every present.
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            });
        _swapWidth = Math.Max(1, width);
        _swapHeight = Math.Max(1, height);
    }

    /// <summary>Releases the window this renderer was drawing to, if any.</summary>
    public void DetachFromWindow()
    {
        ReleaseBackBuffer();
        if (_swapChain is null)
            return;
        _swapChain.Dispose();
        _swapChain = null;
        OutputWidth = 0;
        OutputHeight = 0;
    }

    /// <summary>True when a finished pane goes to a window rather than to a private texture.</summary>
    public bool AttachedToWindow => _swapChain is not null;

    /// <summary>
    /// True once <see cref="Upload"/> has put a frame on the adapter, so <see cref="RenderPane"/>
    /// can redraw it — for a drag or a resize — without the frame being handed over again.
    /// </summary>
    public bool HasFrame => _hasFrame;

    /// <summary>
    /// Shows the pane drawn by the last <see cref="RenderPane"/>. Only meaningful after
    /// <see cref="AttachToWindow"/>.
    /// </summary>
    /// <remarks>
    /// Call <see cref="CopyOutput"/> before this rather than after: the flip model leaves the back
    /// buffer's contents undefined once it has been presented.
    /// </remarks>
    public void Present()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_swapChain is null)
            return;
        // No vsync wait. A camera delivers frames on its own schedule and the compositor paces
        // what it shows; blocking here would only add latency to a live view.
        _swapChain.Present(0, PresentFlags.None);
        // The flip model rotates the buffers, so the texture and view acquired for this frame
        // belong to the frame that has just gone. They are re-acquired on the next draw.
        ReleaseBackBuffer();
    }

    /// <summary>
    /// Blocks until the adapter has finished the queued draw. Only for measurement: a timing
    /// number taken without it measures how fast commands can be written down, not how fast they
    /// run.
    /// </summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _context.Flush();
        // A one-pixel readback is the cheapest available fence: mapping for read cannot return
        // until everything writing that resource has retired.
        if (_target is null)
            return;
        EnsureReadback(OutputWidth, OutputHeight);
        _context.CopyResource(_readback!, _target);
        _context.Map(_readback!, 0, MapMode.Read);
        _context.Unmap(_readback!, 0);
    }

    private void Draw(DewarpFrameFormat format, bool bilinear, bool mipsAvailable)
    {
        var views = format == DewarpFrameFormat.Nv12
            ? new[] { _luma!.View, _chromaInterleaved!.View }
            : [_luma!.View, _chromaU!.View, _chromaV!.View];

        _context.OMSetRenderTargets(_targetView!);
        _context.RSSetViewport(0, 0, OutputWidth, OutputHeight);
        _context.RSSetState(null);
        _context.OMSetDepthStencilState(null);
        _context.OMSetBlendState(null);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(format == DewarpFrameFormat.Nv12
            ? _interleavedShader
            : _planarShader);
        _context.PSSetShaderResources(0, views);
        _context.PSSetSampler(0, (bilinear, mipsAvailable) switch
        {
            (true, true) => _trilinear,
            (true, false) => _trilinearTopLevel,
            (false, true) => _point,
            (false, false) => _pointTopLevel,
        });
        _context.PSSetConstantBuffer(0, _constants);
        _context.Draw(3, 0);

        // The shader resources and the render target are unbound before returning, so the next
        // frame's upload into the same textures is not fighting a live binding and the swap
        // chain's back buffer is not still referenced when the flip model rotates it. Direct3D
        // takes nulls here to mean "nothing bound" -- the null-forgiving operators are Vortice's
        // signatures being stricter than the API, not a nullability risk being waved away.
        _context.PSSetShaderResources(0, new ID3D11ShaderResourceView[views.Length]!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private void EnsureTarget(int width, int height)
    {
        if (_swapChain is not null)
        {
            EnsureSwapChainTarget(width, height);
            return;
        }
        if (_target is not null && OutputWidth == width && OutputHeight == height)
            return;

        _targetView?.Dispose();
        _target?.Dispose();
        _readback?.Dispose();
        _readback = null;

        _target = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _targetView = _device.CreateRenderTargetView(_target);
        OutputWidth = width;
        OutputHeight = height;
    }

    /// <summary>
    /// Resizes the swap chain if the pane has changed size, then takes the current back buffer as
    /// the render target.
    /// </summary>
    /// <remarks>
    /// The buffer is re-acquired for every frame because the flip model rotates them, so a cached
    /// render target view would be aimed at the buffer already on screen. Creating a view costs a
    /// few microseconds against a draw measured in tens of them.
    /// </remarks>
    private void EnsureSwapChainTarget(int width, int height)
    {
        if (width != _swapWidth || height != _swapHeight)
        {
            ReleaseBackBuffer();
            _readback?.Dispose();
            _readback = null;
            _swapChain!.ResizeBuffers(2, (uint)width, (uint)height,
                Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            _swapWidth = width;
            _swapHeight = height;
        }
        OutputWidth = _swapWidth;
        OutputHeight = _swapHeight;
        if (_targetView is not null)
            return;
        _target = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _targetView = _device.CreateRenderTargetView(_target);
    }

    private void ReleaseBackBuffer()
    {
        _targetView?.Dispose();
        _targetView = null;
        if (_swapChain is null)
            return;
        // Only a swap chain's buffer is borrowed per frame; a private target is kept.
        _target?.Dispose();
        _target = null;
    }

    private void EnsureReadback(int width, int height)
    {
        if (_readback is not null)
        {
            var existing = _readback.Description;
            if (existing.Width == (uint)width && existing.Height == (uint)height)
                return;
            _readback.Dispose();
        }
        _readback = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private void EnsurePlanes(in DewarpFrame frame)
    {
        if (_luma is not null && _frameWidth == frame.Width && _frameHeight == frame.Height &&
            _frameFormat == frame.Format)
            return;

        _luma?.Dispose();
        _chromaInterleaved?.Dispose();
        _chromaU?.Dispose();
        _chromaV?.Dispose();
        _chromaInterleaved = null;
        _chromaU = null;
        _chromaV = null;

        int chromaWidth = (frame.Width + 1) / 2;
        int chromaHeight = (frame.Height + 1) / 2;
        _luma = PlaneTexture.Create(_device, frame.Width, frame.Height, Format.R8_UNorm);
        if (frame.Format == DewarpFrameFormat.Nv12)
        {
            _chromaInterleaved = PlaneTexture.Create(
                _device, chromaWidth, chromaHeight, Format.R8G8_UNorm);
        }
        else
        {
            _chromaU = PlaneTexture.Create(_device, chromaWidth, chromaHeight, Format.R8_UNorm);
            _chromaV = PlaneTexture.Create(_device, chromaWidth, chromaHeight, Format.R8_UNorm);
        }

        _frameWidth = frame.Width;
        _frameHeight = frame.Height;
        _frameFormat = frame.Format;
    }

    private void UploadPlanes(in DewarpFrame frame)
    {
        Upload(_luma!, frame.YPlane, frame.YPitch);
        if (frame.Format == DewarpFrameFormat.Nv12)
        {
            Upload(_chromaInterleaved!, frame.UvPlane!, frame.ChromaPitch);
        }
        else
        {
            Upload(_chromaU!, frame.UPlane!, frame.ChromaPitch);
            Upload(_chromaV!, frame.VPlane!, frame.ChromaPitch);
        }
    }

    /// <summary>
    /// Writes one plane into mip 0 and rebuilds the chain above it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pinned <see cref="GCHandle"/> rather than a span overload, because the source row pitch
    /// has to be honoured: a decoder's plane is regularly wider than the picture, and passing a
    /// packed span would shear the image by a pixel per row. Pinning for the duration of one
    /// <c>UpdateSubresource</c> is a few nanoseconds and keeps this project free of
    /// <c>unsafe</c>, matching the two P/Invoke vendor projects.
    /// </para>
    /// <para>
    /// The chain is rebuilt unconditionally rather than only when the view minifies. Deciding
    /// would mean predicting the largest footprint anywhere in the pane, and a coarse prediction
    /// that missed a minifying corner would sample a mip level that was never written — stale
    /// pixels from an earlier frame, which is a far worse failure than the fraction of a
    /// millisecond this costs.
    /// </para>
    /// </remarks>
    private void Upload(PlaneTexture plane, byte[] source, int pitch)
    {
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(plane.Texture, 0, null,
                handle.AddrOfPinnedObject(), (uint)pitch, 0);
        }
        finally
        {
            handle.Free();
        }
    }

    private static SamplerDescription Sampler(Filter filter, float maxLod = float.MaxValue) => new()
    {
        Filter = filter,
        AddressU = TextureAddressMode.Clamp,
        AddressV = TextureAddressMode.Clamp,
        AddressW = TextureAddressMode.Clamp,
        ComparisonFunc = ComparisonFunction.Never,
        MinLOD = 0,
        MaxLOD = maxLod,
        MaxAnisotropy = 1,
    };

    private static string LoadShaderSource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("Dewarp.hlsl", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static ReadOnlyMemory<byte> Compile(
        string source, string entryPoint, string profile, bool planarChroma)
    {
        var defines = planarChroma
            ? new[] { new ShaderMacro("CHROMA_PLANAR", "1") }
            : [];
        // No include handler: this shader includes nothing, and passing null is how you say so.
        var result = Compiler.Compile(source, defines, null!, entryPoint, "Dewarp.hlsl", profile,
            ShaderFlags.OptimizationLevel3, out var blob, out var errors);
        using (blob)
        using (errors)
        {
            if (result.Failure || blob is null)
                throw new InvalidOperationException(
                    $"The dewarp shader ({entryPoint}, {profile}) did not compile: " +
                    (errors?.AsString() ?? result.Description));
            return blob.AsMemory();
        }
    }

    /// <summary>
    /// One video plane as a mip-chained texture, with the view the shader binds and
    /// <c>GenerateMips</c> writes through.
    /// </summary>
    /// <remarks>
    /// <c>MipLevels = 0</c> asks Direct3D for the full chain, and the render-target bind flag is
    /// not decoration: <c>GenerateMips</c> refuses a texture that cannot be drawn into, since
    /// that is how it builds the levels.
    /// </remarks>
    private sealed class PlaneTexture : IDisposable
    {
        private PlaneTexture(ID3D11Texture2D texture, ID3D11ShaderResourceView view)
        {
            Texture = texture;
            View = view;
        }

        public ID3D11Texture2D Texture { get; }

        public ID3D11ShaderResourceView View { get; }

        public static PlaneTexture Create(ID3D11Device device, int width, int height, Format format)
        {
            var texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Math.Max(1, width),
                Height = (uint)Math.Max(1, height),
                MipLevels = 0,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            try
            {
                return new PlaneTexture(texture, device.CreateShaderResourceView(texture));
            }
            catch
            {
                texture.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }
}
