using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DVRTool.Core;
using DVRTool.Render.D3D11;

namespace DVRTool.App;

/// <summary>
/// One dewarped fisheye pane on screen. Hardware by default; the CPU renderer when this machine
/// cannot present hardware, or when it stops being able to mid-session.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where "the GUI defaults to hardware" actually happens.</b> The CPU renderer was
/// written first, which made it easy to think of acceleration as an option to be switched on. It
/// is the other way round: an operator sitting at a workstation is the ordinary case, and headless
/// or remote sessions are the exception. So <see cref="DewarpBackendPreference.Auto"/> is the
/// default, <see cref="DewarpBackendPolicy"/> answers hardware unless something about
/// <i>presentation</i> prevents it, and the CPU path is what catches the exceptions.
/// </para>
/// <para>
/// <b>Two different presentation mechanisms, and the control switches between them live.</b> The
/// accelerated pane is a child window with a DXGI swap chain — the picture never leaves the
/// adapter — and the CPU pane is an ordinary WPF <see cref="Image"/> over a
/// <see cref="WriteableBitmap"/>. They are not interchangeable at the WPF level, which is why this
/// control exists rather than a single element with a swappable renderer: demoting on a driver
/// reset has to replace the visual as well as the renderer, and it has to do it without the pane
/// going blank.
/// </para>
/// <para>
/// <b>The accelerated pane paints over WPF content.</b> A child window is not composited with the
/// rest of the tree, so anything meant to sit on top of the picture — an aim indicator, a compass,
/// a mode button — has to go beside it, not over it. That is the same constraint the Live tab's
/// <c>VideoView</c> already imposes, and accepting it again is what buys the zero-copy present;
/// see <see cref="Direct3DDewarpRenderer.AttachToWindow"/> for why the alternative was not taken.
/// </para>
/// <para>
/// <b><see cref="Present"/> must be called on the UI thread.</b> A decoder hands frames back on
/// its own thread, and neither WPF nor a Direct3D immediate context tolerates being driven from
/// two places, so marshalling is the frame source's job — as is deciding what to do with a frame
/// that arrives while the last one is still being drawn. That decision belongs with whoever owns
/// the decode buffers, and inventing a queue here before there is a frame source would be
/// guessing at its requirements.
/// </para>
/// </remarks>
public sealed class DewarpSurface : ContentControl, IDisposable
{
    private readonly Image _cpuImage = new()
    {
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private SwapChainHost? _gpuHost;
    private IDewarpRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private uint[] _cpuPixels = [];
    private DewarpGpuCapability? _capability;
    private DewarpBackendPreference _preference = DewarpBackendPreference.Auto;
    private bool _disposed;

    public DewarpSurface()
    {
        Background = Brushes.Black;
        ClipToBounds = true;
    }

    /// <summary>Raised when the renderer in use changes, including on a fallback mid-session.</summary>
    public event EventHandler? BackendChanged;

    /// <summary>The renderer currently drawing, or null until the first frame.</summary>
    public DewarpBackend? Backend => _renderer?.Backend;

    /// <summary>
    /// One sentence saying which renderer is in use and why, for a status line. "Why is this one
    /// on the CPU?" is the question that actually gets asked.
    /// </summary>
    public string BackendReason { get; private set; } = "No frame has been drawn yet.";

    /// <summary>The adapter's name when running on hardware, otherwise null.</summary>
    public string? AdapterDescription =>
        (_renderer as Direct3DDewarpRenderer)?.AdapterDescription;

    /// <summary>Where the pane is aimed and how wide it is.</summary>
    public DewarpView View { get; set; } =
        DewarpView.DefaultFor(FisheyeMount.Ceiling, DewarpViewMode.Rectilinear);

    /// <summary>The lens and image circle for the camera being shown.</summary>
    public FisheyeCalibration Calibration { get; set; } = FisheyeCalibration.Default(2560, 2560);

    /// <summary>What to paint outside the image circle, as BGRA.</summary>
    public uint OutsideColor { get; set; } = DewarpSampler.OpaqueBlack;

    /// <summary>
    /// Nudges the accelerated path's mip selection: negative sharper, positive softer. The CPU
    /// renderer ignores it, its levels being whole steps.
    /// </summary>
    public double LodBias { get; set; }

    /// <summary>
    /// Which renderer to use. Changing it tears down the current one, so the next frame is drawn
    /// by the new choice.
    /// </summary>
    public DewarpBackendPreference Preference
    {
        get => _preference;
        set
        {
            if (_preference == value)
                return;
            _preference = value;
            ReleaseRenderer();
        }
    }

    /// <summary>
    /// Draws one frame. Must be called on the UI thread; the frame's planes need only stay valid
    /// for the duration of the call.
    /// </summary>
    public void Present(in DewarpFrame frame)
    {
        if (_disposed)
            return;
        // The renderer first, because choosing it is what puts the hosted element in place, and
        // the pane's size is measured off that element rather than off this control.
        var renderer = EnsureRenderer();
        var (width, height) = PaneSizeInPixels();
        if (width <= 0 || height <= 0)
            return;

        var request = new DewarpRenderRequest(Calibration, View, width, height,
            Bilinear: true, OutsideColor, LodBias);

        if (renderer is Direct3DDewarpRenderer gpu)
        {
            // A swap chain needs the child window, which WPF creates when the control is first
            // realized -- so a pane in an unopened tab has nowhere to draw yet. Skipping is
            // right: there is nothing on screen to be stale.
            IntPtr handle = _gpuHost?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
                return;
            try
            {
                if (!gpu.AttachedToWindow)
                    gpu.AttachToWindow(handle, width, height);
                gpu.Render(frame, request);
                gpu.Present();
                return;
            }
            catch (Exception ex)
            {
                // A driver reset, a GPU swapped out from under us, or an operator connecting over
                // RDP to a session that was local a moment ago. The pane has to keep drawing, so
                // fall back for the rest of the session and redraw this same frame on the CPU
                // rather than dropping it.
                DemoteToCpu(ex);
                renderer = EnsureRenderer();
            }
        }

        renderer.Render(frame, request);
        BlitToBitmap(renderer, width, height);
    }

    /// <summary>Releases the renderer and, on the accelerated path, the child window.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseRenderer();
        _gpuHost?.Dispose();
        _gpuHost = null;
    }

    private IDewarpRenderer EnsureRenderer()
    {
        if (_renderer is not null)
            return _renderer;

        // Probed once per surface and then remembered: enumerating adapters and creating a
        // throwaway device is milliseconds, and nothing it reports changes while a pane is open
        // except by way of the failures DemoteToCpu already handles.
        _capability ??= Direct3DProbe.Probe(RenderCapability.Tier >> 16);
        var choice = DewarpBackendPolicy.Choose(_preference, _capability.Value);

        if (choice.Backend == DewarpBackend.Gpu)
        {
            try
            {
                var gpu = Direct3DDewarpRenderer.Create();
                _renderer = gpu;
                ShowGpuHost();
                SetReason(choice.Reason);
                return gpu;
            }
            catch (Exception ex)
            {
                // The probe created a device a moment ago, so this is a genuine surprise rather
                // than an expected configuration. Carry the message through: it is the only clue
                // anyone will get.
                choice = DewarpBackendPolicy.DemoteToCpu(TrimForSentence(ex.Message));
            }
        }

        _renderer = new CpuDewarpRenderer();
        ShowCpuImage();
        SetReason(choice.Reason);
        return _renderer;
    }

    private void DemoteToCpu(Exception failure)
    {
        ReleaseRenderer();
        _renderer = new CpuDewarpRenderer();
        ShowCpuImage();
        SetReason(DewarpBackendPolicy
            .DemoteToCpu(TrimForSentence(failure.Message)).Reason);
    }

    private void ReleaseRenderer()
    {
        if (_renderer is Direct3DDewarpRenderer gpu)
            gpu.DetachFromWindow();
        _renderer?.Dispose();
        _renderer = null;
    }

    private void ShowGpuHost()
    {
        _gpuHost ??= new SwapChainHost();
        if (ReferenceEquals(Content, _gpuHost))
            return;
        Content = _gpuHost;
        // Forced, because WPF would otherwise create the child window on the next layout pass and
        // the frame being drawn right now would have nowhere to go. That is invisible in a live
        // view, where another frame is 33 ms away, and it is the whole picture for a caller
        // presenting a single still.
        UpdateLayout();
    }

    private void ShowCpuImage()
    {
        if (ReferenceEquals(Content, _cpuImage))
            return;
        Content = _cpuImage;
        // Dropped rather than kept: unparenting an HwndHost destroys its window, so holding the
        // reference would leave an object whose handle is gone. A fresh one is built if the
        // operator asks for hardware again.
        _gpuHost = null;
    }

    private void BlitToBitmap(IDewarpRenderer renderer, int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            // 96 times the scale, so the bitmap lays out at exactly the pixel size it was
            // rendered at and WPF does not resample a picture that is already the right size.
            _bitmap = new WriteableBitmap(width, height,
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgra32, null);
            _cpuImage.Source = _bitmap;
        }
        int stride = _bitmap.BackBufferStride / 4;
        if (_cpuPixels.Length < stride * height)
            _cpuPixels = new uint[stride * height];

        renderer.CopyOutput(_cpuPixels, stride);
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _cpuPixels,
            _bitmap.BackBufferStride, 0);
    }

    /// <summary>
    /// The pane's size in device pixels, which is what both a swap chain and a bitmap want.
    /// </summary>
    /// <remarks>
    /// Rounded up rather than down, so a fractional layout size never leaves an unpainted seam at
    /// the right or bottom edge, and clamped because a 4K pane is already past the point where
    /// more pixels buy anything and a runaway layout should not ask for a gigabyte of buffers.
    /// </remarks>
    private (int Width, int Height) PaneSizeInPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        // The hosted element's size, not this control's, when there is one: a swap chain has to
        // match the child window exactly, and a consumer that sets Padding or a BorderThickness on
        // this control would otherwise get a picture scaled by the difference.
        FrameworkElement measured = _gpuHost is not null && ReferenceEquals(Content, _gpuHost)
            ? _gpuHost
            : this;
        int width = (int)Math.Ceiling(measured.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(measured.ActualHeight * dpi.DpiScaleY);
        return (Math.Clamp(width, 0, 7680), Math.Clamp(height, 0, 4320));
    }

    private void SetReason(string reason)
    {
        BackendReason = reason;
        BackendChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An exception message reshaped to sit inside "...because {this} Using the...".</summary>
    private static string TrimForSentence(string message)
    {
        string trimmed = message.Trim();
        if (trimmed.Length == 0)
            return "it stopped responding.";
        char first = char.ToLowerInvariant(trimmed[0]);
        string rest = trimmed[1..];
        return trimmed.EndsWith('.') ? first + rest : first + rest + ".";
    }

    /// <summary>
    /// The child window the swap chain presents into.
    /// </summary>
    /// <remarks>
    /// The predefined <c>static</c> window class rather than a registered one of our own: it needs
    /// no window procedure, no class registration to unwind, and it will not paint over the pane
    /// between presents. <c>WS_CLIPCHILDREN</c> and <c>WS_CLIPSIBLINGS</c> keep the compositor from
    /// touching the area DXGI owns.
    /// </remarks>
    private sealed class SwapChainHost : HwndHost
    {
        private const int WsChild = 0x40000000;
        private const int WsVisible = 0x10000000;
        private const int WsClipChildren = 0x02000000;
        private const int WsClipSiblings = 0x04000000;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            // HwndHost.Handle reports whatever is returned here, so there is no second field to
            // keep in step -- and it answers IntPtr.Zero before the control is realized, which is
            // exactly the "nowhere to draw yet" case Present checks for.
            IntPtr hwnd = CreateWindowEx(0, "static", null,
                WsChild | WsVisible | WsClipChildren | WsClipSiblings,
                0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    "The window for the accelerated dewarp pane could not be created " +
                    $"(error {Marshal.GetLastWin32Error()}).");
            return new HandleRef(this, hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int exStyle, string className, string? windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }
}
