using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public sealed record GuideObservation(IntPtr Window, uint ProcessId, Rect Frame,
    IReadOnlyList<GuideCandidate> Candidates, byte[]? Png, bool RawView = false, string PluginId = "")
{
    public const string TaskDescription = "用户尚未说明目标。只根据本次所选窗口的可见内容，用一句话说明当前界面，" +
        "再用一句短问询问用户希望在此界面处理什么。不要引入画面和用户目标之外的应用、设置或任务，" +
        "不猜测目标，不预先指向控件，不执行操作。";

    public static Rect WindowRect(IntPtr window)
    {
        var r = Native.Frame(window);
        return new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
    }

    public static bool IsCurrent(IntPtr window, uint processId, Rect frame) =>
        Native.IsWindow(window) && Native.IsWindowVisible(window) && !Native.IsIconic(window) &&
        Native.GetForegroundWindow() == window && Native.ProcessId(window) == processId &&
        WindowRect(window) == frame;

    public static GuideObservation Read(IntPtr window, bool includeImage, GuidePlugin plugin, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        ProbeLog.Log("guide_observe_start");
        token.ThrowIfCancellationRequested();
        var foreground = Native.GetForegroundWindow();
        if (foreground != window)
        {
            ProbeLog.Log("guide_target_not_foreground", ("target", Native.Hex(window)),
                ("fg", Native.Hex(foreground)));
            throw new InvalidDataException("target_not_foreground");
        }
        var pid = Native.ProcessId(window);
        var frame = WindowRect(window);
        if (!IsCurrent(window, pid, frame) || frame.IsEmpty || frame.Width < 1 || frame.Height < 1)
            throw new InvalidDataException("target_unavailable");
        using var process = Process.GetProcessById((int)pid);
        if (!plugin.Matches(process.ProcessName, Native.Title(window)))
            throw new InvalidDataException("unsupported_target");
        if (!Native.IsNonElevated(window)) throw new InvalidDataException("elevated_target");
        var rawView = plugin.UsesRawView;
        var candidates = ReadCandidates(window, token, rawView);
        var uiaMs = clock.ElapsedMilliseconds;
        ProbeLog.Log("guide_uia_complete", ("elapsedMs", uiaMs), ("candidates", candidates.Count));
        token.ThrowIfCancellationRequested();
        var png = includeImage ? Capture.ReadTarget(window) : null;
        ProbeLog.Log("guide_capture_complete", ("elapsedMs", clock.ElapsedMilliseconds - uiaMs),
            ("imageBytes", png?.Length ?? 0));
        if (!IsCurrent(window, pid, frame)) throw new InvalidDataException("target_changed");
        return new GuideObservation(window, pid, frame, candidates, png, rawView, plugin.Id);
    }

    public static bool HasSameContext(IReadOnlyList<GuideCandidate> previous,
        IReadOnlyList<GuideCandidate> current) => previous.Count == current.Count &&
        previous.Zip(current).All(pair => pair.First.RuntimeId.SequenceEqual(pair.Second.RuntimeId) &&
            pair.First.Name == pair.Second.Name && pair.First.AutomationId == pair.Second.AutomationId &&
            pair.First.ControlType == pair.Second.ControlType && pair.First.Bounds == pair.Second.Bounds &&
            pair.First.ToggleState == pair.Second.ToggleState);

    public static void LogContextDifference(IReadOnlyList<GuideCandidate> previous,
        IReadOnlyList<GuideCandidate> current)
    {
        var matches = previous.Select(old => (Old: old,
            Current: current.FirstOrDefault(c => c.RuntimeId.SequenceEqual(old.RuntimeId))))
            .Where(pair => pair.Current is not null).ToArray();
        ProbeLog.Log("guide_context_difference", ("previousCount", previous.Count), ("currentCount", current.Count),
            ("sameRuntimeIds", matches.Length),
            ("nameChanges", matches.Count(p => p.Old.Name != p.Current!.Name)),
            ("automationIdChanges", matches.Count(p => p.Old.AutomationId != p.Current!.AutomationId)),
            ("typeChanges", matches.Count(p => p.Old.ControlType != p.Current!.ControlType)),
            ("boundsChanges", matches.Count(p => p.Old.Bounds != p.Current!.Bounds)),
            ("addedTypes", string.Join(",", current.Where(c => !previous.Any(p => p.RuntimeId.SequenceEqual(c.RuntimeId)))
                .GroupBy(c => c.ControlType).Select(g => $"{g.Key}:{g.Count()}"))),
            ("addedBounds", string.Join(";", current.Where(c => !previous.Any(p => p.RuntimeId.SequenceEqual(c.RuntimeId)))
                .Select(c => c.Bounds.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            ("removedTypes", string.Join(",", previous.Where(p => !current.Any(c => c.RuntimeId.SequenceEqual(p.RuntimeId)))
                .GroupBy(c => c.ControlType).Select(g => $"{g.Key}:{g.Count()}"))));
    }

    public Rect? ValidateContext(IReadOnlyList<GuideCandidate> current, int? id)
    {
        if (!HasSameContext(Candidates, current))
        {
            LogContextDifference(Candidates, current);
            throw new InvalidDataException("target_changed");
        }
        if (id is null) return null;
        var candidate = Candidates.Single(c => c.Id == id);
        return candidate.Bounds;
    }

    public static IReadOnlyList<GuideCandidate> ReadCandidates(IntPtr window, CancellationToken token, bool rawView = false)
    {
        var root = AutomationElement.FromHandle(window) ?? throw new InvalidDataException("uia_unavailable");
        var types = new[] { ControlType.Button, ControlType.ComboBox, ControlType.Hyperlink,
            ControlType.ListItem, ControlType.MenuItem, ControlType.CheckBox, ControlType.RadioButton };
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new OrCondition(types.Select(t => new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray()));
        var cache = new CacheRequest { TreeFilter = Automation.RawViewCondition };
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
                     AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.IsPasswordProperty, AutomationElement.ProcessIdProperty,
                     AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
                     TogglePattern.ToggleStateProperty }) cache.Add(property);
        IEnumerable<AutomationElement> found;
        if (rawView) found = WalkRaw(root, cache, token);
        else
        {
            AutomationElementCollection matches;
            using (cache.Activate()) matches = root.FindAll(TreeScope.Descendants, condition);
            if (matches.Count > 128) throw new InvalidDataException("too_many_candidates");
            found = matches.Cast<AutomationElement>();
        }
        var frame = WindowRect(window);
        var candidates = new List<GuideCandidate>();
        var providerProcesses = new HashSet<int>();
        var scrollControls = 0;
        foreach (AutomationElement element in found)
        {
            token.ThrowIfCancellationRequested();
            var c = element.Cached;
            if (!types.Contains(c.ControlType) || !c.IsEnabled || c.IsOffscreen) continue;
            providerProcesses.Add(c.ProcessId);
            if (c.IsPassword || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200 ||
                !UsableBounds(c.BoundingRectangle, frame)) continue;
            if (IsScrollControl(element, root, token)) { scrollControls++; continue; }
            if (candidates.Count >= 128) throw new InvalidDataException("too_many_candidates");
            var state = element.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true);
            candidates.Add(new GuideCandidate(candidates.Count + 1, c.Name, c.AutomationId,
                c.ControlType.ProgrammaticName, c.BoundingRectangle, element.GetRuntimeId(),
                state is ToggleState toggle ? toggle.ToString() : null));
        }
        ProbeLog.Log("guide_uia_scope", ("windowProcess", Native.ProcessId(window)),
            ("providerProcesses", string.Join(",", providerProcesses.Order())), ("excludedScrollControls", scrollControls));
        return candidates;
    }

    static IEnumerable<AutomationElement> WalkRaw(AutomationElement root, CacheRequest cache, CancellationToken token)
    {
        var visited = 0;
        return Walk(root, 0);

        IEnumerable<AutomationElement> Walk(AutomationElement parent, int depth)
        {
            var walker = TreeWalker.RawViewWalker;
            for (var child = walker.GetFirstChild(parent, cache); child is not null;
                 child = walker.GetNextSibling(child, cache))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 4096 || depth >= 48) throw new InvalidDataException("uia_traversal_limit");
                if (child.Cached.ControlType == ControlType.ScrollBar) continue;
                yield return child;
                foreach (var descendant in Walk(child, depth + 1)) yield return descendant;
            }
        }
    }

    static bool IsScrollControl(AutomationElement element, AutomationElement root, CancellationToken token)
    {
        var walker = TreeWalker.ControlViewWalker;
        var parent = walker.GetParent(element);
        for (var depth = 0; depth < 32 && parent is not null; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (Automation.Compare(parent, root)) return false;
            if (parent.Current.ControlType == ControlType.ScrollBar) return true;
            parent = walker.GetParent(parent);
        }
        if (parent is not null) throw new InvalidDataException("uia_ancestry_limit");
        return false;
    }

    public static bool UsableBounds(Rect bounds, Rect frame) =>
        !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0 && frame.Contains(bounds);

}
