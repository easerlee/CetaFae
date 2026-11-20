using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class BindingChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var card = new GuideCard(GuideOptions.Create("http://127.0.0.1:1/v1/", "generated-fixture"));
        card.SetCloud(true);
        card.SetStatus("Generated completed answer");
        card.ShowQuestion(); card.SetRegionAvailable(true); card.SetFollowupAvailable(true);
        var retained = GuideSession.RetainBoundAnswer(null, card);
        card.Reset();
        check(retained == "Generated completed answer" && !card.HasSession && !card.HasQuestion && !card.HasRegion && !card.HasFollowup,
            "suspension retains actual displayed reply even before the result cache commits, without retaining actions");
        card.SetStatus("上次回答\n" + retained);
        check(GuideSession.RetainBoundAnswer(retained, card) == retained,
            "repeated binding returns do not accumulate previous-answer labels");
        card.SetStatus("Generated clarification");
        check(GuideSession.RetainBoundAnswer(null, card) == "Generated clarification",
            "a visible clarification without a completed cache survives a foreground switch");
        check(GuideSession.RetainBoundAnswer(retained, card) == retained,
            "temporary follow-up instructions cannot replace the last completed answer");
        card.Reset();
        check(GuideSession.RetainBoundAnswer(retained, card) == retained,
            "an empty processing surface cannot erase the completed reply");
        card.SetStatus("Generated unrelated disclosure"); card.ShowConsent();
        check(GuideSession.RetainBoundAnswer(null, card) is null,
            "disclosure with a retained session cannot become a conversation reply");
        check(GuideSession.RetainBoundAnswer(retained, card) == retained,
            "disclosure is not remembered as a window conversation");
        card.Reset();
        check(GuideSession.RetainBoundAnswer(null, card) is null,
            "an explicit binding reset cannot resurrect completed text");
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
        var firstReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? waiting = firstReturn.Task;
        var refreshes = 0;
        var response = GuideSession.WaitForPresentationAsync(() =>
        {
            if (++refreshes == 2) waiting = secondReturn.Task;
            if (refreshes == 3) waiting = null;
        }, () => true, () => waiting, CancellationToken.None);
        check(!response.IsCompleted, "a completed model response remains hidden while the target is away");
        firstReturn.SetResult();
        secondReturn.SetResult();
        check(await response == true && refreshes == 3,
            "a rapid return and second focus loss both recheck presence before displaying a response");
        using var cancelledView = new CancellationTokenSource();
        var neverReturned = new TaskCompletionSource();
        var abandoned = GuideSession.WaitForPresentationAsync(() => { }, () => true, () => neverReturned.Task, cancelledView.Token);
        cancelledView.Cancel();
        check(await abandoned is null, "closing or replacing the binding discards a response without waiting for target focus");
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
