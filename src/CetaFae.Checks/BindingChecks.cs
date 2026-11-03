using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class BindingChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var binding = new GuideBinding();
        var target = new IntPtr(41);
        binding.Begin(target, 42);
        check(binding.Update(true, true, false) == BindingChange.None && !binding.Confirmed,
            "a pending drop is not suspended before its activation decision");
        binding.Confirm();
        var generation = binding.Generation;
        check(binding.Update(true, true, false) == BindingChange.Suspended && binding.Window == target && binding.ProcessId == 42,
            "switching away suspends a confirmed binding without changing identity");
        check(binding.Update(true, true, false) == BindingChange.None,
            "repeated background events do not repeatedly reset the suspended binding");
        var gate = new GuideRunGate(); gate.SetCloud(true); var pending = gate.Begin();
        gate.Cancel();
        check(binding.Update(true, true, true) == BindingChange.Resumed && !gate.Accepts(pending) && binding.Generation == generation,
            "returning retains the binding but cannot revive a cancelled response");
        check(binding.Update(true, false, true) == BindingChange.Suspended && binding.Window == target &&
            binding.Update(true, true, true) == BindingChange.Resumed,
            "minimization and restoration suspend and resume the same target");
        check(binding.Update(false, true, true) == BindingChange.Ended && binding.Window == IntPtr.Zero && binding.Generation != generation,
            "destroyed or replaced window identity ends a binding instead of resuming a different window");
        binding.Begin(target, 99); binding.Confirm(); binding.Clear();
        check(binding.Update(true, true, true) == BindingChange.None && !binding.Confirmed && !binding.Suspended,
            "explicit close or drag clears identity and cannot be undone by a queued foreground event");
        var attempts = 0;
        check(await GuideSession.ActivateOnceAsync(target, () => attempts++, () => target, CancellationToken.None) && attempts == 0,
            "an already foreground drop needs no activation request");
        var polls = 0;
        check(await GuideSession.ActivateOnceAsync(target, () => attempts++, () => ++polls > 3 ? target : IntPtr.Zero,
            CancellationToken.None) && attempts == 1,
            "one activation request waits for actual asynchronous foreground confirmation");
        check(!await GuideSession.ActivateOnceAsync(target, () => attempts++, () => IntPtr.Zero, CancellationToken.None) && attempts == 2,
            "a denied activation is not reported successful and does not trigger repeated attempts");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await GuideSession.ActivateOnceAsync(target, () => attempts++, () => IntPtr.Zero, cancelled.Token); }
        catch (OperationCanceledException) { }
        check(attempts == 2, "a cancelled activation cannot start another foreground request");
    }
}
