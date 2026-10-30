using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public sealed record GuideObservation(IntPtr Window, uint ProcessId, Rect Frame,
    IReadOnlyList<GuideCandidate> Candidates, byte[]? Png, bool RawView = false, string PluginId = "",
    GuideScope? Scope = null, string? ScopeIntent = null, string? VisualHash = null, GuideVisual? Visual = null)
{
    public const string TaskDescription = "用户尚未说明目标。只根据本次所选窗口的可见内容，分别判断所属软件和当前界面用途；" +
        "两者独立判断，缺少依据的部分明确未知，不从普通界面风格猜测软件。即使没有可操作控件，也先说明可知的界面内容，" +
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

    public static GuideObservation Read(IntPtr window, bool includeImage, GuidePlugin plugin, CancellationToken token,
        string? intent = null, Rect? selection = null)
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
        var rule = intent is null || selection is not null ? null : plugin.ScopeFor(intent);
        var anchors = EnsureSafeContent(window, token, rule);
        var scope = selection is { } region ? GuideScope.FromUserSelection(frame, region) :
            rule is null ? null : GuideScope.Resolve(frame, rule, anchors);
        var candidates = ReadCandidates(window, token, rawView);
        var uiaMs = clock.ElapsedMilliseconds;
        ProbeLog.Log("guide_uia_complete", ("elapsedMs", uiaMs), ("candidates", candidates.Count));
        token.ThrowIfCancellationRequested();
        Rect? caret = null;
        var png = includeImage ? Capture.ReadTarget(window, scope?.Region, out caret) : null;
        if (includeImage)
        {
            var after = EnsureSafeContent(window, token, rule);
            if (rule is not null && scope is not null && !scope.HasSameAnchor(GuideScope.Resolve(frame, rule, after)))
                throw new InvalidDataException("target_changed");
        }
        ProbeLog.Log("guide_capture_complete", ("elapsedMs", clock.ElapsedMilliseconds - uiaMs),
            ("imageBytes", png?.Length ?? 0));
        if (!IsCurrent(window, pid, frame)) throw new InvalidDataException("target_changed");
        var hash = png is not null && (plugin.Kind == "general" || scope?.UserSelected == true) ? Fingerprint(png) : null;
        var visual = hash is not null ? GuideVisual.Read(png!, scope?.Region ?? frame, caret) : null;
        return new GuideObservation(window, pid, frame, candidates, png, rawView, plugin.Id, scope, intent, hash, visual);
    }

    public bool HasSameScope(GuideObservation current) => Scope is null ? current.Scope is null : Scope.HasSameAnchor(current.Scope);
    public bool HasSameVisual(GuideObservation current) => VisualHash is null || VisualHash == current.VisualHash ||
        Visual is not null && current.Visual is not null && Visual.Matches(current.Visual);
    internal static string Fingerprint(byte[] png) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png));

    public void ValidateTargetContext(GuideObservation current)
    {
        if (Window != current.Window || ProcessId != current.ProcessId || Frame != current.Frame ||
            PluginId != current.PluginId || RawView != current.RawView)
            throw new InvalidDataException("target_changed");
        ValidateContext(current.Candidates, null);
    }

    public Rect? ValidateContext(GuideObservation current, int? id)
    {
        ValidateTargetContext(current);
        if (ScopeIntent != current.ScopeIntent ||
            !HasSameScope(current) || !HasSameVisual(current)) throw new InvalidDataException("target_changed");
        return ValidateContext(current.Candidates, id);
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
        var cache = new CacheRequest { TreeFilter = Automation.RawViewCondition };
        // Traversal caches contain metadata only; content is read after eligibility checks.
        foreach (var property in new[] { AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.IsPasswordProperty, AutomationElement.ProcessIdProperty,
                     AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
                     AutomationElement.IsControlElementProperty }) cache.Add(property);
        // Walk the raw hierarchy explicitly so a filtered walker cannot descend through a hidden wrapper.
        var found = WalkElements(root, cache, token, TreeWalker.RawViewWalker);
        var frame = WindowRect(window);
        var candidates = new List<GuideCandidate>();
        var providerProcesses = new HashSet<int>();
        var scrollControls = 0;
        foreach (AutomationElement element in found)
        {
            token.ThrowIfCancellationRequested();
            var c = element.Cached;
            if (!types.Contains(c.ControlType) || !c.IsEnabled || c.IsOffscreen || !rawView && !c.IsControlElement) continue;
            providerProcesses.Add(c.ProcessId);
            if (!UsableBounds(c.BoundingRectangle, frame)) continue;
            if (IsScrollControl(element, root, token)) { scrollControls++; continue; }
            var name = ReadVisibleElementName(element, root, window, token);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200) continue;
            if (candidates.Count >= 128) throw new InvalidDataException("too_many_candidates");
            var automationId = element.Current.AutomationId;
            GuidePrivacy.EnsureTextSafe(automationId);
            var state = element.GetCurrentPropertyValue(TogglePattern.ToggleStateProperty, true);
            candidates.Add(new GuideCandidate(candidates.Count + 1, name, automationId,
                c.ControlType.ProgrammaticName, c.BoundingRectangle, element.GetRuntimeId(),
                state is ToggleState toggle ? toggle.ToString() : null));
        }
        ProbeLog.Log("guide_uia_scope", ("windowProcess", Native.ProcessId(window)),
            ("providerProcesses", string.Join(",", providerProcesses.Order())), ("excludedScrollControls", scrollControls));
        return candidates;
    }

    static IReadOnlyList<GuideScopeAnchor> EnsureSafeContent(IntPtr window, CancellationToken token, ObservationScopeRule? rule = null)
    {
        try
        {
            var root = AutomationElement.FromHandle(window) ?? throw new InvalidDataException("privacy_unavailable");
            var cache = new CacheRequest { TreeFilter = Automation.RawViewCondition };
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.IsPasswordProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            var elements = new[] { root.GetUpdatedCache(cache) }.Concat(
                WalkElements(root, cache, token, TreeWalker.RawViewWalker, false));
            var anchors = new List<GuideScopeAnchor>();
            foreach (var element in elements)
            {
                token.ThrowIfCancellationRequested();
                var name = ReadVisibleElementName(element, root, window, token);
                var type = element.Cached.ControlType.ProgrammaticName;
                if (name is not null && rule?.MatchesAnchor(name, type) == true)
                    anchors.Add(new GuideScopeAnchor(name, type, element.Current.BoundingRectangle, element.GetRuntimeId()));
            }
            return anchors;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException || ex is InvalidDataException { Message: "uia_traversal_limit" })
        {
            throw new InvalidDataException("privacy_unavailable");
        }
    }

    static string? ReadVisibleElementName(AutomationElement element, AutomationElement root, IntPtr window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var offscreen = element.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true);
        if (offscreen is true) return null;
        var password = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
        var role = element.Cached.ControlType.ProgrammaticName;
        var caption = (ReferenceEquals(password, AutomationElement.NotSupported) || ReferenceEquals(offscreen, AutomationElement.NotSupported)) &&
            (role is "ControlType.TitleBar" or "ControlType.MenuBar" or "ControlType.MenuItem" or "ControlType.Button") &&
            IsTitleBarChrome(element, root, window, token);
        return GuidePrivacy.ReadVisibleName(offscreen, password, () => element.Current.Name, role, caption);
    }

    static bool IsTitleBarChrome(AutomationElement element, AutomationElement root, IntPtr window, CancellationToken token)
    {
        var current = element;
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (Automation.Compare(current, root)) return false;
            if (current.Current.ControlType == ControlType.TitleBar) return true;
            // Hosted system captions can use a native child Window instead of a UIA TitleBar role.
            if (current.Current.ControlType == ControlType.Window &&
                current.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty, true) is int rawHandle && rawHandle != 0)
            {
                var handle = new IntPtr(rawHandle);
                if (Native.IsWindow(handle) && Native.Class(handle) == "ApplicationFrameTitleBarWindow" &&
                    Native.GetAncestor(handle, Native.GA_ROOT) == window && Native.ProcessId(handle) == Native.ProcessId(window))
                    return true;
            }
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return false;
    }

    static IEnumerable<AutomationElement> WalkElements(AutomationElement root, CacheRequest cache, CancellationToken token,
        TreeWalker walker, bool skipScrollBars = true) =>
        WalkVisible(root, parent => walker.GetFirstChild(parent, cache), child => walker.GetNextSibling(child, cache),
            child => child.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true),
            child => skipScrollBars && child.Cached.ControlType == ControlType.ScrollBar, token);

    internal static IEnumerable<T> WalkVisible<T>(T root, Func<T, T?> firstChild, Func<T, T?> nextSibling,
        Func<T, object?> offscreen, Func<T, bool> skip, CancellationToken token) where T : class
    {
        var visited = 0;
        return Walk(root, 0);

        IEnumerable<T> Walk(T parent, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (offscreen(parent) is true) yield break;
            for (var child = firstChild(parent); child is not null; child = nextSibling(child))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 4096 || depth >= 48) throw new InvalidDataException("uia_traversal_limit");
                if (offscreen(child) is true || skip(child)) continue;
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
