using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

internal static class PluginWorkerChecks
{
    static readonly PluginProgram Counter = new(1, "let count=0; function invoke(input) { const dirty = Object.prototype.changed === true; Object.prototype.changed = true; return JSON.stringify({count:++count,dirty,question:JSON.parse(input).input.question || ''}); }");
    static bool Exited(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    internal static async Task<int> OwnerFixtureAsync()
    {
        var output = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            var id = 0;
            await PluginWorker.InvokeAsync(Counter, "generated", new { }, default, value => id = value);
            await output.WriteLineAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await output.FlushAsync();
            await Console.In.ReadLineAsync();
            return 0;
        }
        finally { await PluginWorker.ClearIdleAsync(); }
    }

    internal static async Task SoakAsync(Action<bool, string> check)
    {
        var samples = new List<object>();
        var owners = new List<object>();
        var calls = new List<int>();
        var completed = false;
        var started = Stopwatch.StartNew();
        await PluginWorker.ClearIdleAsync();
        try
        {
            for (var call = 0; call < PluginWorker.MaxCalls * 5; call++)
            {
                var id = 0;
                var question = new string('水', 10_000) + call;
                var reply = await PluginWorker.InvokeAsync(Counter, "generated", new { question }, default, value => id = value);
                calls.Add(id);
                using var json = JsonDocument.Parse(reply);
                if (json.RootElement.GetProperty("count").GetInt32() != 1 ||
                    json.RootElement.GetProperty("dirty").GetBoolean() ||
                    json.RootElement.GetProperty("question").GetString() != question)
                    throw new InvalidOperationException("soak_request_isolation");
                if (call % PluginWorker.MaxCalls is 0 or 15 or 30)
                {
                    using var child = Process.GetProcessById(id);
                    child.Refresh();
                    samples.Add(new { call = call + 1, id, privateBytes = child.PrivateMemorySize64, handles = child.HandleCount });
                    if (child.PrivateMemorySize64 > PluginProcessLimits.MaxMemoryBytes)
                        throw new InvalidOperationException("soak_worker_memory_limit");
                }
            }
            check(calls.Chunk(PluginWorker.MaxCalls).All(group => group.Distinct().Count() == 1) &&
                  calls.Distinct().Count() == 5 && calls.All(Exited),
                "160 generated calls retain fresh state and retire all five 32-call workers");

            var age = Stopwatch.StartNew();
            var first = 0;
            var ageCalls = new List<int>();
            for (var call = 0; call < 8; call++)
            {
                if (call > 0) await Task.Delay(TimeSpan.FromSeconds(10));
                var id = 0;
                await PluginWorker.InvokeAsync(Counter, "generated", new { }, default, value => id = value);
                ageCalls.Add(id);
                if (call == 0) first = id;
                if (id != first) break;
            }
            check(age.Elapsed >= TimeSpan.FromSeconds(60) && ageCalls.Count >= 7 &&
                  ageCalls.Take(ageCalls.Count - 1).All(id => id == first) && ageCalls[^1] != first && Exited(first),
                "an active lease is replaced after its real 60-second age limit before 32 calls");
            await PluginWorker.ClearIdleAsync();
            check(ageCalls.All(Exited), "explicit owner cleanup reaps the final age-test worker");

            foreach (var crash in new[] { false, true })
            {
                var start = PluginWorker.StartInfo(Environment.ProcessPath!, typeof(PluginWorkerChecks).Assembly.Location);
                start.ArgumentList.Clear();
                start.ArgumentList.Add(typeof(PluginWorkerChecks).Assembly.Location);
                start.ArgumentList.Add("--plugin-owner-fixture");
                using var owner = new Process { StartInfo = start };
                Process? child = null;
                owner.Start();
                try
                {
                    var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    if (!int.TryParse(line, out var id)) throw new InvalidDataException("soak_owner_ready");
                    child = Process.GetProcessById(id);
                    var elapsed = Stopwatch.StartNew();
                    if (crash) owner.Kill();
                    else { await owner.StandardInput.WriteLineAsync("stop"); await owner.StandardInput.FlushAsync(); }
                    await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                    owners.Add(new { crash, exitMs = elapsed.Elapsed.TotalMilliseconds, ownerExit = owner.ExitCode, childExited = child.HasExited });
                    check(child.HasExited && (crash || owner.ExitCode == 0), crash
                        ? "abrupt owner exit closes actual pipes and its idle plugin worker exits without replay"
                        : "normal owner exit reaps its idle plugin worker before returning");
                }
                finally
                {
                    if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); }
                    if (child is not null)
                    {
                        if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                        child.Dispose();
                    }
                }
            }
            completed = true;
        }
        finally
        {
            await PluginWorker.ClearIdleAsync();
            var path = Path.Combine(Environment.CurrentDirectory, ".artifacts", "checks", "warm-worker-soak.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { completed, elapsedMs = started.Elapsed.TotalMilliseconds,
                calls = calls.Count, workers = calls.Distinct().Count(), samples, owners, desktop = false, model = false,
                limitations = "Helper-only counters; no forced GC, leak threshold, aggregate idle-memory or native acceptance." },
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await PluginWorker.ClearIdleAsync();
        try
        {
            using (var frames = new MemoryStream())
            {
                await PluginWorker.WriteFrameAsync(frames, "{\"文字\":\"水花\"}", 100);
                await PluginWorker.WriteFrameAsync(frames, "{}", 100);
                frames.Position = 0;
                check(await PluginWorker.ReadFrameAsync(frames, 100) == "{\"文字\":\"水花\"}" &&
                      await PluginWorker.ReadFrameAsync(frames, 100) == "{}" && await PluginWorker.ReadFrameAsync(frames, 100) is null,
                    "bounded byte frames preserve Unicode and distinct replies before clean EOF");
            }
            foreach (var bytes in new[] { new byte[] {0,0}, new byte[] {0,0,0,0}, new byte[] {255,255,255,255},
                         new byte[] {0,0,1,0}, new byte[] {0,0,0,2,123}, new byte[] {0,0,0,1,255} })
            {
                var rejected = false;
                try { await PluginWorker.ReadFrameAsync(new MemoryStream(bytes), 100); }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException) { rejected = true; }
                check(rejected, "invalid, partial, oversized or non-UTF8 worker frames reject");
            }
            var first = 0; var second = 0;
            var one = await PluginWorker.InvokeAsync(Counter, "generated", new { question="one" }, default, id => first=id);
            var two = await PluginWorker.InvokeAsync(Counter, "generated", new { question="two" }, default, id => second=id);
            using (var a = JsonDocument.Parse(one))
            using (var b = JsonDocument.Parse(two))
                check(first == second && a.RootElement.GetProperty("count").GetInt32() == 1 &&
                      b.RootElement.GetProperty("count").GetInt32() == 1 && !b.RootElement.GetProperty("dirty").GetBoolean() &&
                      b.RootElement.GetProperty("question").GetString() == "two",
                    "same program reuses its process with fresh globals, prototypes and current request");
            var ids = new List<int>();
            var parallel = Enumerable.Range(0, 4).Select(i => PluginWorker.InvokeAsync(Counter, "generated", new { question=i.ToString() },
                default, id => ids.Add(id))).ToArray();
            await Task.WhenAll(parallel);
            check(ids.Count == 4 && ids.All(id => id == first) && parallel.All(t =>
                { using var value=JsonDocument.Parse(t.Result); return value.RootElement.GetProperty("count").GetInt32() == 1; }),
                "concurrent requests serialize on one process without sharing interpreter state");
            var changed = 0;
            await PluginWorker.InvokeAsync(new PluginProgram(1, "function invoke(){return '{}';}"), "generated", new { }, default, id => changed=id);
            check(changed != first && Exited(first), "different program kills and reaps the previous process before dispatch");

            await PluginWorker.ClearIdleAsync();
            var calls = new List<int>();
            for (var i = 0; i < PluginWorker.MaxCalls + 1; i++)
                await PluginWorker.InvokeAsync(Counter, "generated", new { }, default, id => calls.Add(id));
            check(calls.Take(PluginWorker.MaxCalls).Distinct().Count() == 1 && calls[^1] != calls[0] && Exited(calls[0]),
                "worker retires after its bounded invocation count and the next call starts clean");

            var failedId=0; var rejectedProgram=false;
            try { await PluginWorker.InvokeAsync(new PluginProgram(1, "function invoke(){return '[]';}"), "generated", new { }, default, id=>failedId=id); }
            catch (InvalidDataException) { rejectedProgram=true; }
            check(rejectedProgram && Exited(failedId), "invalid program result reaps the worker without retaining it");

            var cancelledId=0;
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled=false;
                try { await PluginWorker.InvokeAsync(Counter,"generated",new{},cancel.Token,id=>{cancelledId=id;cancel.Cancel();}); }
                catch (OperationCanceledException) { cancelled=true; }
                check(cancelled && Exited(cancelledId), "in-flight cancellation reaps its owned worker before returning");
            }
            var occupied = new PluginProgram(1, "function invoke(){while(true){}} ");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var cancel = new CancellationTokenSource())
            {
                var running = PluginWorker.InvokeAsync(occupied,"generated",new{},cancel.Token,_=>entered.SetResult());
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                using var queuedCancel = new CancellationTokenSource();
                var queuedId=0;
                var queued = PluginWorker.InvokeAsync(Counter,"generated",new{},queuedCancel.Token,id=>queuedId=id);
                queuedCancel.Cancel();
                var cancelled=false;
                try { await queued; } catch (OperationCanceledException) { cancelled=true; }
                cancel.Cancel();
                try { await running; } catch (OperationCanceledException) { }
                check(cancelled && queuedId==0,"cancelled queued call never starts or borrows a process");
            }
            var timedId=0; var timed=false;
            try { await PluginWorker.InvokeAsync(occupied,"generated",new{},default,id=>timedId=id); }
            catch (InvalidDataException) { timed=true; }
            check(timed && Exited(timedId), "runaway execution is refused and its process is reaped");

            var recovered=0;
            await PluginWorker.InvokeAsync(Counter,"generated",new{},default,id=>recovered=id);
            using(var process=Process.GetProcessById(recovered)) process.Kill();
            var afterCrash=0;
            await PluginWorker.InvokeAsync(Counter,"generated",new{},default,id=>afterCrash=id);
            check(afterCrash!=recovered,"an already-exited idle worker is replaced for a new request without replay");
            await Task.Delay(PluginWorker.IdleTimeout + TimeSpan.FromSeconds(1));
            check(Exited(afterCrash),"idle helper is automatically reaped without further user activity");

            var start=PluginWorker.StartInfo(Environment.ProcessPath!,typeof(GuideModel).Assembly.Location,session:true);
            using(var process=new Process{StartInfo=start})
            {
                process.Start();
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6));
                async Task<bool> Send(PluginProgram program)
                {
                    var text=JsonSerializer.Serialize(new{program,operation="generated",input=new{}},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
                    await PluginWorker.WriteFrameAsync(process.StandardInput.BaseStream,text,PluginWorker.MaxRequestBytes,deadline.Token);
                    var reply=await PluginWorker.ReadFrameAsync(process.StandardOutput.BaseStream,PluginWorker.MaxReplyBytes,deadline.Token);
                    using var parsed=JsonDocument.Parse(reply!);return parsed.RootElement.GetProperty("ok").GetBoolean();
                }
                try
                {
                    var accepted=await Send(Counter);
                    var foreign=await Send(new PluginProgram(1,"function invoke(){return '{}';}"));
                    await process.WaitForExitAsync(deadline.Token);
                    check(accepted && !foreign,"child independently refuses a different program on the existing session");
                }
                finally { if(!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            }
        }
        finally { await PluginWorker.ClearIdleAsync(); }
    }

    internal static async Task BenchmarkAsync(string executable)
    {
        executable=Path.GetFullPath(executable);
        var manifest=PluginCatalog.Load(Path.Combine(Path.GetDirectoryName(executable)!,"plugins")).ChatProvider!
            .Program ?? throw new InvalidDataException("benchmark_program");
        var input=new {source="generated old",find="old",replacement="new"};
        var request=JsonSerializer.Serialize(new{program=manifest,operation="task-transform",input},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        var fresh=new List<double>(); var warm=new List<double>(); var first=0d; var id=0;
        var hostFresh=new List<double>(); var hostWarm=new List<double>(); var hostFirst=0d;
        await PluginWorker.ClearIdleAsync();
        for(var i=0;i<12;i++)
        {
            var clock=Stopwatch.StartNew();
            var reply=await ObservationWorker.ExchangeAsync(PluginWorker.StartInfo(Environment.ProcessPath!,typeof(GuideModel).Assembly.Location),request,default);
            using var parsed=JsonDocument.Parse(reply);
            if(!parsed.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidDataException("benchmark_result");
            hostFresh.Add(clock.Elapsed.TotalMilliseconds);
        }
        try
        {
            for(var i=0;i<13;i++)
            {
                var clock=Stopwatch.StartNew();
                var reply=await PluginWorker.InvokeAsync(manifest,"task-transform",input,default);
                using var parsed=JsonDocument.Parse(reply);
                if(parsed.RootElement.GetProperty("text").GetString()!="generated new")throw new InvalidDataException("benchmark_result");
                if(i==0)hostFirst=clock.Elapsed.TotalMilliseconds;else hostWarm.Add(clock.Elapsed.TotalMilliseconds);
            }
        }
        finally { await PluginWorker.ClearIdleAsync(); }
        // Use the same self-contained file for both modes; the host entry point normally uses its own path.
        for(var i=0;i<12;i++)
        {
            var clock=Stopwatch.StartNew();
            var reply=await ObservationWorker.ExchangeAsync(PluginWorker.StartInfo(executable,""),request,default);
            using var parsed=JsonDocument.Parse(reply);
            if(!parsed.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidDataException("benchmark_result");
            fresh.Add(clock.Elapsed.TotalMilliseconds);
        }
        var sessionStart=PluginWorker.StartInfo(executable,"",session:true);
        using var process=new Process{StartInfo=sessionStart};
        try
        {
            var clock=Stopwatch.StartNew();process.Start();id=process.Id;
            for(var i=0;i<13;i++)
            {
                if(i>0) clock.Restart();
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await PluginWorker.WriteFrameAsync(process.StandardInput.BaseStream,request,PluginWorker.MaxRequestBytes,deadline.Token);
                var reply=await PluginWorker.ReadFrameAsync(process.StandardOutput.BaseStream,PluginWorker.MaxReplyBytes,deadline.Token);
                using var parsed=JsonDocument.Parse(reply!);
                if(!parsed.RootElement.GetProperty("ok").GetBoolean() || parsed.RootElement.GetProperty("result").GetProperty("text").GetString()!="generated new")
                    throw new InvalidDataException("benchmark_result");
                if(i==0)first=clock.Elapsed.TotalMilliseconds;else warm.Add(clock.Elapsed.TotalMilliseconds);
            }
            static double Median(List<double> values){var sorted=values.Order().ToArray();return (sorted[5]+sorted[6])/2;}
            process.Refresh();
            var proof=new{freshMs=fresh,warmMs=warm,firstMs=first,freshMedianMs=Median(fresh),warmMedianMs=Median(warm),
                productionHost=new{freshMs=hostFresh,warmMs=hostWarm,firstMs=hostFirst,freshMedianMs=Median(hostFresh),warmMedianMs=Median(hostWarm),
                    improvementPercent=100*(1-Median(hostWarm)/Median(hostFresh))},
                improvementPercent=100*(1-Median(warm)/Median(fresh)),workerPrivateBytes=process.PrivateMemorySize64,processId=id,
                desktop=false,model=false,limitations="Same packaged worker protocol with generated data, warm OS cache; excludes production host queue and first-call startup from warm samples."};
            var path=Path.Combine(Environment.CurrentDirectory,".artifacts","checks","warm-worker-benchmark.json");
            File.WriteAllText(path,JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(proof));
            if(Median(warm)>Median(fresh)*0.5 || Median(hostWarm)>Median(hostFresh)*0.5)
                throw new InvalidOperationException("Warm median must improve by at least 50 percent in packaged and production-host comparisons");
        }
        finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
    }
}
