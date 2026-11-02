using System.IO;
using System.Text.Json;
using System.Windows;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class SnapshotChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var frame = new Rect(0, 0, 900, 600);
        var candidate = new GuideCandidate(1, "Generated button", "", "ControlType.Button", new Rect(20, 20, 80, 30), [1]);
        var snapshot = new GuideObservation(new IntPtr(123), 456, frame, [], null,
            PluginId: "generated", Snapshot: true);
        var changed = snapshot with { Candidates = [candidate], VisualHash = "changed" };
        check(snapshot.ValidateContext(changed, null) is null,
            "a captured-view explanation is independent of later pixel and candidate changes");
        void Reject(Action action, string name)
        {
            var rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, name);
        }
        Reject(() => snapshot.ValidateContext(changed, 1), "snapshot recognition cannot authorize a control marker");
        foreach (var wrong in new[] { snapshot with { Window = new IntPtr(124) }, snapshot with { ProcessId = 457 },
                     snapshot with { Frame = new Rect(1, 0, 900, 600) }, snapshot with { PluginId = "other" },
                     snapshot with { RawView = true } })
            Reject(() => snapshot.ValidateContext(wrong, null), "snapshot target and provider bindings stay strict");
        var strict = snapshot with { Snapshot = false, Candidates = [candidate], VisualHash = "original" };
        Reject(() => strict.ValidateContext(strict with { VisualHash = "changed" }, null),
            "strict guidance still refuses changed visual evidence");
        Reject(() => strict.ValidateContext(strict with { Candidates = [] }, 1),
            "strict guidance still refuses removed control evidence");
        Reject(() => strict.ValidateContext(snapshot, null), "strict guidance cannot be downgraded to snapshot validation");
        check(GuideObservation.ReadOptionalCandidates(() => throw new InvalidDataException("too_many_candidates")).Count == 0,
            "a large actionable-control list does not block image explanation");
        check(GuideObservation.ReadOptionalCandidates(() => throw new System.Runtime.InteropServices.COMException()).Count == 0,
            "an unavailable control provider degrades to image explanation");
        var limited = false;
        check(GuideObservation.ReadOptionalCandidates(() => throw new InvalidDataException("privacy_unavailable"),
                  () => limited = true).Count == 0 && limited,
            "incomplete control privacy properties are explicitly limited evidence, not a safety verdict");
        foreach (var code in new[] { "sensitive_content", "target_changed" })
            Reject(() => GuideObservation.ReadOptionalCandidates(() => throw new InvalidDataException(code)),
                "optional controls never swallow privacy or target rejection");
        var cancelled = false;
        try { GuideObservation.ReadOptionalCandidates(() => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "optional controls never swallow cancellation");
        var nameRead = false;
        var available = GuidePrivacy.TryInspect(() => GuidePrivacy.ReadVisibleName(false, null, () =>
        { nameRead = true; return "not read"; }));
        check(!available && !nameRead, "best-effort checking skips an unverified name without blocking the image path");
        var checkedSibling = false;
        available = GuidePrivacy.TryInspect(() => checkedSibling = true);
        check(available && checkedSibling, "best-effort checking continues after a missing field");
        Reject(() => GuidePrivacy.TryInspect(() => GuidePrivacy.ReadVisibleName(false, true, () => "not read")),
            "a later protected field still rejects the image path");
        Reject(() => GuidePrivacy.TryInspect(() => GuidePrivacy.EnsureTextSafe("password=generated-only")),
            "detected credential text cannot degrade to limited checking");
        cancelled = false;
        try { GuidePrivacy.TryInspect(() => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "best-effort privacy does not absorb cancellation");
        var visible = new Rect(8, 8, 900, 600);
        var outer = new Rect(0, 0, 916, 616);
        check(WindowCapture.MapBounds(visible, outer, 900, 600) == visible &&
              WindowCapture.MapBounds(visible, outer, 916, 616) == outer,
            "window capture maps exact compositor or outer-window dimensions without guessing offsets");
        Reject(() => WindowCapture.MapBounds(visible, outer, 901, 600), "mismatched capture dimensions cannot be stretched onto a target");
        Reject(() => WindowCapture.MapBounds(visible, outer, 100000, 100000), "capture dimensions are bounded before allocation");
        var legacyRead = false;
        check(Capture.WithWindowFallback(() => "modern", () => { legacyRead = true; return "legacy"; }) == "modern" && !legacyRead,
            "available target-window compositor capture does not invoke legacy capture");
        check(Capture.WithWindowFallback<string>(() => throw new InvalidDataException("capture_backend_unavailable"),
                  () => "legacy") == "legacy", "unavailable window compositor can use target-only PrintWindow");
        foreach (var error in new Exception[] { new UnauthorizedAccessException(),
                     new System.Runtime.InteropServices.COMException("generated", unchecked((int)0x80070005)),
                     new InvalidDataException("capture_protected"), new InvalidDataException("capture_target_unavailable") })
        {
            legacyRead = false;
            Reject(() => Capture.WithWindowFallback<string>(() => throw error,
                () => { legacyRead = true; return "legacy"; }), "protected or vanished targets cannot fall back through another capture path");
            check(!legacyRead, "capture fallback does not bypass access denial or selected-target loss");
        }
        var plugin = PluginCatalog.ParsePlugin(ScopeChecks.Package);
        var bound = snapshot with { PluginId = plugin.Id, PrivacyLimited = true };
        var roundtrip = JsonSerializer.Deserialize<GuideObservation>(JsonSerializer.Serialize(bound, ObservationWorker.Json), ObservationWorker.Json)!;
        check(roundtrip.Snapshot && roundtrip.PrivacyLimited, "snapshot and limited-check evidence survive owned-worker IPC");
        ObservationWorker.ValidateScopeReply(roundtrip, plugin, null, null, true);
        foreach (var forged in new[] { bound with { Candidates = [candidate] }, bound with { VisualHash = "invented" },
                     bound with { Scope = GuideScope.FromUserSelection(frame, new Rect(10, 10, 30, 30)) } })
            Reject(() => ObservationWorker.ValidateScopeReply(forged, plugin, null, null, true),
                "worker snapshot cannot inject candidate authority, live fingerprints or an unauthorized scope");
    }

    internal static async Task RunTransportAsync(Action<bool, string> check)
    {
        var attempts = new List<bool>();
        var response = await ObservationWorker.ExchangeWithOptionalAccessibilityAsync(async (visualOnly, token) =>
        {
            attempts.Add(visualOnly);
            if (!visualOnly) await Task.Delay(Timeout.Infinite, token);
            return "generated image reply";
        }, true, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        check(response == "generated image reply" && attempts.SequenceEqual(new[] { false, true }),
            "a timed-out optional accessibility pass permits exactly one image-only worker continuation");
        using var stop = new CancellationTokenSource();
        attempts.Clear(); var cancelled = false;
        try
        {
            await ObservationWorker.ExchangeWithOptionalAccessibilityAsync(async (visualOnly, token) =>
            {
                attempts.Add(visualOnly); stop.Cancel();
                await Task.Delay(Timeout.Infinite, token); return "not returned";
            }, true, stop.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && attempts.SequenceEqual(new[] { false }), "withdrawal never starts an image-only continuation");
        attempts.Clear();
        response = await ObservationWorker.ExchangeWithOptionalAccessibilityAsync((visualOnly, _) =>
        { attempts.Add(visualOnly); return Task.FromResult("sensitive refusal reply"); }, true, CancellationToken.None);
        check(response == "sensitive refusal reply" && attempts.SequenceEqual(new[] { false }),
            "a completed refusal is retained instead of retried as image-only collection");
    }
}
