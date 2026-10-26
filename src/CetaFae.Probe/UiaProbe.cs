using System.IO;
using System.Text.Json;
using System.Windows.Automation;

namespace CetaFae.Probe;

/// <summary>TC-1：把目标窗口的 UIA 控件树落盘，用于算坐标命中率与定位稳定性。</summary>
public static class UiaProbe
{
    public sealed record Row(
        int Depth,
        string ControlType,
        string Name,
        string AutoId,
        string ClassName,
        string Rect,
        bool Offscreen,
        bool Enabled,
        bool Focusable,
        bool Clickable,
        string Patterns);

    static readonly (string Name, AutomationPattern Pattern)[] PatternSet =
    {
        ("Invoke", InvokePattern.Pattern),
        ("SelectionItem", SelectionItemPattern.Pattern),
        ("Toggle", TogglePattern.Pattern),
        ("ExpandCollapse", ExpandCollapsePattern.Pattern),
        ("Value", ValuePattern.Pattern),
        ("ScrollItem", ScrollItemPattern.Pattern),
    };

    /// <summary>返回节点数；读不到返回 -1。窗口已失效、读取失败、内容很少是三件事，分开记。</summary>
    public static int Dump(IntPtr hwnd, string outPath, int maxDepth, int maxNodes, bool rawView = false)
    {
        if (!Native.IsWindow(hwnd))
        {
            ProbeLog.Log("uia_window_gone", ("window", Native.Hex(hwnd)));
            return -1;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception ex)
        {
            ProbeLog.Log("uia_read_failed", ("window", Native.Hex(hwnd)), ("error", ex.GetType().Name), ("message", ex.Message));
            return -1;
        }
        if (root is null)
        {
            ProbeLog.Log("uia_read_failed", ("window", Native.Hex(hwnd)), ("error", "null_root"));
            return -1;
        }

        var rows = new List<Row>();
        Walk(root, 0, maxDepth, maxNodes, rows, rawView ? TreeWalker.RawViewWalker : TreeWalker.ControlViewWalker);

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (var writer = new StreamWriter(outPath, false))
        {
            foreach (var row in rows)
                writer.WriteLine(JsonSerializer.Serialize(row));
        }

        ProbeLog.Log("uia_dump",
            ("window", Native.Hex(hwnd)),
            ("title", Native.Title(hwnd)),
            ("out", Path.GetFullPath(outPath)),
            ("nodes", rows.Count),
            ("view", rawView ? "raw" : "control"),
            ("depthLimit", maxDepth),
            ("elapsedMs", clock.ElapsedMilliseconds));

        // 节点少只是告警。简单弹窗可以只有几个节点，不能据此判窗口失效。
        if (rows.Count < 10)
            ProbeLog.Log("uia_small_tree", ("nodes", rows.Count), ("window", Native.Hex(hwnd)));

        return rows.Count;
    }

    static void Walk(AutomationElement element, int depth, int maxDepth, int maxNodes, List<Row> rows, TreeWalker walker)
    {
        if (depth > maxDepth || rows.Count >= maxNodes) return;

        rows.Add(Read(element, depth));
        if (depth == maxDepth) return;

        AutomationElement? child;
        try { child = walker.GetFirstChild(element); }
        catch { return; }

        while (child is not null && rows.Count < maxNodes)
        {
            Walk(child, depth + 1, maxDepth, maxNodes, rows, walker);
            try { child = walker.GetNextSibling(child); }
            catch { break; }
        }
    }

    static Row Read(AutomationElement element, int depth)
    {
        string name = "", autoId = "", cls = "", type = "";
        string rect = "";
        bool offscreen = true, enabled = false, focusable = false;

        try
        {
            var c = element.Current;
            name = c.Name ?? "";
            autoId = c.AutomationId ?? "";
            cls = c.ClassName ?? "";
            type = c.ControlType?.ProgrammaticName ?? "";
            offscreen = c.IsOffscreen;
            enabled = c.IsEnabled;
            focusable = c.IsKeyboardFocusable;
            var r = c.BoundingRectangle;
            rect = r.IsEmpty ? "" : $"{r.Left},{r.Top},{r.Width},{r.Height}";
        }
        catch
        {
            // 元素在枚举途中消失：保留空行，后面统计会把它算成不可定位。
        }

        var patterns = new List<string>();
        foreach (var (label, pattern) in PatternSet)
        {
            try
            {
                if (element.TryGetCurrentPattern(pattern, out _)) patterns.Add(label);
            }
            catch
            {
                // 忽略：某些元素在被查询时已失效。
            }
        }

        var clickable = patterns.Contains("Invoke") || patterns.Contains("SelectionItem")
                        || patterns.Contains("Toggle") || patterns.Contains("ExpandCollapse");

        return new Row(depth, type, name, autoId, cls, rect, offscreen, enabled, focusable, clickable,
            string.Join("|", patterns));
    }
}
