namespace Glasswork.Core.AppUpdate;

/// <summary>
/// Discriminated result for whether the canvas-extension Retry action can be
/// spawned as a detached PowerShell process. Unlike <see cref="SelfUpdatePlan"/>,
/// there is no release page to fall back to — Retry either runs the bundled
/// installer script or the Settings UI shows an inline message explaining why
/// it could not.
/// </summary>
public sealed record CanvasExtensionRetryPlan
{
    private CanvasExtensionRetryPlan(
        bool canRun,
        SelfUpdateFallbackReason reason,
        SelfUpdateProcessSpec? processSpec)
    {
        CanRun = canRun;
        Reason = reason;
        ProcessSpec = processSpec;
    }

    public bool CanRun { get; }
    public SelfUpdateFallbackReason Reason { get; }
    public SelfUpdateProcessSpec? ProcessSpec { get; }

    public static CanvasExtensionRetryPlan Unavailable(SelfUpdateFallbackReason reason) =>
        new(canRun: false, reason, processSpec: null);

    public static CanvasExtensionRetryPlan Run(SelfUpdateProcessSpec spec) =>
        new(canRun: true, SelfUpdateFallbackReason.None, spec);
}
