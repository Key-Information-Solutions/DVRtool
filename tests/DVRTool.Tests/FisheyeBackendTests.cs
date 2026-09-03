using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Which renderer a machine gets, and the CPU renderer that owns the buffers.
/// </summary>
/// <remarks>
/// The policy is worth testing on its own because it is the one piece of the accelerated path
/// that has to be right on machines this code will never run on. Every rule it applies is about
/// whether the finished pane can be <i>presented</i>, not about how fast the arithmetic is, so
/// each case below names the configuration it stands for.
/// </remarks>
public class FisheyeBackendTests
{
    private static DewarpGpuCapability Workstation => new(
        DeviceCreated: true, IsHardwareAdapter: true,
        AdapterDescription: "NVIDIA GeForce RTX 5090",
        IsRemoteSession: false, WpfRenderTier: 2, FailureReason: null);

    // ---- The policy --------------------------------------------------------------------

    [Fact]
    public void AnOrdinaryWorkstationGetsTheGpu()
    {
        // The case the whole design is aimed at, and the reason the default is Auto rather than
        // Cpu: an operator at a desk is the rule, not the exception.
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Auto, Workstation);
        Assert.Equal(DewarpBackend.Gpu, choice.Backend);
        Assert.Contains("RTX 5090", choice.Reason);
    }

    [Fact]
    public void ARemoteSessionGetsTheCpu()
    {
        // Hardware is present and the shader would run; the shared surface the pane reaches WPF
        // through is what is not dependable here. The reason has to say "remote desktop", because
        // that is the fact the operator can check.
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Auto,
            Workstation with { IsRemoteSession = true });
        Assert.Equal(DewarpBackend.Cpu, choice.Backend);
        Assert.Contains("remote-desktop", choice.Reason);
    }

    [Fact]
    public void SoftwareCompositingGetsTheCpu()
    {
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Auto,
            Workstation with { WpfRenderTier = 0 });
        Assert.Equal(DewarpBackend.Cpu, choice.Backend);
        Assert.Contains("software", choice.Reason);
    }

    [Fact]
    public void AWarpAdapterGetsTheCpu()
    {
        // The trap this rule exists for: a device is created successfully and reports a
        // plausible name, so "did D3D11 initialise?" answers yes while the rasterizer is
        // software and slower than the parallel CPU renderer it would be replacing.
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Auto,
            Workstation with
            {
                IsHardwareAdapter = false,
                AdapterDescription = "Microsoft Basic Render Driver",
            });
        Assert.Equal(DewarpBackend.Cpu, choice.Backend);
        Assert.Contains("Microsoft Basic Render Driver", choice.Reason);
    }

    [Fact]
    public void NoDeviceGetsTheCpuAndKeepsTheReason()
    {
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Auto,
            DewarpGpuCapability.None("d3d11.dll could not be loaded."));
        Assert.Equal(DewarpBackend.Cpu, choice.Backend);
        Assert.Contains("d3d11.dll", choice.Reason);
    }

    [Fact]
    public void AskingForTheGpuOverridesEveryPresentationRule()
    {
        // Deliberate: all three Auto declines are judgements about a configuration this code
        // cannot see the screen of. An operator who can, and who says "use the GPU", gets it.
        var awkward = Workstation with
        {
            IsRemoteSession = true, WpfRenderTier = 0, IsHardwareAdapter = false,
        };
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Gpu, awkward);
        Assert.Equal(DewarpBackend.Gpu, choice.Backend);
    }

    [Fact]
    public void AskingForTheGpuStillCannotConjureADevice()
    {
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Gpu,
            DewarpGpuCapability.None("This build of Windows has no Direct3D 11."));
        Assert.Equal(DewarpBackend.Cpu, choice.Backend);
        Assert.Contains("Direct3D 11", choice.Reason);
    }

    [Fact]
    public void AskingForTheCpuIsHonouredOnTheBestHardware()
    {
        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Cpu, Workstation);
        Assert.Equal(DewarpBackend.Cpu, choice.Backend);
        Assert.Contains("settings", choice.Reason);
    }

    [Fact]
    public void ARuntimeFailureDemotesWithTheCauseIntact()
    {
        var choice = DewarpBackendPolicy.DemoteToCpu("the display driver was reset.");
        Assert.Equal(DewarpBackend.Cpu, choice.Backend);
        Assert.Contains("display driver was reset", choice.Reason);
    }

    [Fact]
    public void EveryChoiceExplainsItself()
    {
        // "Why is this one on the CPU?" is the question that actually gets asked, and a blank
        // reason is how it becomes unanswerable.
        var capabilities = new[]
        {
            Workstation,
            Workstation with { IsRemoteSession = true },
            Workstation with { WpfRenderTier = 0 },
            Workstation with { IsHardwareAdapter = false },
            DewarpGpuCapability.None(string.Empty),
        };
        foreach (var preference in Enum.GetValues<DewarpBackendPreference>())
        {
            foreach (var capability in capabilities)
            {
                var choice = DewarpBackendPolicy.Choose(preference, capability);
                Assert.False(string.IsNullOrWhiteSpace(choice.Reason),
                    $"{preference} on {capability.AdapterDescription} explained nothing");
            }
        }
    }

    // ---- The CPU renderer --------------------------------------------------------------

    [Fact]
    public void RendererMatchesTheHandWiredPipeline()
    {
        // The renderer is orchestration, not arithmetic: its whole job is to call the three
        // tested pieces in the right order with the right buffers. So the claim worth pinning is
        // that it produces exactly what calling them by hand produces.
        var calibration = FisheyeCalibration.Default(512, 512);
        var view = Rect(yaw: 30, pitch: 20, fov: 80);
        var frame = SyntheticFrame(512, 512, DewarpFrameFormat.I420);
        var request = new DewarpRenderRequest(calibration, view, 160, 120);

        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, request);

        var expected = ByHand(frame, request);
        Assert.Equal(expected, renderer.Output.ToArray());
    }

    [Fact]
    public void Nv12AndI420AgreeThroughTheRenderer()
    {
        var calibration = FisheyeCalibration.Default(256, 256);
        var request = new DewarpRenderRequest(calibration, Rect(fov: 100), 96, 96);
        var planar = SyntheticFrame(256, 256, DewarpFrameFormat.I420);
        var interleaved = SyntheticFrame(256, 256, DewarpFrameFormat.Nv12);

        using var a = new CpuDewarpRenderer();
        using var b = new CpuDewarpRenderer();
        a.Render(planar, request);
        b.Render(interleaved, request);
        Assert.Equal(a.Output.ToArray(), b.Output.ToArray());
    }

    [Fact]
    public void ReusingTheRendererGivesTheSamePicture()
    {
        // The table and every buffer survive between frames, which is the point of the type. A
        // stale bound, a stale mip level or a half-overwritten scratch buffer would show up as
        // the second render differing from the first.
        var calibration = FisheyeCalibration.Default(512, 512);
        var request = new DewarpRenderRequest(calibration, Rect(fov: 120), 200, 150);
        var frame = SyntheticFrame(512, 512, DewarpFrameFormat.I420);

        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, request);
        var first = renderer.Output.ToArray();
        renderer.Render(frame, request);
        Assert.Equal(first, renderer.Output.ToArray());
    }

    [Fact]
    public void MovingTheViewAndComingBackGivesTheSamePicture()
    {
        // The cache key has to invalidate on the view and restore on the way back. Keying it on
        // something that never changes would pass the test above and fail this one.
        var calibration = FisheyeCalibration.Default(512, 512);
        var frame = SyntheticFrame(512, 512, DewarpFrameFormat.I420);
        var home = new DewarpRenderRequest(calibration, Rect(fov: 90), 128, 96);
        var away = new DewarpRenderRequest(calibration, Rect(yaw: 90, pitch: 50, fov: 30),
            128, 96);

        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, home);
        var expected = renderer.Output.ToArray();
        renderer.Render(frame, away);
        Assert.NotEqual(expected, renderer.Output.ToArray());
        renderer.Render(frame, home);
        Assert.Equal(expected, renderer.Output.ToArray());
    }

    [Fact]
    public void ChangingOnlyTheOutsideColourKeepsTheTable()
    {
        // Neither the outside colour nor bilinear enters the table, so neither may throw it away.
        // Observable only through the picture: the rim repaints, the geometry does not move.
        var calibration = FisheyeCalibration.Default(256, 256);
        var frame = SyntheticFrame(256, 256, DewarpFrameFormat.I420);
        // Two colours no converted pixel can be. Testing against opaque black instead would be
        // wrong, and quietly so: a source pixel at luma zero converts to opaque black as well, so
        // "every black pixel is outside the circle" is false on real footage too.
        const uint green = 0xFF00FF00, magenta = 0xFFFF00FF;
        var first = new DewarpRenderRequest(calibration, Rect(pitch: 85, fov: 120), 128, 96,
            OutsideColor: green);
        var second = first with { OutsideColor = magenta };

        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, first);
        var before = renderer.Output.ToArray();
        var bounds = renderer.LastBounds;
        renderer.Render(frame, second);
        var after = renderer.Output.ToArray();

        Assert.False(bounds.IsEmpty);
        // The table did not move, so the same pixels are outside; only their colour changed.
        Assert.Equal(bounds, renderer.LastBounds);
        int repainted = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == green)
            {
                Assert.Equal(magenta, after[i]);
                repainted++;
            }
            else
            {
                Assert.Equal(before[i], after[i]);
            }
        }
        Assert.True(repainted > 0, "the test view should look past the rim somewhere");
    }

    [Fact]
    public void APaneEntirelyOutsideTheCircleIsFilledNotLeftStale()
    {
        // A circle far off the frame converts nothing, so the fast path skips straight to the
        // fill. Without it the pane would keep whatever the previous frame left in the buffer.
        var calibration = FisheyeCalibration.Default(256, 256);
        var frame = SyntheticFrame(256, 256, DewarpFrameFormat.I420);
        var visible = new DewarpRenderRequest(calibration, Rect(), 64, 48);

        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, visible);
        Assert.NotEqual(DewarpSampler.OpaqueBlack, renderer.Output[0]);

        var narrow = calibration with { FieldOfViewDegrees = 1, RadiusX = 1 };
        renderer.Render(frame, new DewarpRenderRequest(narrow,
            new DewarpView(DewarpViewMode.Rectilinear, new ViewOrientation(0, 0, 0), 150),
            64, 48, OutsideColor: 0xFF00FF00));
        Assert.All(renderer.Output.ToArray(), pixel => Assert.Equal(0xFF00FF00u, pixel));
    }

    [Fact]
    public void CopyOutputHonoursAWiderStride()
    {
        // A WriteableBitmap's stride is its own business and is regularly wider than the pane.
        var calibration = FisheyeCalibration.Default(128, 128);
        var frame = SyntheticFrame(128, 128, DewarpFrameFormat.I420);
        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, new DewarpRenderRequest(calibration, Rect(), 20, 10));

        const int stride = 32;
        var destination = new uint[stride * 10];
        Array.Fill(destination, 0xDEADBEEF);
        renderer.CopyOutput(destination, stride);

        for (int row = 0; row < 10; row++)
        {
            for (int col = 0; col < 20; col++)
                Assert.Equal(renderer.Output[row * 20 + col], destination[row * stride + col]);
            // The padding between rows is not the renderer's to touch.
            for (int col = 20; col < stride; col++)
                Assert.Equal(0xDEADBEEFu, destination[row * stride + col]);
        }
    }

    [Fact]
    public void AGrowingPaneDoesNotCarryOverTheSmallerOne()
    {
        var calibration = FisheyeCalibration.Default(256, 256);
        var frame = SyntheticFrame(256, 256, DewarpFrameFormat.I420);
        using var renderer = new CpuDewarpRenderer();

        renderer.Render(frame, new DewarpRenderRequest(calibration, Rect(), 64, 48));
        Assert.Equal(64 * 48, renderer.Output.Length);
        renderer.Render(frame, new DewarpRenderRequest(calibration, Rect(), 200, 150));
        Assert.Equal(200 * 150, renderer.Output.Length);
        Assert.Equal(200, renderer.OutputWidth);
        Assert.Equal(150, renderer.OutputHeight);

        // And the same pane again after shrinking, to prove the buffer is reused rather than
        // reallocated into a different shape.
        renderer.Render(frame, new DewarpRenderRequest(calibration, Rect(), 64, 48));
        Assert.Equal(64 * 48, renderer.Output.Length);
    }

    [Fact]
    public void TheRendererMinifiesAWideViewJustAsTheTableSays()
    {
        var calibration = FisheyeCalibration.Default(2560, 2560);
        var view = new DewarpView(DewarpViewMode.Panorama360, ViewOrientation.Center, 360);
        var frame = SyntheticFrame(2560, 2560, DewarpFrameFormat.Nv12);
        var request = new DewarpRenderRequest(calibration, view, 1200, 300);

        using var renderer = new CpuDewarpRenderer();
        renderer.Render(frame, request);

        var map = DewarpMap.Build(calibration, view, 1200, 300, 2560, 2560);
        Assert.Equal(map.MipLevel, renderer.MipLevel);
        Assert.True(renderer.MipLevel > 0, "a 360 degree unroll into 1200x300 should halve");
        Assert.Equal(map.Bounds, renderer.LastBounds);
    }

    [Fact]
    public void EveryMipDepthSurvivesThePingPong()
    {
        // Levels 1 and 3 read the first scratch buffer and level 2 the second, so an off-by-one
        // in the alternation only breaks at one particular depth. Walking all four against the
        // hand-wired pipeline covers each of them.
        var calibration = FisheyeCalibration.Default(2560, 2560);
        var frame = SyntheticFrame(2560, 2560, DewarpFrameFormat.I420);
        using var renderer = new CpuDewarpRenderer();

        // A 360 degree unroll of a 2560-pixel circle steps about 4000/paneWidth source pixels per
        // output pixel, and DewarpMap's thresholds are at 1.6, 3.2 and 6.4 — so these four widths
        // land one on each level, and the level is asserted rather than assumed so that a change
        // to those thresholds fails here instead of quietly narrowing the sweep.
        var seen = new HashSet<int>();
        foreach (var (width, height, level) in
            new[] { (3200, 800, 0), (1800, 450, 1), (1000, 250, 2), (400, 100, 3) })
        {
            var request = new DewarpRenderRequest(calibration,
                new DewarpView(DewarpViewMode.Panorama360, ViewOrientation.Center, 360),
                width, height);
            renderer.Render(frame, request);
            Assert.Equal(level, renderer.MipLevel);
            seen.Add(renderer.MipLevel);
            Assert.Equal(ByHand(frame, request), renderer.Output.ToArray());
        }
        Assert.Equal(4, seen.Count);
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static DewarpView Rect(double yaw = 0, double pitch = 0, double fov = 90) =>
        new(DewarpViewMode.Rectilinear, new ViewOrientation(yaw, pitch, 0), fov);

    /// <summary>
    /// The three tested pieces called by hand, in the order the renderer calls them. The
    /// reference the renderer is held against.
    /// </summary>
    private static uint[] ByHand(in DewarpFrame frame, in DewarpRenderRequest request)
    {
        var map = DewarpMap.Build(request.Calibration, request.View,
            request.OutputWidth, request.OutputHeight, frame.Width, frame.Height);
        var output = new uint[request.OutputWidth * request.OutputHeight];
        if (map.Bounds.IsEmpty)
        {
            Array.Fill(output, request.OutsideColor);
            return output;
        }

        var bounds = map.Bounds;
        var converted = new uint[bounds.PixelCount];
        if (frame.Format == DewarpFrameFormat.Nv12)
            DewarpSampler.ConvertNv12ToBgra(frame.YPlane, frame.YPitch, frame.UvPlane!,
                frame.ChromaPitch, bounds, frame.Range, converted, bounds.Width);
        else
            DewarpSampler.ConvertI420ToBgra(frame.YPlane, frame.YPitch, frame.UPlane!,
                frame.VPlane!, frame.ChromaPitch, bounds, frame.Range, converted, bounds.Width);

        var source = converted;
        int width = bounds.Width, height = bounds.Height;
        for (int level = 0; level < map.MipLevel; level++)
        {
            int halfWidth = (width + 1) / 2, halfHeight = (height + 1) / 2;
            var halved = new uint[halfWidth * halfHeight];
            DewarpSampler.BoxHalve(source, width, height, width, halved, halfWidth);
            source = halved;
            width = halfWidth;
            height = halfHeight;
        }

        DewarpSampler.Sample(source, width, height, width, bounds, map.MipLevel, map,
            output, request.OutputWidth, request.Bilinear, request.OutsideColor);
        return output;
    }

    /// <summary>
    /// A frame with structure in all three planes — a diagonal luma ramp and two chroma gradients
    /// running opposite ways — so a plane swap, a pitch mistake or a stale buffer changes the
    /// picture instead of hiding in flat colour.
    /// </summary>
    private static DewarpFrame SyntheticFrame(int width, int height, DewarpFrameFormat format)
    {
        int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
        var y = new byte[width * height];
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
                y[row * width + col] = (byte)((col * 3 + row * 5) % 256);

        if (format == DewarpFrameFormat.Nv12)
        {
            var uv = new byte[chromaWidth * 2 * chromaHeight];
            for (int row = 0; row < chromaHeight; row++)
            {
                for (int col = 0; col < chromaWidth; col++)
                {
                    uv[row * chromaWidth * 2 + col * 2] = (byte)((col * 7 + row) % 256);
                    uv[row * chromaWidth * 2 + col * 2 + 1] = (byte)(255 - (col + row * 3) % 256);
                }
            }
            return DewarpFrame.Nv12(width, height, y, width, uv, chromaWidth * 2);
        }

        var u = new byte[chromaWidth * chromaHeight];
        var v = new byte[chromaWidth * chromaHeight];
        for (int row = 0; row < chromaHeight; row++)
        {
            for (int col = 0; col < chromaWidth; col++)
            {
                u[row * chromaWidth + col] = (byte)((col * 7 + row) % 256);
                v[row * chromaWidth + col] = (byte)(255 - (col + row * 3) % 256);
            }
        }
        return DewarpFrame.I420(width, height, y, width, u, v, chromaWidth);
    }
}
