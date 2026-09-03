using DVRTool.Core;
using DVRTool.Render.D3D11;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The accelerated renderer, held against the CPU one on real hardware.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FisheyeShaderTests"/> proves the shader's <i>arithmetic</i> matches the tested CPU
/// geometry with no adapter in the room. What it cannot prove is anything between that arithmetic
/// and a pixel: the constant buffer arriving in the rows the HLSL declares, texel centres landing
/// on pixel centres, the chroma plane sampled at the right level, the channel order surviving a
/// B8G8R8A8 render target, and the rim of the circle going black instead of smearing along a
/// clamped texture edge. Every one of those is invisible to a unit test and obvious in a picture,
/// so the check here is the picture: render one frame through both renderers and compare.
/// </para>
/// <para>
/// <b>Every test skips itself where there is no usable adapter</b> rather than failing. The
/// fallback existing is the point — a machine that cannot run these gets the CPU renderer, whose
/// own tests already cover it.
/// </para>
/// <para>
/// <b>The tolerances are measured, not guessed, and the two renderers are not bit-identical on
/// purpose.</b> The CPU converts to BGRA and blends in 8-bit fixed point; the GPU blends the
/// planes in fp32 and converts after, which is the same answer for an affine map to within where
/// each one rounds. And the CPU takes chroma with nearest sampling where the GPU interpolates
/// it — a real difference, and a deliberate improvement. So the tight comparisons run on smooth
/// frames, where the measured mean difference is 0.10-0.13 of a level per channel and no pixel is
/// off by more than one, and a separate case bounds the pathological one.
/// </para>
/// </remarks>
public class Direct3DDewarpTests
{
    /// <summary>Whether this machine has an adapter worth comparing against. Probed once.</summary>
    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        var capability = Direct3DProbe.Probe();
        if (!capability.DeviceCreated)
            return capability.FailureReason ?? "no Direct3D 11 device";
        if (!capability.IsHardwareAdapter)
            return $"only a software adapter ({capability.AdapterDescription})";
        try
        {
            using var renderer = Direct3DDewarpRenderer.Create();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    });

    // ---- Agreement with the CPU renderer ----------------------------------------------

    [Fact]
    public void MatchesTheCpuRendererOnAGreyscaleFrame()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // The tightest isolation available: a smooth luma ramp, neutral chroma, and a magnifying
        // view so both renderers read the top mip level. What is under comparison is the geometry,
        // the sampling position and the luma conversion, and nothing else.
        // Measured on an RTX 5090: mean 0.13, worst 1.
        var difference = Compare(
            Frame(1024, 1024, chroma: Chroma.Neutral, luma: Luma.Smooth),
            Request(1024, new ViewOrientation(35, 25, 0), fov: 20, 480, 360));

        Assert.True(difference.Worst <= 2,
            $"worst channel differed by {difference.Worst} at {difference.WorstAt}");
        Assert.True(difference.Mean <= 0.3, $"mean difference was {difference.Mean:0.####}");
    }

    [Fact]
    public void MatchesTheCpuRendererOnAColourFrame()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // Chroma in play, varying as gently as real subsampled chroma does -- which is all real
        // chroma, since it is subsampled precisely because it varies gently. Nearest and
        // interpolated sampling therefore land in the same place and the two paths still agree to
        // within a level. A swapped U and V, or chroma read at the wrong mip, moves this by tens.
        // Measured: mean 0.10, worst 1.
        var difference = Compare(
            Frame(512, 512, chroma: Chroma.Smooth, luma: Luma.Smooth),
            Request(512, new ViewOrientation(200, 15, 0), fov: 30, 320, 240));

        Assert.True(difference.Worst <= 2,
            $"worst channel differed by {difference.Worst} at {difference.WorstAt}");
        Assert.True(difference.Mean <= 0.3, $"mean difference was {difference.Mean:0.####}");
    }

    [Fact]
    public void ASteppedLumaPatternStaysWithinTheSamplingPositionError()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // A pattern that steps 219 levels between adjacent pixels wherever its modulo wraps, so
        // it turns any disagreement about *where* to sample into a large disagreement about the
        // value. That is what makes it the sensitive test for the half-pixel and constant-buffer
        // mistakes, and also why its worst case is allowed to be several levels: the shader
        // constants are fp32, which puts the sampling position within about 0.02 of a pixel, and
        // 0.02 of a 219-level step is four levels. Measured: mean 0.28, worst 4.
        var difference = Compare(
            Frame(1024, 1024, chroma: Chroma.Neutral, luma: Luma.Stepped),
            Request(1024, new ViewOrientation(35, 25, 0), fov: 20, 480, 360));

        Assert.True(difference.Worst <= 6,
            $"worst channel differed by {difference.Worst} at {difference.WorstAt}");
        Assert.True(difference.Mean <= 0.6, $"mean difference was {difference.Mean:0.####}");
    }

    [Fact]
    public void ChromaFilteringIsTheOnlyLargeDivergence()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // The one case where the two renderers genuinely part company, recorded so it is a known
        // quantity rather than a surprise. Chroma stepping 170 levels between adjacent samples
        // cannot survive nearest-versus-interpolated sampling, and individual pixels are far
        // apart. The mean is still bounded, which is what rules out a channel swap -- that would
        // put this in the tens. Measured: mean 2.45, worst 148.
        var difference = Compare(
            Frame(1024, 1024, chroma: Chroma.Stepped, luma: Luma.Stepped),
            Request(1024, ViewOrientation.Center, fov: 40, 480, 360));

        Assert.True(difference.Mean <= 4, $"mean difference was {difference.Mean:0.####}");
    }

    // ---- The parts only hardware can answer -------------------------------------------

    [Fact]
    public void TheTwoChromaShaderPermutationsAgree()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // I420 and NV12 are two pixel shaders compiled from one file. They are the same picture,
        // so a divergence means one permutation's texture bindings are wrong -- and NV12 is what
        // a hardware decoder hands over, so that is not the rare path.
        var request = Request(512, ViewOrientation.Center, fov: 360, 640, 160,
            DewarpViewMode.Panorama360);
        using var renderer = Direct3DDewarpRenderer.Create();

        var planar = Gpu(renderer, Frame(512, 512, Chroma.Smooth, Luma.Stepped), request);
        var interleaved = Gpu(renderer,
            Frame(512, 512, Chroma.Smooth, Luma.Stepped, DewarpFrameFormat.Nv12), request);
        Assert.Equal(planar, interleaved);
    }

    [Fact]
    public void TheRimGoesBlackRatherThanSmearing()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // Two claims at once. The pane is aimed almost at the rim, so most of it looks past the
        // edge of the image circle: those pixels must be the outside colour rather than a clamped
        // copy of the last texel row, and they must be the *same* pixels on both renderers, which
        // is what pins the constant buffer's thetaMax and the shader's InsideFrame check together.
        const uint green = 0xFF00FF00;
        var request = Request(512, new ViewOrientation(0, 87, 0), fov: 130, 320, 240)
            with { OutsideColor = green };
        var frame = Frame(512, 512, Chroma.Neutral, Luma.Smooth);

        using var renderer = Direct3DDewarpRenderer.Create();
        var gpu = Gpu(renderer, frame, request);
        using var cpu = new CpuDewarpRenderer();
        cpu.Render(frame, request);

        int outsideOnCpu = 0, outsideOnGpu = 0, disagreed = 0;
        for (int i = 0; i < gpu.Length; i++)
        {
            bool a = cpu.Output[i] == green;
            bool b = gpu[i] == green;
            if (a) outsideOnCpu++;
            if (b) outsideOnGpu++;
            if (a != b) disagreed++;
        }

        Assert.True(outsideOnCpu > gpu.Length / 10,
            "the test view should be substantially past the rim");
        // Only the boundary itself may differ, and it is a line a few hundred pixels long in a
        // pane of 76 800.
        Assert.True(disagreed < gpu.Length / 100,
            $"{disagreed} of {gpu.Length} pixels disagreed about being outside the circle " +
            $"(cpu {outsideOnCpu}, gpu {outsideOnGpu})");
    }

    [Fact]
    public void ZoomedOutPanesUseTheMipChain()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // The quality claim, made visible. A frame of alternating single-pixel columns has nothing
        // in it but the highest spatial frequency there is: sampled from the top mip level it
        // comes out as arbitrary noise, and filtered down the chain it converges on the average of
        // the two columns. So the same minifying pane, with the level forced to zero by a large
        // negative bias, should be dramatically noisier. Measured: spread 0.0 filtered against
        // 221.1 forced.
        //
        // Note the comparison is not against Bilinear = false. That switches the sampler from
        // linear to point, which still reads the mip chain -- it measures 0.0 as well, and using
        // it here would have made this test pass while proving nothing.
        var request = Request(1024, ViewOrientation.Center, fov: 360, 200, 60,
            DewarpViewMode.Panorama360);
        var frame = Frame(1024, 1024, Chroma.Neutral, Luma.Striped);

        using var renderer = Direct3DDewarpRenderer.Create();
        double filtered = Spread(Gpu(renderer, frame, request));
        double forced = Spread(Gpu(renderer, frame, request with { LodBias = -50 }));

        Assert.True(forced > 100, $"the forced-to-top pane should alias badly, spread {forced:0.#}");
        Assert.True(filtered * 20 < forced,
            $"filtered spread {filtered:0.##} is not far below forced {forced:0.#} -- is the " +
            "mip chain being sampled at all?");
    }

    [Fact]
    public void OnePaneNeedingMipsDoesNotDisturbOneThatDoesNot()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // The mip chain is built lazily, by the first pane of a frame that actually minifies, and
        // panes that do not are drawn through a sampler clamped to the top level. So one frame can
        // be drawn both ways, and the state has to be picked per pane rather than left behind by
        // the previous one: a magnifying pane rendered before and after a minifying one must come
        // out identical both times.
        var frame = Frame(1024, 1024, Chroma.Smooth, Luma.Stepped);
        var magnifying = Request(1024, new ViewOrientation(20, 30, 0), fov: 10, 400, 300);
        var minifying = Request(1024, ViewOrientation.Center, fov: 360, 400, 120,
            DewarpViewMode.Panorama360);

        using var renderer = Direct3DDewarpRenderer.Create();
        renderer.Upload(frame);

        renderer.RenderPane(magnifying);
        var before = Read(renderer);
        renderer.RenderPane(minifying);
        renderer.RenderPane(magnifying);
        var after = Read(renderer);

        Assert.Equal(before, after);
        // And it is a real picture either way, not a uniform pane that would match itself
        // trivially.
        Assert.True(before.Distinct().Count() > 100);
    }

    [Fact]
    public void RenderPaneRefusesBeforeAFrameArrives()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // Splitting upload from draw means there is now a state where the renderer has no frame.
        // Drawing then would sample textures that do not exist yet, so it says so instead.
        using var renderer = Direct3DDewarpRenderer.Create();
        Assert.Throws<InvalidOperationException>(() =>
            renderer.RenderPane(Request(512, ViewOrientation.Center, fov: 90, 64, 48)));
    }

    [Fact]
    public void EveryViewModeRendersSomething()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // A smoke test over the shader's two branches and all three mounts. A constant-buffer row
        // in the wrong place tends to produce a blank or uniform pane everywhere, which is easy to
        // miss when only one mode is exercised.
        var frame = Frame(512, 512, Chroma.Smooth, Luma.Stepped, DewarpFrameFormat.Nv12);
        using var renderer = Direct3DDewarpRenderer.Create();

        foreach (var mount in Enum.GetValues<FisheyeMount>())
        {
            var calibration = FisheyeCalibration.Default(512, 512) with { Mount = mount };
            foreach (var mode in new[]
            {
                DewarpViewMode.Rectilinear, DewarpViewMode.Panorama180, DewarpViewMode.Panorama360,
            })
            {
                var pane = Gpu(renderer, frame, new DewarpRenderRequest(
                    calibration, DewarpView.DefaultFor(mount, mode), 256, 192));
                Assert.True(pane.Distinct().Count() > 100,
                    $"{mount}/{mode} produced a near-uniform pane");
            }
        }
    }

    [Fact]
    public void QuadPanesAreTheRectilinearPrimitive()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // Quad is a composition of rectilinear views rather than its own projection, on the GPU
        // exactly as on the CPU. The claim is that all four aims come out the same on both
        // backends -- worth making again here because the composition happens above the renderer
        // and could drift on one side only, and because a yaw of 10 degrees plus i*90 is where a
        // sign error in the mount roll would show up on three panes out of four.
        //
        // Magnifying, and smooth: at Quad's own default of 90 degrees into a 160x120 pane the view
        // minifies, and the two renderers then differ by design rather than by mistake -- the GPU
        // picks a fractional mip level per pixel where the CPU box-halves the whole pane once.
        // That was measured at 134 levels apart on stepped content, which is the difference doing
        // its job, not a fault. Filtering is tested on its own above; this test is about aim.
        var calibration = FisheyeCalibration.Default(512, 512);
        var quad = new DewarpView(DewarpViewMode.Quad, new ViewOrientation(10, 45, 0), 30);
        var frame = Frame(512, 512, Chroma.Neutral, Luma.Smooth);
        using var renderer = Direct3DDewarpRenderer.Create();
        using var cpu = new CpuDewarpRenderer();

        foreach (var (view, _, _) in quad.Panes)
        {
            var request = new DewarpRenderRequest(calibration, view, 320, 240);
            var pane = Gpu(renderer, frame, request);
            cpu.Render(frame, request);
            Assert.Equal(0, cpu.MipLevel);
            var difference = Measure(cpu.Output, pane, request.OutputWidth);
            Assert.True(difference.Worst <= 2,
                $"quad pane at yaw {view.Orientation.YawDegrees:0} differed by " +
                $"{difference.Worst} at {difference.WorstAt}");
        }
    }

    [Fact]
    public void FleetResolutionRendersAndReuses()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        // Site C's actual main stream, and the size the whole optimization argument is about: 6.6
        // Mpx of source through a 1600x900 pane. Rendered twice through one renderer, so the
        // texture reuse is exercised rather than only first-frame allocation.
        var frame = Frame(2560, 2560, Chroma.Smooth, Luma.Stepped, DewarpFrameFormat.Nv12);
        var request = Request(2560, ViewOrientation.Center, fov: 360, 1600, 900,
            DewarpViewMode.Panorama360);

        using var renderer = Direct3DDewarpRenderer.Create();
        var first = Gpu(renderer, frame, request);
        var second = Gpu(renderer, frame, request);
        Assert.Equal(first, second);
        Assert.Equal(1600, renderer.OutputWidth);
        Assert.Equal(900, renderer.OutputHeight);
    }

    [Fact]
    public void ResizingThePaneKeepsWorking()
    {
        if (Skip(out string? why)) { Assert.NotNull(why); return; }

        var frame = Frame(512, 512, Chroma.Neutral, Luma.Smooth);
        using var renderer = Direct3DDewarpRenderer.Create();

        // Growing, shrinking, square and awkwardly odd, through one renderer: the target and the
        // readback staging texture both have to be rebuilt, and an odd pane is where a row-pitch
        // assumption in the readback would show up.
        foreach (var (width, height) in new[] { (64, 48), (800, 600), (200, 200), (33, 17) })
        {
            var pane = Gpu(renderer, frame,
                Request(512, ViewOrientation.Center, fov: 90, width, height));
            Assert.Equal(width * height, pane.Length);
            Assert.Equal(width, renderer.OutputWidth);
            Assert.Equal(height, renderer.OutputHeight);
            Assert.Contains(pane, pixel => pixel != pane[0]);
        }
    }

    [Fact]
    public void TheProbeDescribesThisMachine()
    {
        // Runs everywhere, including on a headless agent: either way the probe has to come back
        // with something the policy and an operator can read, rather than throwing on the way to
        // a camera list.
        var capability = Direct3DProbe.Probe(wpfRenderTier: 2);
        if (capability.DeviceCreated)
            Assert.False(string.IsNullOrWhiteSpace(capability.AdapterDescription),
                "a created device reported no adapter name");
        else
            Assert.False(string.IsNullOrWhiteSpace(capability.FailureReason),
                "a failed probe gave no reason");

        var choice = DewarpBackendPolicy.Choose(DewarpBackendPreference.Auto, capability);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static bool Skip(out string? reason)
    {
        reason = Unavailable.Value;
        return reason is not null;
    }

    private static DewarpRenderRequest Request(int source, ViewOrientation orientation,
        double fov, int width, int height, DewarpViewMode mode = DewarpViewMode.Rectilinear) =>
        new(FisheyeCalibration.Default(source, source),
            new DewarpView(mode, orientation, fov), width, height);

    private static uint[] Gpu(Direct3DDewarpRenderer renderer, in DewarpFrame frame,
        in DewarpRenderRequest request)
    {
        renderer.Render(frame, request);
        return Read(renderer);
    }

    /// <summary>The last pane, read back off the adapter.</summary>
    private static uint[] Read(Direct3DDewarpRenderer renderer)
    {
        var pane = new uint[renderer.OutputWidth * renderer.OutputHeight];
        renderer.CopyOutput(pane, renderer.OutputWidth);
        return pane;
    }

    private readonly record struct Difference(int Worst, double Mean, string WorstAt);

    /// <summary>Renders one frame through both renderers and measures how far apart they are.</summary>
    private static Difference Compare(in DewarpFrame frame, in DewarpRenderRequest request)
    {
        using var renderer = Direct3DDewarpRenderer.Create();
        var gpu = Gpu(renderer, frame, request);
        using var cpu = new CpuDewarpRenderer();
        cpu.Render(frame, request);
        return Measure(cpu.Output, gpu, request.OutputWidth);
    }

    /// <summary>Per-channel worst and mean difference between two panes.</summary>
    private static Difference Measure(ReadOnlySpan<uint> expected, uint[] actual, int width)
    {
        int worst = 0;
        long total = 0;
        string worstAt = "nowhere";
        for (int i = 0; i < actual.Length; i++)
        {
            uint a = expected[i], b = actual[i];
            // Blue, green and red. Alpha is opaque on both paths by construction and would only
            // dilute the mean.
            for (int shift = 0; shift < 24; shift += 8)
            {
                int difference = Math.Abs(
                    (int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF));
                total += difference;
                if (difference > worst)
                {
                    worst = difference;
                    worstAt = $"({i % width},{i / width}) cpu {a:X8} gpu {b:X8}";
                }
            }
        }
        return new Difference(worst, total / (double)(actual.Length * 3), worstAt);
    }

    /// <summary>
    /// Standard deviation of each pixel's channel sum: how much variation a pane carries, which is
    /// what tells filtered content from aliased content.
    /// </summary>
    private static double Spread(uint[] pane)
    {
        double sum = 0, sumOfSquares = 0;
        foreach (uint pixel in pane)
        {
            double value = ((pixel >> 16) & 0xFF) + ((pixel >> 8) & 0xFF) + (pixel & 0xFF);
            sum += value;
            sumOfSquares += value * value;
        }
        double mean = sum / pane.Length;
        return Math.Sqrt(Math.Max(0, sumOfSquares / pane.Length - mean * mean));
    }

    /// <summary>What the luma plane of a test frame carries.</summary>
    private enum Luma
    {
        /// <summary>A gentle diagonal ramp. The realistic case, and the tight comparison.</summary>
        Smooth,

        /// <summary>
        /// A ramp that wraps, stepping 219 levels between adjacent pixels. Turns a small sampling
        /// -position error into a large value error, which is what makes it sensitive.
        /// </summary>
        Stepped,

        /// <summary>Alternating single-pixel columns: nothing but the top spatial frequency.</summary>
        Striped,
    }

    /// <summary>What the chroma planes of a test frame carry.</summary>
    private enum Chroma
    {
        /// <summary>128 everywhere, so a comparison sees only geometry and luma.</summary>
        Neutral,

        /// <summary>Gentle gradients, as real subsampled chroma has.</summary>
        Smooth,

        /// <summary>Wrapping gradients stepping 170 levels, which no real footage does.</summary>
        Stepped,
    }

    private static DewarpFrame Frame(int width, int height, Chroma chroma, Luma luma,
        DewarpFrameFormat format = DewarpFrameFormat.I420)
    {
        int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
        var y = new byte[width * height];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                y[row * width + col] = luma switch
                {
                    Luma.Smooth => (byte)(16 + 219.0 * (col + row) / (width + height)),
                    Luma.Striped => (byte)(col % 2 == 0 ? 16 : 235),
                    _ => (byte)(16 + (col * 3 + row * 5) % 220),
                };
            }
        }

        byte U(int col, int row) => chroma switch
        {
            Chroma.Neutral => 128,
            Chroma.Smooth => (byte)(112 + 16.0 * col / chromaWidth),
            _ => (byte)(40 + (col * 5 + row) % 170),
        };
        byte V(int col, int row) => chroma switch
        {
            Chroma.Neutral => 128,
            Chroma.Smooth => (byte)(144 - 16.0 * row / chromaHeight),
            _ => (byte)(220 - (col + row * 7) % 170),
        };

        if (format == DewarpFrameFormat.Nv12)
        {
            var uv = new byte[chromaWidth * 2 * chromaHeight];
            for (int row = 0; row < chromaHeight; row++)
            {
                for (int col = 0; col < chromaWidth; col++)
                {
                    uv[row * chromaWidth * 2 + col * 2] = U(col, row);
                    uv[row * chromaWidth * 2 + col * 2 + 1] = V(col, row);
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
                u[row * chromaWidth + col] = U(col, row);
                v[row * chromaWidth + col] = V(col, row);
            }
        }
        return DewarpFrame.I420(width, height, y, width, u, v, chromaWidth);
    }
}
