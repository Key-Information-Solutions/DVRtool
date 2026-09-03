namespace DVRTool.Core;

/// <summary>Which renderer is actually drawing a dewarped pane.</summary>
public enum DewarpBackend
{
    /// <summary>
    /// A Direct3D 11 pixel shader. The default wherever it will work: the source frame is
    /// uploaded once and everything after it — colour conversion, mip generation, the projection
    /// and the gather — happens on the adapter.
    /// </summary>
    Gpu,

    /// <summary>
    /// <see cref="DewarpMap"/> plus <see cref="DewarpSampler"/> across every core. The fallback,
    /// and fast enough to be a real one rather than a token.
    /// </summary>
    Cpu,
}

/// <summary>What the operator has asked for, which is normally "whatever works best".</summary>
public enum DewarpBackendPreference
{
    /// <summary>
    /// Hardware when the machine can present it, the CPU renderer otherwise. The default.
    /// </summary>
    Auto,

    /// <summary>
    /// Hardware if a device can be created at all, even somewhere <see cref="Auto"/> would have
    /// declined. The escape hatch for a machine whose configuration this code reads wrongly.
    /// </summary>
    Gpu,

    /// <summary>
    /// The CPU renderer regardless. For a machine with a misbehaving driver, and for producing a
    /// reference image to compare an accelerated one against.
    /// </summary>
    Cpu,
}

/// <summary>
/// What a machine can actually do, as probed once at startup. Plain data, so the policy that
/// reads it is testable without a graphics adapter.
/// </summary>
/// <param name="DeviceCreated">A Direct3D 11 device was created successfully.</param>
/// <param name="IsHardwareAdapter">
/// The adapter backing that device is real silicon rather than WARP or the Microsoft Basic
/// Render Driver.
/// </param>
/// <param name="AdapterDescription">The adapter's name, for the reason string and the log.</param>
/// <param name="IsRemoteSession">This process is running inside a Terminal Services session.</param>
/// <param name="WpfRenderTier">
/// WPF's own render tier — 0 when WPF is compositing in software, 2 when it is not.
/// </param>
/// <param name="FailureReason">
/// Why <paramref name="DeviceCreated"/> is false, worded for an operator. Null when it is true.
/// </param>
public readonly record struct DewarpGpuCapability(
    bool DeviceCreated,
    bool IsHardwareAdapter,
    string AdapterDescription,
    bool IsRemoteSession,
    int WpfRenderTier,
    string? FailureReason)
{
    /// <summary>What to report when no probe has run, or when one threw.</summary>
    public static DewarpGpuCapability None(string reason) =>
        new(false, false, string.Empty, false, 0, reason);
}

/// <summary>The chosen backend and why, so the GUI can say it and a log can record it.</summary>
/// <param name="Backend">The renderer to use.</param>
/// <param name="Reason">
/// One sentence an operator can act on. Present whether the answer was hardware or not, because
/// "why is this one on the CPU?" is the question that actually gets asked.
/// </param>
public readonly record struct DewarpBackendChoice(DewarpBackend Backend, string Reason);

/// <summary>
/// Picks the renderer for a machine. Pure, and the only place the rule lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hardware is the default and the CPU renderer is for the exceptions.</b> The CPU path was
/// built first, which made it easy to think of it as the baseline with acceleration as a bonus —
/// but the ordinary case is an operator sitting at a workstation with a GPU in it, and on that
/// machine the accelerated path is not a marginal win: it drops a 2560×2560 panorama from ~7 ms
/// across twenty-four cores to a fraction of a millisecond on one adapter, it rebuilds no table
/// when the view moves, and it aliases less because the mip level is chosen per pixel instead of
/// per pane. Headless and remote sessions are the exception, and they are what
/// <see cref="DewarpBackend.Cpu"/> is for.
/// </para>
/// <para>
/// <b>Every rule below is about presentation, not arithmetic.</b> A remote session may well have
/// a hardware adapter — Windows has offered one to Terminal Services sessions since WDDM 1.2, so
/// the shader would run fine. What does not run fine is getting the result on screen: the
/// accelerated pane reaches WPF through a shared Direct3D 9Ex surface, whose front buffer is not
/// reliably available in a remote session, and WPF itself commonly drops to software compositing
/// there — at which point the finished texture is read back over the wire and the "acceleration"
/// is a net loss. Same for render tier 0. So these are not "the GPU is too slow" rules, they are
/// "the picture cannot get out" rules, and that is why an explicit
/// <see cref="DewarpBackendPreference.Gpu"/> is allowed to override all of them.
/// </para>
/// </remarks>
public static class DewarpBackendPolicy
{
    /// <summary>The renderer to use, and the sentence explaining it.</summary>
    public static DewarpBackendChoice Choose(
        DewarpBackendPreference preference, in DewarpGpuCapability capability)
    {
        string adapter = string.IsNullOrWhiteSpace(capability.AdapterDescription)
            ? "the graphics adapter"
            : capability.AdapterDescription;

        if (preference == DewarpBackendPreference.Cpu)
            return new DewarpBackendChoice(DewarpBackend.Cpu,
                "The CPU renderer was selected in the settings.");

        if (!capability.DeviceCreated)
        {
            string why = string.IsNullOrWhiteSpace(capability.FailureReason)
                ? "No Direct3D 11 device could be created on this machine."
                : capability.FailureReason!;
            return new DewarpBackendChoice(DewarpBackend.Cpu, why);
        }

        // An explicit request gets honoured as far as a working device allows, including on the
        // configurations Auto declines: the operator can see their own screen, and this code
        // cannot.
        if (preference == DewarpBackendPreference.Gpu)
            return new DewarpBackendChoice(DewarpBackend.Gpu,
                $"Hardware rendering on {adapter}, selected in the settings.");

        if (!capability.IsHardwareAdapter)
            return new DewarpBackendChoice(DewarpBackend.Cpu,
                $"Only a software adapter is available ({adapter}), which is slower than the " +
                "CPU renderer. Using the CPU renderer.");

        // Ordered ahead of the render tier because it is the more useful sentence: an operator
        // who is on RDP knows it, and telling them the tier number instead explains nothing.
        if (capability.IsRemoteSession)
            return new DewarpBackendChoice(DewarpBackend.Cpu,
                "This is a remote-desktop session, where the accelerated pane cannot be " +
                "presented reliably. Using the CPU renderer.");

        if (capability.WpfRenderTier <= 0)
            return new DewarpBackendChoice(DewarpBackend.Cpu,
                "Windows is compositing this window in software, so an accelerated pane would " +
                "be copied back out of the adapter every frame. Using the CPU renderer.");

        return new DewarpBackendChoice(DewarpBackend.Gpu, $"Hardware rendering on {adapter}.");
    }

    /// <summary>
    /// The choice to fall back to when an accelerated renderer fails at run time, keeping the
    /// original message so an operator sees the cause and not just the consequence.
    /// </summary>
    /// <remarks>
    /// This is the path a driver reset, a GPU hot-swap or an operator connecting over RDP to a
    /// session that was local a moment ago takes: the device is gone mid-frame, and the pane has
    /// to keep drawing. Demoting for the rest of the session rather than retrying every frame is
    /// deliberate — a device that has just been removed is usually about to be removed again, and
    /// a pane that flickers between two renderers is worse than one that quietly stays on the
    /// slower one.
    /// </remarks>
    public static DewarpBackendChoice DemoteToCpu(string failure)
    {
        string detail = string.IsNullOrWhiteSpace(failure) ? "it stopped responding" : failure;
        return new DewarpBackendChoice(DewarpBackend.Cpu,
            $"Hardware rendering was dropped for this session because {detail} Using the CPU " +
            "renderer.");
    }
}
