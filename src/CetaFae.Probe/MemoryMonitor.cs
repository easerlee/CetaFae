using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public sealed record MonitorSettings(bool Enabled = false, int High = 85, int Low = 75,
    int SustainedSeconds = 60, int CooldownMinutes = 30)
{
    internal MonitorSettings Validate()
    {
        if (High is < 50 or > 99 || Low is < 20 || Low >= High || SustainedSeconds is < 30 or > 3600 ||
            CooldownMinutes is < 5 or > 1440) throw new ArgumentException("monitor_settings");
        return this;
    }
}

internal sealed record MemoryReading(ulong Total, ulong Available)
{
    internal double UsedPercent => 100d * (Total - Available) / Total;
}

internal sealed record MemoryPolicyState(double? HighSince = null, double? LowSince = null,
    double? LastSample = null, double? LastNotice = null, bool Armed = true);

/// <summary>Only total-memory counters run passively. Feature policy runs in the disposable TS worker.</summary>
internal sealed class MemoryMonitor : IDisposable
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    readonly CancellationTokenSource _stop = new();
    readonly GuidePlugin _plugin;
    readonly MonitorSettings _config;
    readonly Func<bool> _suppressed;
    readonly Action<MemoryReading> _notify;
    MemoryPolicyState _state = new();
    double _ignoredUntil;
    internal MemoryMonitor(GuidePlugin plugin, MonitorSettings config, Func<bool> suppressed, Action<MemoryReading> notify)
    {
        _plugin = plugin; _config = config.Validate(); _suppressed = suppressed; _notify = notify;
        if (config.Enabled) _ = RunAsync();
    }
    internal void Ignore() => _ignoredUntil = Environment.TickCount64 + TimeSpan.FromHours(2).TotalMilliseconds;
    async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var reading = Read();
                var result = await EvaluateAsync(_plugin, _config, _state, Environment.TickCount64,
                    reading.UsedPercent, _suppressed(), _ignoredUntil, _stop.Token);
                _state = result.State;
                if (result.Remind && !_stop.IsCancellationRequested && !_suppressed()) _notify(reading);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or ArgumentException)
        { ProbeLog.Log("memory_monitor_stopped", ("type", ex.GetType().Name)); }
    }
    internal static async Task<(MemoryPolicyState State, bool Remind)> EvaluateAsync(GuidePlugin plugin,
        MonitorSettings config, MemoryPolicyState state, double now, double usedPercent, bool suppressed,
        double ignoredUntil, CancellationToken token = default)
    {
        config.Validate();
        if (!double.IsFinite(now) || !double.IsFinite(usedPercent) || usedPercent is < 0 or > 100 ||
            !double.IsFinite(ignoredUntil)) throw new InvalidDataException("memory_sample");
        var raw = await GuideFeature.InvokeAsync(plugin, "memory-policy", new { memory = new { now, usedPercent,
            suppressed, ignoredUntil, config, state } }, token);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "memory", "remind" }))
            throw new InvalidDataException("memory_policy");
        var next = root.GetProperty("memory").Deserialize<MemoryPolicyState>(Json) ?? throw new InvalidDataException("memory_policy");
        if (new[] { next.HighSince, next.LowSince, next.LastSample, next.LastNotice }.Any(n =>
            n.HasValue && (!double.IsFinite(n.Value) || n.Value < 0 || n.Value > now)))
            throw new InvalidDataException("memory_policy");
        return (next, root.GetProperty("remind").GetBoolean() && config.Enabled && !suppressed && now >= ignoredUntil);
    }
    internal static MemoryReading Read()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 || status.AvailablePhysical > status.TotalPhysical)
            throw new IOException("memory_counter_unavailable");
        return new(status.TotalPhysical, status.AvailablePhysical);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public void Dispose() { _stop.Cancel(); }
}
