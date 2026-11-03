namespace CetaFae.Probe;

internal enum BindingChange { None, Suspended, Resumed, Ended }

// Window identity outlives an individual observation or model request.
internal sealed class GuideBinding
{
    internal IntPtr Window { get; private set; }
    internal uint ProcessId { get; private set; }
    internal int Generation { get; private set; }
    internal bool Confirmed { get; private set; }
    internal bool Suspended { get; private set; }

    internal void Begin(IntPtr window, uint processId)
    {
        Clear(); Window = window; ProcessId = processId;
    }

    internal void Confirm() { if (Window != IntPtr.Zero) Confirmed = true; }

    internal BindingChange Update(bool sameIdentity, bool visible, bool allowedForeground)
    {
        if (Window == IntPtr.Zero) return BindingChange.None;
        if (!sameIdentity) { Clear(); return BindingChange.Ended; }
        if (!Confirmed) return BindingChange.None;
        var suspend = !visible || !allowedForeground;
        if (suspend == Suspended) return BindingChange.None;
        Suspended = suspend;
        return suspend ? BindingChange.Suspended : BindingChange.Resumed;
    }

    internal void Clear()
    {
        Window = IntPtr.Zero; ProcessId = 0; Confirmed = Suspended = false; Generation++;
    }
}
