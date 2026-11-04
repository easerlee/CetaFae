namespace CetaFae.Probe;

/// <summary>A previous interpretation helps resolve references, never validates current evidence.</summary>
internal sealed class GuideUnderstanding
{
    GuideFlowTarget? _target;
    GuideIdentification? _identification;

    internal void Remember(GuideFlowTarget target, GuideIdentification identification)
    {
        _target = target;
        _identification = identification with { Question = null, NextCheck = null };
    }

    internal GuideIdentification? ForTarget(GuideFlowTarget target) => target == _target ? _identification : null;
    internal GuideIdentification? ForWindow(IntPtr window) => _target?.Window == window ? _identification : null;

    internal void Clear() { _target = null; _identification = null; }
}
