using System.Runtime.InteropServices;
using DVRTool.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DVRTool.Render.D3D11;

/// <summary>
/// Finds out what a machine can actually do, once at startup, so
/// <see cref="DewarpBackendPolicy"/> can pick a renderer.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="Direct3DDewarpRenderer"/> because the probe has to be allowed to
/// fail. It runs on a fleet of machines nobody has inventoried, and "no adapter", "a driver that
/// throws on enumeration" and "d3d11.dll is missing" all have to come back as a sentence an
/// operator can read rather than as a crash on the way to a camera list.
/// </remarks>
public static class Direct3DProbe
{
    /// <summary>Microsoft's PCI vendor id, which is what WARP and the Basic Render Driver report.</summary>
    private const int MicrosoftVendorId = 0x1414;

    /// <summary>
    /// Everything the policy needs to know about this machine.
    /// </summary>
    /// <param name="wpfRenderTier">
    /// WPF's <c>RenderCapability.Tier &gt;&gt; 16</c>, passed in because this project deliberately
    /// does not reference WPF. Leave it at 2 from a context that has no WPF to ask.
    /// </param>
    public static DewarpGpuCapability Probe(int wpfRenderTier = 2)
    {
        bool remote = IsRemoteSession();
        try
        {
            var (device, context, description, hardware) = CreateDeviceCore();
            context.Dispose();
            device.Dispose();
            return new DewarpGpuCapability(
                DeviceCreated: true,
                IsHardwareAdapter: hardware,
                AdapterDescription: description,
                IsRemoteSession: remote,
                WpfRenderTier: wpfRenderTier,
                FailureReason: null);
        }
        catch (DllNotFoundException)
        {
            return Failed("Direct3D 11 is not present on this machine.", remote, wpfRenderTier);
        }
        catch (Exception ex)
        {
            return Failed(
                $"Direct3D 11 could not start on this machine ({ex.Message.Trim()}).",
                remote, wpfRenderTier);
        }
    }

    /// <summary>
    /// Creates the device the renderer will use, or throws with a message worth showing.
    /// </summary>
    public static (ID3D11Device Device, ID3D11DeviceContext Context, string AdapterDescription)
        CreateDevice()
    {
        var (device, context, description, _) = CreateDeviceCore();
        return (device, context, description);
    }

    private static (ID3D11Device, ID3D11DeviceContext, string, bool) CreateDeviceCore()
    {
        // The first adapter DXGI lists, which is the one Windows has decided this process should
        // render on. Asking for the most VRAM instead would be wrong on a laptop, where the
        // discrete adapter may not be the one attached to the display.
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        Exception? last = null;

        for (uint index = 0; ; index++)
        {
            // EnumAdapters1 answers DXGI_ERROR_NOT_FOUND past the last adapter, which is the
            // documented way to learn how many there are.
            if (factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Failure || adapter is null)
                break;

            using (adapter)
            {
                var description = adapter.Description1;
                // BgraSupport, because the pane is presented as BGRA and a shared Direct3D 9
                // surface is A8R8G8B8; without it the render target format is unavailable on some
                // drivers. Feature level 10 is enough for everything this shader does, and is
                // what makes an older integrated adapter usable rather than a fallback.
                var levels = new[]
                {
                    FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0,
                };
                try
                {
                    var result = global::Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                        adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                        out var device, out _, out var context);
                    result.CheckError();
                    bool hardware = !description.Flags.HasFlag(AdapterFlags.Software)
                        && description.VendorId != MicrosoftVendorId;
                    return (device!, context!, Name(description), hardware);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
        }

        throw last ?? new InvalidOperationException(
            "No graphics adapter on this machine could create a Direct3D 11 device.");
    }

    private static string Name(AdapterDescription1 description) =>
        string.IsNullOrWhiteSpace(description.Description)
            ? "an unnamed adapter"
            : description.Description.Trim();

    private static DewarpGpuCapability Failed(string reason, bool remote, int tier) =>
        new(false, false, string.Empty, remote, tier, reason);

    /// <summary>
    /// True inside a Terminal Services session.
    /// </summary>
    /// <remarks>
    /// <c>SM_REMOTESESSION</c> rather than the environment or a registry probe: it is what
    /// Windows itself answers, it flips when a session is reconnected to a different client, and
    /// it is one call with no dependency on WPF or WinForms.
    /// </remarks>
    public static bool IsRemoteSession()
    {
        const int SM_REMOTESESSION = 0x1000;
        try
        {
            return GetSystemMetrics(SM_REMOTESESSION) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
