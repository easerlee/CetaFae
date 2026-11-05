using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using CetaFae.Plugins;

namespace CetaFae.Probe;

/// <summary>Only host-selected window/control identities; features cannot inject coordinates or runtime IDs.</summary>
internal static class PlatformDesktop
{
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    internal static (double X, double Y) Point(PlatformPoint point, Rect frame)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            frame != new Rect(point.Left, point.Top, point.Width, point.Height) ||
            !frame.Contains(point.X, point.Y)) throw new PlatformFailure("point_changed");
        return (point.X, point.Y);
    }

    internal static PlatformResult Execute(PlatformRequest call, PlatformScope scope)
    {
        var id = PlatformEngine.Text(call.Parameters, "windowId", max: 64);
        var target = scope.Windows.SingleOrDefault(w => w.Id == id) ?? throw new PlatformFailure("resource_denied");
        var window = new IntPtr(target.Handle);
        using var process = scope.Process(target.Process);
        void Current(bool foreground)
        {
            scope.Permit(call.Operation);
            if (!Native.IsWindow(window) || Native.ProcessId(window) != target.Process.Id || !Native.IsWindowVisible(window) || Native.IsIconic(window))
                throw new PlatformFailure("target_changed");
            // UIPI and secure desktops remain OS boundaries, even for an explicitly selected target.
            if (call.Operation.StartsWith("ui.", StringComparison.Ordinal) && !Native.IsNonElevated(window) && !PlatformEngine.IsElevated())
                throw new PlatformFailure("higher_integrity_target");
            if (foreground && Native.GetForegroundWindow() != window) throw new PlatformFailure("target_not_foreground");
        }
        Current(false);
        if (call.Operation == "window.activate")
        {
            _ = Native.SetForegroundWindow(window);
            return Native.GetForegroundWindow() == window ? PlatformEngine.Result(call, "verified", "foreground_verified") :
                PlatformEngine.Result(call, "denied", "foreground_restricted");
        }
        if (!Native.GetWindowRect(window, out var rectangle)) throw new PlatformFailure("target_changed");
        var frame = new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        if (call.Operation == "window.inspect") return PlatformEngine.Result(call, "observed", "window_observed",
            new { x = frame.X, y = frame.Y, width = frame.Width, height = frame.Height, foreground = Native.GetForegroundWindow() == window });
        Current(true);
        if (call.Operation == "ui.point")
        {
            var pointId = PlatformEngine.Text(call.Parameters, "pointId", max: 64);
            var pointLease = target.Points?.SingleOrDefault(point => point.Id == pointId) ?? throw new PlatformFailure("resource_denied");
            var point = Point(pointLease, frame); Current(true); Click(point.X, point.Y);
            return PlatformEngine.Result(call, "dispatched", "input_sent_postcondition_required");
        }
        if (call.Operation is "ui.text" or "ui.key")
        {
            // Explicit raw-input authority works without an accessibility provider; semantic value writes use their separate control grant.
            var inputs = new List<Input>();
            if (call.Operation == "ui.text")
            {
                var text = PlatformEngine.Text(call.Parameters, "text", max: 1000); GuidePrivacy.EnsureTextSafe(text);
                foreach (var c in text) { inputs.Add(Key(0, c, 4)); inputs.Add(Key(0, c, 6)); }
            }
            else
            {
                var key = PlatformEngine.Text(call.Parameters, "key");
                ushort code = key switch { "Enter" => 13, "Escape" => 27, "Tab" => 9, "Left" => 37, "Up" => 38, "Right" => 39, "Down" => 40,
                    _ => throw new PlatformFailure("unsupported_key") };
                inputs.Add(Key(code, '\0', 0)); inputs.Add(Key(code, '\0', 2));
            }
            Current(true); Send(inputs.ToArray());
            return PlatformEngine.Result(call, "dispatched", "input_sent_postcondition_required");
        }
        var controlId = PlatformEngine.Text(call.Parameters, "controlId", max: 64);
        var selected = target.Controls.SingleOrDefault(c => c.Id == controlId) ?? throw new PlatformFailure("resource_denied");
        var root = AutomationElement.FromHandle(window);
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? found = null;
        var pending = new Stack<AutomationElement>(); pending.Push(root);
        for (var i = 0; pending.Count > 0 && i < 512; i++)
        {
            var node = pending.Pop();
            if (node.GetRuntimeId().SequenceEqual(selected.RuntimeId)) { found = node; break; }
            for (var child = walker.GetFirstChild(node); child is not null; child = walker.GetNextSibling(child))
            { if (pending.Count >= 512) throw new PlatformFailure("control_limit"); pending.Push(child); }
        }
        var control = found ?? throw new PlatformFailure("control_changed");
        var properties = control.Current;
        var bounds = properties.BoundingRectangle;
        if (!properties.IsEnabled || properties.IsOffscreen || properties.IsPassword || bounds.IsEmpty || !frame.Contains(bounds))
            throw new PlatformFailure("control_unavailable");
        Current(true);
        switch (call.Operation)
        {
            case "ui.invoke":
                if (!control.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) throw new PlatformFailure("pattern_unavailable");
                ((InvokePattern)invoke).Invoke(); break;
            case "ui.setValue":
                if (!control.TryGetCurrentPattern(ValuePattern.Pattern, out var value) || ((ValuePattern)value).Current.IsReadOnly)
                    throw new PlatformFailure("pattern_unavailable");
                var text = PlatformEngine.Text(call.Parameters, "text", true, 1000); GuidePrivacy.EnsureTextSafe(text);
                ((ValuePattern)value).SetValue(text);
                return ((ValuePattern)value).Current.Value == text ? PlatformEngine.Result(call, "verified", "value_verified") :
                    PlatformEngine.Result(call, "outcome_unknown", "value_not_verified");
            case "ui.scroll":
                if (!control.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll)) throw new PlatformFailure("pattern_unavailable");
                var direction = PlatformEngine.Text(call.Parameters, "direction");
                ((ScrollPattern)scroll).Scroll(ScrollAmount.NoAmount, direction switch { "up" => ScrollAmount.SmallDecrement,
                    "down" => ScrollAmount.SmallIncrement, _ => throw new PlatformFailure("invalid_parameters") }); break;
            case "ui.click":
                Click(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); break;
            default: throw new PlatformFailure("unsupported_operation");
        }
        return PlatformEngine.Result(call, "dispatched", "action_sent_postcondition_required");
    }
    static Input Key(ushort key, char scan, uint flags) => new() { Type = 1, Data = new() { Key = new() { Key = key, Scan = scan, Flags = flags } } };
    static void Click(double pointX, double pointY)
    {
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (width < 2 || height < 2) throw new PlatformFailure("desktop_unavailable");
        var x = (int)Math.Round((pointX - GetSystemMetrics(76)) * 65535 / (width - 1));
        var y = (int)Math.Round((pointY - GetSystemMetrics(77)) * 65535 / (height - 1));
        if (x is < 0 or > 65535 || y is < 0 or > 65535) throw new PlatformFailure("point_changed");
        Send([new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = 0xC001 } } },
            new() { Data = new() { Mouse = new() { Flags = 2 } } }, new() { Data = new() { Mouse = new() { Flags = 4 } } }]);
    }
    static void Send(Input[] inputs)
    {
        if (new[] { 16, 17, 18, 91, 92 }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0)) throw new PlatformFailure("modifier_key_held");
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length) throw new InvalidOperationException("input_not_confirmed");
    }
}
