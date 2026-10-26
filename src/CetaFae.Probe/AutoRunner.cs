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

            if (!await EnsureForeground(target))
            {
                ProbeLog.Log("auto_abort", ("reason", "target_not_foreground"), ("fg", Native.Hex(Native.GetForegroundWindow())));
                break;
            }
            await Task.Delay(200);

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

            if (ball.TargetHwnd == target)
            {
                await FollowProbe(ball, target);
                if (i == 1) await AnimatedMove(ball, target);
            }
            else ProbeLog.Log("trial_miss", ("n", i), ("tracked", Native.Hex(ball.TargetHwnd)), ("target", Native.Hex(target)));

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

    /// <summary>连续拖动目标窗口（40 步，每步 4px），看事件流下描边跟不跟得上。</summary>
    static async Task AnimatedMove(BallWindow ball, IntPtr target)
    {
        var start = Native.Frame(target);
        int w = start.Right - start.Left, h = start.Bottom - start.Top;
        var before = ball.OverlayUpdates;

        for (var i = 1; i <= 40; i++)
        {
            Native.SetWindowPos(target, IntPtr.Zero, start.Left + i * 4, start.Top + i, w, h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            await Task.Delay(8);
        }

        await Task.Delay(250);
        var t = Native.Frame(target);
        ProbeLog.Log("animate",
            ("moves", 40),
            ("overlayUpdates", ball.OverlayUpdates - before),
            ("maxLatencyMs", ball.MaxLatencyMs),
            ("targetRectPx", $"{t.Left},{t.Top},{t.Right - t.Left},{t.Bottom - t.Top}"),
            ("overlayPx", ball.OverlayPx()),
            ("fg", Native.Hex(Native.GetForegroundWindow())));

        Native.SetWindowPos(target, IntPtr.Zero, start.Left, start.Top, w, h,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        await Task.Delay(200);
    }

    /// <summary>前台锁会把 SetForegroundWindow 吃掉，先敲一下 ALT 解除锁再试。</summary>
    static async Task<bool> EnsureForeground(IntPtr target)
    {
        for (var i = 0; i < 3; i++)
        {
            if (Native.GetForegroundWindow() == target) return true;
            Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.SetForegroundWindow(target);
            await Task.Delay(200);
        }
        return Native.GetForegroundWindow() == target;
    }

    /// <summary>TC-4：跟踪期间挪动、缩放目标窗口，逐次比对描边与目标外框的像素差。</summary>
    static async Task FollowProbe(BallWindow ball, IntPtr target)
    {
        var start = Native.Frame(target);
        int w0 = start.Right - start.Left, h0 = start.Bottom - start.Top;
        var moves = new[]
        {
            (60, 40, 0, 0), (-120, -80, 0, 0), (0, 0, 120, 90),
            (0, 0, -160, -110), (200, 0, 0, 0), (-200, 0, 0, 0)
        };

        for (var i = 0; i < moves.Length; i++)
        {
            var (dx, dy, dw, dh) = moves[i];
            Native.SetWindowPos(target, IntPtr.Zero,
                start.Left + dx, start.Top + dy,
                Math.Max(320, w0 + dw), Math.Max(240, h0 + dh),
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            await Task.Delay(180);

            var t = Native.Frame(target);
            var o = ball.OverlayPx().Split(',');
            int ol = int.Parse(o[0]), ot = int.Parse(o[1]), ow = int.Parse(o[2]), oh = int.Parse(o[3]);

            ProbeLog.Log("follow",
                ("n", i + 1),
                ("targetRectPx", $"{t.Left},{t.Top},{t.Right - t.Left},{t.Bottom - t.Top}"),
                ("overlayPx", ball.OverlayPx()),
                ("dLeft", ol - t.Left),
                ("dTop", ot - t.Top),
                ("dWidth", ow - (t.Right - t.Left)),
                ("dHeight", oh - (t.Bottom - t.Top)),
                ("overlayUpdates", ball.OverlayUpdates),
                ("fg", Native.Hex(Native.GetForegroundWindow())));
        }

        Native.SetWindowPos(target, IntPtr.Zero, start.Left, start.Top, w0, h0,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        await Task.Delay(150);
        ProbeLog.Log("follow_end", ("restored", true));
    }

    /// <summary>只跑跟窗与连续拖动，不碰鼠标输入。用于 TC-4 单独取证。</summary>
    public static async Task FollowOnly(BallWindow ball, IntPtr target)
    {
        var r = Native.Frame(target);
        ProbeLog.Log("follow_only_start",
            ("target", Native.Hex(target)),
            ("class", Native.Class(target)),
            ("rect", $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}"),
            ("fg", Native.Hex(Native.GetForegroundWindow())));

        await FollowProbe(ball, target);
        await AnimatedMove(ball, target);
        await AnimatedMove(ball, target);

        ProbeLog.Log("follow_only_end",
            ("overlayUpdates", ball.OverlayUpdates),
            ("maxLatencyMs", ball.MaxLatencyMs),
            ("coalesced", ball.Coalesced),
            ("fg", Native.Hex(Native.GetForegroundWindow())));
    }

    public static IntPtr Find(string fragment) => FindWindow(fragment);

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
