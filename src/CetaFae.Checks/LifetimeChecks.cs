using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CetaFae.Probe;
using CetaFae.Plugins;

namespace CetaFae.Checks;

static class LifetimeChecks
{
    public static int Fixture(string mode)
    {
        Console.ReadLine();
        if (mode == "blocked") Thread.Sleep(Timeout.Infinite);
        if (mode == "oversized") Console.Write(new string('x', 13 * 1024 * 1024));
        if (mode == "failed") return 3;
        if (mode == "ok") Console.Write("ready");
        return 0;
    }

    static ProcessStartInfo FixtureStart(string mode)
    {
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(typeof(LifetimeChecks).Assembly.Location);
        start.ArgumentList.Add("--worker-fixture");
        start.ArgumentList.Add(mode);
        return start;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var lifetime = new CancellationTokenSource();
        using (var deadline = new GuideDeadline(TimeSpan.FromMilliseconds(200), lifetime.Token))
        {
            await deadline.RunAsync(async token => { await Task.Delay(120, token); return 1; });
            var nextRead = false;
            try
            {
                await deadline.RunAsync(async token => { await Task.Delay(120, token); return 2; });
                nextRead = true;
            }
            catch (OperationCanceledException) { }
            check(!nextRead, "sequential processing stages share one deadline instead of resetting it");
        }
        var late = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var deadline = new GuideDeadline(TimeSpan.FromMilliseconds(40), lifetime.Token))
        {
            var rejected = false;
            try { await deadline.RunAsync(_ => late.Task); }
            catch (OperationCanceledException) { rejected = true; }
            late.SetResult(7);
            check(rejected, "a backend ignoring cancellation cannot deliver a late result");
            var dispatched = false;
            try { await deadline.RunAsync(_ => { dispatched = true; return Task.FromResult(1); }); }
            catch (OperationCanceledException) { }
            check(!dispatched, "an expired stage cannot dispatch another read or request");
        }
        using (var deadline = new GuideDeadline(TimeSpan.FromMilliseconds(40), lifetime.Token))
            check(await deadline.RunAsync(_ => Task.FromResult(1)) == 1, "a completed processing stage returns its result");
        await Task.Delay(80);
        check(!lifetime.IsCancellationRequested, "disposing the processing deadline preserves the guidance lifetime");
        using (var revoked = new CancellationTokenSource())
        using (var deadline = new GuideDeadline(TimeSpan.FromSeconds(15), revoked.Token))
        {
            revoked.Cancel();
            var dispatched = false;
            try { await deadline.RunAsync(_ => { dispatched = true; return Task.FromResult(1); }); }
            catch (OperationCanceledException) { }
            check(!dispatched, "revocation before processing prevents dispatch");
        }

        using (var cancel = new CancellationTokenSource(1500))
        {
            var rejected = false;
            var childId = 0;
            var pending = ObservationWorker.ExchangeAsync(FixtureStart("blocked"), "{}", cancel.Token, id => childId = id);
            var cancellation = new Stopwatch();
            using var notice = cancel.Token.Register(cancellation.Start);
            try { await pending; }
            catch (OperationCanceledException) { rejected = true; }
            var exited = false;
            try { using var child = Process.GetProcessById(childId); exited = child.HasExited; }
            catch (ArgumentException) { exited = true; }
            check(rejected && childId != 0 && exited, "cancellation terminates and reaps a blocked worker before returning");
            check(cancellation.IsRunning && cancellation.ElapsedMilliseconds <= 500,
                "owned-worker cancellation and reaping completes within 500ms in the headless fixture");
        }
        using (var timeout = new CancellationTokenSource(5000))
            check(await ObservationWorker.ExchangeAsync(FixtureStart("ok"), "{}", timeout.Token) == "ready",
                "a new worker can complete after a blocked worker is terminated");
        using (var timeout = new CancellationTokenSource(5000))
        {
            var rejected = false;
            try { await ObservationWorker.ExchangeAsync(FixtureStart("oversized"), "{}", timeout.Token); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "oversized pipe output fails without waiting for the timeout");
        }
        using (var timeout = new CancellationTokenSource(5000))
        {
            var rejected = false;
            try { await ObservationWorker.ExchangeAsync(FixtureStart("failed"), "{}", timeout.Token); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "a failed worker cannot produce a successful observation");
        }
        var observation = new GuideObservation(new IntPtr(42), 5, new Rect(-10, 20, 200, 100),
            [new GuideCandidate(1, "generated", "test", "ControlType.Button", new Rect(0, 30, 50, 20), [1, 2], "On")],
            [1, 2, 3], true, "generated.plugin");
        var copy = JsonSerializer.Deserialize<GuideObservation>(JsonSerializer.Serialize(observation, ObservationWorker.Json),
            ObservationWorker.Json)!;
        check(copy.Window == observation.Window && copy.Frame == observation.Frame && copy.RawView &&
              copy.PluginId == observation.PluginId &&
              copy.Png!.SequenceEqual(observation.Png!) && GuideObservation.HasSameContext(observation.Candidates, copy.Candidates),
            "bounded worker IPC preserves HWND, pixels, geometry, identities and state");
        var escapedPlugin = new GuidePlugin(1, "generated.escaped", "0.1.0", "Generated",
            Enumerable.Range(0, 4).Select(i => new PluginTarget(["process-" + i],
                Enumerable.Range(0, 16).Select(j => new string('<', 190) + j).ToArray())).ToArray(),
            "control", ["generated.task"], []);
        var wire = JsonSerializer.Serialize(new { Window = 42, IncludeImage = false, Plugin = escapedPlugin }, ObservationWorker.Json);
        check(wire.Length > 64 * 1024 &&
              await ObservationWorker.ReadBoundedAsync(new StringReader(wire), ObservationWorker.MaxRequestCharacters,
                  CancellationToken.None) == wire,
            "bounded collector input accepts JSON-escaped plugin data without the old 64Ki character truncation");
        var worker = new ProcessStartInfo("dotnet");
        worker.ArgumentList.Add(typeof(ObservationWorker).Assembly.Location);
        worker.ArgumentList.Add("--observe-worker");
        using (var timeout = new CancellationTokenSource(5000))
        {
            var reply = await ObservationWorker.ExchangeAsync(worker, "invalid private input", timeout.Token);
            using var json = JsonDocument.Parse(reply);
            check(json.RootElement.GetProperty("Observation").ValueKind == JsonValueKind.Null &&
                  json.RootElement.GetProperty("Error").GetString() == "worker_request_invalid" && !reply.Contains("private"),
                "the production worker rejects malformed input without a window, observation or content echo");
        }

        // Hold stdin open without any request: the production watchdog must terminate this child without UIA.
        worker.RedirectStandardInput = true;
        worker.RedirectStandardOutput = true;
        worker.RedirectStandardError = true;
        worker.UseShellExecute = false;
        worker.CreateNoWindow = true;
        using var orphan = new Process { StartInfo = worker };
        orphan.Start();
        try
        {
            using var timeout = new CancellationTokenSource(18000);
            await orphan.WaitForExitAsync(timeout.Token);
            check(orphan.ExitCode == 3, "production watchdog terminates a stalled headless worker without a parent cancellation");
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill();
            await orphan.WaitForExitAsync();
        }
    }
}
