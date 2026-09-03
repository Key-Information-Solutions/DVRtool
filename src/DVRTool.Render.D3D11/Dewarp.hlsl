// One fisheye view, dewarped.
//
// This file is the twin of DewarpShaderConstants in DVRTool.Core, and the two are meant to be
// read side by side. Everything below is a transcription of that type's RayFor / SourceForRay /
// LodFor, which the tests in FisheyeShaderTests hold against the DewarpGeometry the CPU renderer
// uses -- across every lens law, mount and view mode, to within 0.02 source pixels. Edit one
// without the other and the accelerated pane goes subtly wrong in a way no test catches and no
// screenshot explains, so: if you change the arithmetic here, change it there in the same commit.
//
// There is no sample table. DewarpMap exists because this trigonometry costs 6-15 ms to tabulate
// on a CPU and the view only moves when the operator moves it; here it is about thirty flops in a
// pixel shader, so it is evaluated per pixel and a drag rebuilds nothing at all.
//
// Two permutations are compiled from this one file: CHROMA_PLANAR for I420's separate U and V
// planes, and without it for NV12's interleaved plane. One shader with a uniform branch would
// also work, but it would need dummy textures bound to the slots it does not read.

static const float Pi = 3.14159265358979f;

cbuffer DewarpConstants : register(b0)
{
    // The row layout is pinned by DewarpShaderConstants.WriteTo and by a test. Every row is a
    // full float4, including the three rotation rows: HLSL never straddles a float3 across a
    // 16-byte boundary, so a hand-packed layout and the compiler's idea of one are always a
    // single edit away from disagreeing.
    float4 Pane;      // output width, output height, source width, source height
    float4 Circle;    // centre x, centre y, focal length in pixels, max incidence angle
    float4 Sensor;    // ellipticity, cos(calibration roll), sin(calibration roll), mirrored
    float4 Window;    // tan(half fov), tan(half fov) * aspect, panorama half-span, view yaw
    float4 Modes;     // is a panorama, lens projection ordinal, lod bias, unused
    float4 Rot0;      // view rotation, row 0 (xyz; w unused)
    float4 Rot1;
    float4 Rot2;
    float4 Yuv0;      // luma scale, luma offset, Cr->R, Cb->G
    float4 Yuv1;      // Cr->G, Cb->B, unused, unused
    float4 Outside;   // rgba painted where the pane looks past the rim of the image circle
};

Texture2D<float> Luma : register(t0);
#ifdef CHROMA_PLANAR
Texture2D<float> ChromaU : register(t1);
Texture2D<float> ChromaV : register(t2);
#else
Texture2D<float2> Chroma : register(t1);
#endif
SamplerState Trilinear : register(s0);

struct Vertex
{
    float4 Position : SV_Position;
};

// One triangle covering the whole viewport, from the vertex index alone: no buffers, no input
// layout, and one fewer vertex than a quad. Vertices land at (-1,-1), (-1,3) and (3,-1), which
// contains the clip-space square.
Vertex VsMain(uint id : SV_VertexID)
{
    Vertex output;
    output.Position = float4(id == 2 ? 3.0f : -1.0f, id == 1 ? 3.0f : -1.0f, 0.0f, 1.0f);
    return output;
}

// LensModel.RadiusOverFocal, with the validity check written out because HLSL has no NaN to
// carry it. The ordinals are LensProjection's, which a test pins for exactly this reason.
float RadiusOverFocal(float theta, out bool valid)
{
    int projection = (int)Modes.y;
    valid = theta >= 0.0f;
    if (projection == 0)                        // Equidistant
    {
        valid = valid && theta <= Pi;
        return theta;
    }
    if (projection == 1)                        // Stereographic
    {
        valid = valid && theta <= Pi;
        return 2.0f * tan(theta * 0.5f);
    }
    if (projection == 2)                        // EquisolidAngle
    {
        valid = valid && theta <= Pi;
        return 2.0f * sin(theta * 0.5f);
    }
    if (projection == 3)                        // Orthographic; sin turns over at 90 degrees
    {
        valid = valid && theta <= Pi * 0.5f;
        return sin(theta);
    }
    valid = false;
    return 0.0f;
}

// The direction an output pixel looks along, in the lens frame.
//
// SV_Position.xy already arrives at the pixel centre, which is why nothing here adds a half
// pixel where the C# twin does. That is the one place the two texts legitimately differ, and a
// half-pixel disagreement between them is precisely what ships as "the accelerated view looks
// slightly soft".
float3 RayFor(float2 pixel)
{
    float2 halfSize = Pane.xy * 0.5f;
    float2 ndc = (pixel - halfSize) / halfSize;
    if (Sensor.w != 0.0f)
        ndc.x = -ndc.x;                         // a floor mount sees the scene handed the other way

    if (Modes.x != 0.0f)
    {
        // An equirectangular unroll: azimuth across, incidence angle down, so the rim of the
        // circle is the top of the strip.
        float phi = Window.w + ndc.x * Window.z;
        float theta = Circle.w * (1.0f - pixel.y / Pane.y);
        float sinTheta = sin(theta);
        return float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cos(theta));
    }

    // A flat perspective window: the pixel on an image plane at unit distance, rotated into the
    // lens frame. (Named norm, not length: a local called length would shadow the intrinsic.)
    float2 plane = ndc * Window.xy;
    float norm = sqrt(plane.x * plane.x + plane.y * plane.y + 1.0f);
    float3 direction = float3(plane / norm, 1.0f / norm);
    return float3(dot(Rot0.xyz, direction),
                  dot(Rot1.xyz, direction),
                  dot(Rot2.xyz, direction));
}

// The source pixel a direction lands on, or false when the lens never saw it.
//
// Nothing about the encoded frame's extent enters here, deliberately: this is the exact twin of
// DewarpShaderConstants.SourceFor, and that is the function the parity test compares. The
// separate question of whether the coordinate is inside the frame at all belongs to the gather,
// and lives in InsideFrame below.
//
// Source coordinates are in the same convention as the CPU path: an integer coordinate is the
// centre of that source pixel, not its corner.
bool SourceFor(float2 pixel, out float2 source)
{
    source = float2(0.0f, 0.0f);
    float3 d = RayFor(pixel);

    float hypotenuse = sqrt(d.x * d.x + d.y * d.y);
    // atan2, not acos: a lens wider than 180 degrees sees directions with a negative z, and
    // atan2 reports those as an angle past 90 rather than losing the sign.
    float theta = atan2(hypotenuse, d.z);
    if (!(theta <= Circle.w))
        return false;

    bool valid;
    float radiusOverFocal = RadiusOverFocal(theta, valid);
    if (!valid || !isfinite(Circle.z))
        return false;
    float radius = Circle.z * radiusOverFocal;

    // Azimuth carried as a unit vector: on the optical axis both components are zero and atan2
    // would be arbitrary, but the radius is zero there too, so the product is the centre.
    float2 unit = hypotenuse > 0.0f ? d.xy / hypotenuse : float2(0.0f, 0.0f);
    float2 offset = radius * unit;
    // Calibration roll first, in optical space; then ellipticity, in sensor space.
    float2 rolled = float2(offset.x * Sensor.y - offset.y * Sensor.z,
                           offset.x * Sensor.z + offset.y * Sensor.y);
    source = float2(Circle.x + rolled.x, Circle.y + rolled.y * Sensor.x);
    return true;
}

// Whether a source coordinate is on the encoded frame at all.
//
// The twin of the bounds test in DewarpSampler.Bilinear, and needed for the same reason: an
// image circle may legitimately reach past the edge of the frame -- a full-frame fisheye's does
// -- and those pixels have no picture behind them. Without this the sampler's clamp address mode
// would answer with the nearest edge texel instead, smearing the last row of the frame outward
// along the rim. Half-open, matching the CPU's floor-then-compare.
bool InsideFrame(float2 source)
{
    return source.x >= 0.0f && source.y >= 0.0f
        && source.x < Pane.z && source.y < Pane.w;
}

// Which mip level to fetch from: the base-2 log of how far the source travels for one step
// across the pane.
//
// Three explicit taps, rather than the screen-space derivatives Sample() would apply for free.
// Those take their level from the coordinate handed to the neighbouring pixels of the 2x2 quad,
// and across the rim of the image circle one of those neighbours is outside -- its coordinate is
// whatever the branch left behind, the derivative explodes, the hardware picks the coarsest
// level, and the edge of the pane comes out a blurred halo one or two pixels thick. Evaluating
// the projection at the two neighbours costs about sixty more flops per pixel, which measures as
// nothing, and each answer is exact. A neighbour outside the circle is dropped, not used.
float LodFor(float2 pixel, float2 here)
{
    float footprint = 0.0f;
    float2 neighbour;
    if (SourceFor(pixel + float2(1.0f, 0.0f), neighbour))
        footprint = max(footprint, length(neighbour - here));
    if (SourceFor(pixel + float2(0.0f, 1.0f), neighbour))
        footprint = max(footprint, length(neighbour - here));
    return max(0.0f, (footprint > 1.0f ? log2(footprint) : 0.0f) + Modes.z);
}

float4 PsMain(float4 position : SV_Position) : SV_Target
{
    float2 here;
    if (!SourceFor(position.xy, here) || !InsideFrame(here))
        return Outside;

    float lod = LodFor(position.xy, here);

    // A texel centre sits at (index + 0.5) / size, and an integer source coordinate is a pixel
    // centre, so the half pixel goes on here and nowhere else.
    float2 uv = (here + 0.5f) / Pane.zw;
    float luma = Luma.SampleLevel(Trilinear, uv, lod).x;

    // The chroma plane is already half resolution, so the same footprint in source pixels is one
    // level further up its own chain. Sampling it at the luma level instead would double-blur
    // the colour -- invisible on most footage, wrong on a colour chart.
    float chromaLod = max(0.0f, lod - 1.0f);
#ifdef CHROMA_PLANAR
    float2 chroma = float2(ChromaU.SampleLevel(Trilinear, uv, chromaLod).x,
                           ChromaV.SampleLevel(Trilinear, uv, chromaLod).x);
#else
    float2 chroma = Chroma.SampleLevel(Trilinear, uv, chromaLod).xy;
#endif

    // Back to 0-255 to use YuvMatrix's coefficients unchanged: they are the same numbers the CPU
    // converter uses, read out of the same table, so neither path can drift in colour.
    float y = (luma * 255.0f - Yuv0.y) * Yuv0.x;
    float cb = chroma.x * 255.0f - 128.0f;
    float cr = chroma.y * 255.0f - 128.0f;
    float3 rgb = float3(y + Yuv0.z * cr,
                        y + Yuv0.w * cb + Yuv1.x * cr,
                        y + Yuv1.y * cb) / 255.0f;
    return float4(saturate(rgb), 1.0f);
}
