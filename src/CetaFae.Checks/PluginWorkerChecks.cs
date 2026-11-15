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
