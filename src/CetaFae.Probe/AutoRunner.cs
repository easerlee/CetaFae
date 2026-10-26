namespace CetaFae.Probe;

/// <summary>
/// 合成输入驱动的自动批次。每次试验记三件事：拖动前后前台窗口、落点命中率、Esc 后标记是否清干净。
/// </summary>
public static class AutoRunner
{
    public static async Task RunAsync(BallWindow ball, string titleFragment, int trials, int dwellMs)
    {
        ProbeLog.Log("auto_start",
            ("title", titleFragment),
            ("trials", trials),
            ("dwellMs", dwellMs),
            ("fg", Native.Hex(Native.GetForegroundWindow())));

        for (var i = 1; i <= trials; i++)
        {
            var target = FindWindow(titleFragment);
            if (target == IntPtr.Zero)
            {
                ProbeLog.Log("auto_abort", ("reason", "target_not_found"));
                break;
            }

            Native.SetForegroundWindow(target);
            await Task.Delay(250);

            var (bl, bt, bw, bh) = ball.BallRectPx();
            int fromX = bl + bw / 2, fromY = bt + bh / 2;
            var r = Native.Frame(target);
            int toX = r.Left + (r.Right - r.Left) / 2;
            int toY = r.Top + (r.Bottom - r.Top) / 2;

            var fgBefore = Native.GetForegroundWindow();
            ProbeLog.Log("trial_start",
                ("n", i),
                ("target", Native.Hex(target)),
                ("targetClass", Native.Class(target)),
                ("fgBefore", Native.Hex(fgBefore)),
                ("fgIsTarget", fgBefore == target),
                ("from", $"{fromX},{fromY}"),
                ("to", $"{toX},{toY}"));

            Native.SetCursorPos(fromX, fromY);
            await Task.Delay(60);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);

            const int steps = 24;
            for (var s = 1; s <= steps; s++)
            {
                Native.SetCursorPos(fromX + (toX - fromX) * s / steps, fromY + (toY - fromY) * s / steps);
                await Task.Delay(10);
            }

            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(dwellMs);

            var fgAfter = Native.GetForegroundWindow();
            ProbeLog.Log("trial_end",
                ("n", i),
                ("fgAfter", Native.Hex(fgAfter)),
                ("fgChanged", fgAfter != fgBefore),
                ("tracked", Native.Hex(ball.TargetHwnd)),
                ("overlayUpdates", ball.OverlayUpdates));

            Native.keybd_event((byte)Native.VK_ESCAPE, 0, 0, UIntPtr.Zero);
            Native.keybd_event((byte)Native.VK_ESCAPE, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(150);

            var stillTracked = ball.TargetHwnd;
            ProbeLog.Log("trial_esc",
                ("n", i),
                ("trackedAfterEsc", Native.Hex(stillTracked)),
                ("overlayVisible", ball.OverlayVisible));
            if (stillTracked != IntPtr.Zero) ball.StopTracking("auto_fallback");

            await Task.Delay(200);
        }

        ProbeLog.Log("auto_end", ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    static IntPtr FindWindow(string fragment)
    {
        var found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            var title = Native.Title(hwnd);
            if (title.Length > 0 && title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
