using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

/// <summary>Generated task oracles, independent of model output and native application versions.</summary>
static class TaskChecks
{
    sealed record TaskFamily(string Id, string Process, string Title, string Page, string Goal, string State,
        string[] Controls, string? Fact = null, string? Satisfied = null, string Type = "ControlType.Button");
    sealed record TaskCase(TaskFamily Family, string StateId, string State, string? Intent,
        GuideCandidate[] Candidates, string Mode, int? CandidateId, string? Fact);
    static readonly Rect Frame = new(0, 0, 820, 560);
    static readonly TaskFamily[] Families =
    [
        new("S-01", "generated-app", "生成错误", "应用程序错误", "解释这个错误，并指出关闭提示返回的入口。",
            "应用程序无法正常启动(0xc000007b)。单击确定关闭应用程序。", ["确定", "详细信息"], "0xc000007b"),
        new("S-02", "generated-app", "生成占用提示", "文件正在使用", "我要保留文件，取消当前操作并返回。",
            "操作无法完成，因为文件已在另一个程序中打开。", ["取消", "重试"]),
        new("S-03", "generated-app", "生成权限提示", "文件访问被拒绝", "解释权限限制，指出取消入口；不申请提权。",
            "你需要管理员权限才能删除此文件。", ["取消", "继续"]),
        new("S-04", "generated-app", "生成未响应提示", "程序未响应", "我要继续等待程序响应，不关闭程序。",
            "生成程序没有响应。可以继续等待或关闭程序。", ["等待程序响应", "关闭程序"]),
        new("S-05", "generated-app", "生成备份提示", "备份状态", "我要查看当前备份的详细进度，不取消备份。",
            "备份正在进行，已完成40%。", ["详细信息", "取消备份"]),
        new("S-06", "generated-app", "生成连接提示", "连接失败", "解释连接错误，并指出重试入口；不修改网络配置。",
            "无法连接到服务器，错误代码0x80072EE7。", ["重试", "取消"], "0x80072EE7"),
        new("W-01", "SystemSettings", "设置", "设置 > Windows 更新", "我想暂停更新，若已暂停只解释当前状态。",
            "当前未暂停更新。", ["暂停更新", "检查更新"], Satisfied: "更新已暂停，直到2026/10/29为止。"),
        new("W-02", "SystemSettings", "设置", "设置 > 默认应用 > 按协议选择默认值", "HTTP协议的默认应用选择入口在哪里？只指出位置。",
            "HTTP当前使用生成浏览器，HTTPS当前使用另一浏览器。", ["HTTP 默认应用：生成浏览器", "HTTPS 默认应用：另一浏览器"]),
        new("W-03", "SystemSettings", "设置", "设置 > 系统 > 通知", "我要开启生成日历的通知；若已开启只解释现状。",
            "生成日历通知：关闭。", ["生成日历 通知", "其他应用 通知"], Satisfied: "生成日历通知：开启。", Type: "ControlType.CheckBox"),
        new("W-04", "SystemSettings", "设置", "设置 > 网络和 Internet > 代理", "手动设置代理服务器的入口在哪里？只指出位置。",
            "手动代理关闭；自动检测设置开启。", ["使用代理服务器 设置", "自动检测设置"]),
        new("W-05", "SystemSettings", "设置", "设置 > 蓝牙和设备", "添加新设备的入口在哪里？不自动配对。",
            "蓝牙已开启。", ["添加设备", "蓝牙"]),
        new("W-06", "SystemSettings", "设置", "设置 > 隐私和安全性 > 麦克风", "我要允许生成录音机使用麦克风；若已允许只解释。",
            "生成录音机麦克风权限：关闭。", ["生成录音机 麦克风权限", "其他应用 麦克风权限"],
            Satisfied: "生成录音机麦克风权限：开启。", Type: "ControlType.CheckBox"),
        new("E-01", "msedge", "设置", "Edge 设置 > 网站权限 > 通知", "我要允许generated.invalid网站的通知，在哪里设置？已允许则只说明。",
            "generated.invalid的通知：阻止。", ["generated.invalid 规则菜单", "另一网站 规则菜单"], Satisfied: "generated.invalid的通知：允许。"),
        new("E-02", "msedge", "设置", "Edge 设置 > 地址栏和搜索", "我要把默认搜索引擎设为必应，若已是必应只解释。",
            "地址栏中使用的搜索引擎：生成搜索。", ["地址栏中使用的搜索引擎", "管理搜索引擎"],
            Satisfied: "地址栏中使用的搜索引擎：必应。", Type: "ControlType.ComboBox"),
        new("E-03", "msedge", "设置", "Edge 设置 > 隐私 > 清除浏览数据", "先打开选择清理范围的面板，不删除任何数据。",
            "清除浏览数据：可先选择要清除的内容。", ["选择要清除的内容", "立即清除"]),
        new("E-04", "msedge", "设置", "Edge 设置 > 系统和性能", "我要开启图形加速，若已开启只解释，不操作或重启。",
            "使用图形加速：关闭。更改后可能需要重启浏览器。", ["使用图形加速", "启动增强"],
            Satisfied: "使用图形加速：开启。", Type: "ControlType.CheckBox"),
        new("E-05", "msedge", "扩展", "Edge 扩展管理", "我要启用生成阅读工具扩展，若已启用只解释。",
            "生成阅读工具：已安装但关闭。", ["生成阅读工具", "移除生成阅读工具"],
            Satisfied: "生成阅读工具：已安装并开启。", Type: "ControlType.CheckBox"),
        new("E-06", "msedge", "设置", "Edge 设置 > 下载", "我要开启下载前询问每个文件的保存位置，若已开启只解释。",
            "在下载之前询问每个文件的保存位置：关闭。", ["在下载之前询问每个文件的保存位置", "更改下载位置"],
            Satisfied: "在下载之前询问每个文件的保存位置：开启。", Type: "ControlType.CheckBox")
    ];

    static IEnumerable<TaskCase> Cases()
    {
        foreach (var task in Families)
        {
            var controls = task.Controls.Select((name, i) => new GuideCandidate(i + 1, name, "", i == 0 ? task.Type : "ControlType.Button",
                new Rect(40, 260 + i * 70, 740, 46), [i + 1], task.Type == "ControlType.CheckBox" && i == 0 ? "Off" : null)).ToArray();
            yield return new(task, "normal", task.State, task.Goal, controls, "point", 1, task.Fact);
            yield return new(task, "unclear", task.State, null, controls, "ask", null, null);
            yield return new(task, "unavailable", "相关入口不可用，当前没有可操作的目标控件；不能确认或改变目标状态。",
                task.Goal, [], "explain", null, null);
            if (task.Satisfied is { } satisfied)
            {
                var current = task.Id == "W-01"
                    ? new[] { controls[0] with { Name = "继续更新" } }
                    : controls.Select((c, i) => i == 0 && c.ToggleState is not null ? c with { ToggleState = "On" } : c).ToArray();
                yield return new(task, "satisfied", satisfied, task.Goal, current, "explain", null,
                    task.Id == "W-01" ? "2026" : null);
            }
        }
    }

    public static void Run(Action<bool, string> check)
    {
        var installed = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        var cases = Cases().ToArray();
        check(Families.Select(f => f.Id).Distinct().Count() == 18 &&
              Families.Count(f => f.Id.StartsWith("S-", StringComparison.Ordinal)) == 6 &&
              Families.Count(f => f.Id.StartsWith("W-", StringComparison.Ordinal)) == 6 &&
              Families.Count(f => f.Id.StartsWith("E-", StringComparison.Ordinal)) == 6,
            "generated guidance inventory covers all18 baseline S/W/E tasks, excluding conditional execution");
        check(cases.Length == 62 && cases.Select(c => c.Family.Id + "/" + c.StateId).Distinct().Count() == cases.Length,
            "task oracles freeze62 unique normal, unclear, unavailable and applicable satisfied states");
        foreach (var task in Families)
        {
            var plugin = installed.Resolve(task.Process, task.Title);
            check(plugin.Kind == (task.Id.StartsWith("S-", StringComparison.Ordinal) ? "general" : "application"),
                $"generated {task.Id} uses the explicit generic guide or installed adapter through the common resolver");
        }
        foreach (var item in cases)
        {
            var observation = new GuideObservation(new IntPtr(1), 1, Frame, item.Candidates, null,
                Scope: GuideScope.FromUserSelection(Frame, Frame), ScopeIntent: item.Intent);
            var payload = GuidePayload.From(observation);
            check(payload.Candidates.Count == item.Candidates.Length &&
                  payload.Candidates.All(c => Frame.Contains(c.Bounds)) &&
                  (item.CandidateId is null || payload.Candidates.Any(c => c.Id == item.CandidateId)),
                $"generated {item.Family.Id}/{item.StateId} has a valid deliberate target scope and independent expected decision");
        }
        var known = GuideSession.FailureMessage(new InvalidDataException("elevated_target"));
        check(known.Contains("无法确认") && known.Contains("不会尝试提权") && !known.Contains("尚未发送"),
            "unconfirmed privilege is explained without promising earlier content was never sent");
        check(FailureCode(new InvalidDataException("answer_candidate")) == "answer_candidate" &&
              FailureCode(new InvalidDataException("generated private content")) == "unclassified",
            "task diagnostics retain only allowlisted fixed failure codes, never exception content");
        check(FailureCode(new ArgumentException("answer_candidate")) == "unclassified",
            "task diagnostic failure codes also require the known exception category");
        var capture = GuideSession.FailureMessage(new InvalidDataException("capture_unusable"));
        check(capture.Contains("无法取得可靠") && capture.Contains("不会改为读取整个屏幕"),
            "capture failure gives a target-only recovery instruction without full-screen fallback");
        foreach (var code in new[] { "target_not_foreground", "target_activation_failed", "unsupported_target" })
            check(!GuideSession.FailureMessage(new InvalidDataException(code)).Contains("未读取窗口内容或发送请求"),
                $"{code} cannot claim an earlier session stage never read or sent content");
    }

    public static async Task RunLiveAsync(Action<bool, string> check, string? caseId = null)
    {
        var installed = PluginCatalog.Load(Path.Combine(AppContext.BaseDirectory, "plugins"));
        // Render generated pixels before awaiting; no native window or desktop capture is created.
        var selected = Cases().Where(c => caseId is null || c.Family.Id + "/" + c.StateId == caseId).ToArray();
        if (selected.Length == 0) throw new ArgumentException("unknown_generated_task_case");
        var fixtures = selected.Select(c => (Case: c, Image: Render(c))).ToArray();
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "deepseek-v4.1-flash"));
        var failed = 0;
        foreach (var (item, image) in fixtures)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var observation = new GuideObservation(new IntPtr(1), 1, Frame, item.Candidates, image,
                    Scope: GuideScope.FromUserSelection(Frame, Frame), ScopeIntent: item.Intent);
                var payload = GuidePayload.From(observation);
                var task = item.Intent is null ? GuideObservation.TaskDescription :
                    "用户明确提出的目标：" + item.Intent + "。只解释和指路，不执行任何操作；目标已满足时说明现状，不指向相反动作。";
                var answer = (await model.AskAsync(task, payload.Candidates, payload.Png, CancellationToken.None)).ForIntent(item.Intent);
                var plugin = installed.Resolve(item.Family.Process, item.Family.Title);
                var valid = answer.Mode == item.Mode && answer.CandidateId == item.CandidateId &&
                    (item.Fact is null || answer.Explanation.Contains(item.Fact, StringComparison.OrdinalIgnoreCase)) &&
                    (answer.CandidateId is not { } id || plugin.AllowsPoint(item.Intent, item.Candidates.Single(c => c.Id == id).Name));
                if (valid) check(true, $"LIVE generated {item.Family.Id}/{item.StateId}: mode={answer.Mode}, elapsedMs={clock.ElapsedMilliseconds}; native acceptance NOT implied");
                else
                {
                    failed++;
                    Console.WriteLine(JsonSerializer.Serialize(new { diagnostic = "generated_task_mismatch", task = item.Family.Id,
                        state = item.StateId, mode = answer.Mode, candidateId = answer.CandidateId,
                        elapsedMs = clock.ElapsedMilliseconds }));
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine(JsonSerializer.Serialize(new { diagnostic = "generated_task_failure", task = item.Family.Id,
                    state = item.StateId, category = ex.GetType().Name, reason = FailureCode(ex),
                    elapsedMs = clock.ElapsedMilliseconds }));
            }
        }
        check(failed == 0, $"all{fixtures.Length} generated guidance decisions agree with their frozen oracles; mismatches={failed}; no automatic retries");
    }

    static string FailureCode(Exception error) => error is InvalidDataException && error.Message is
        ("answer_shape" or "answer_fields" or "answer_value" or "answer_candidate" or
         "model_response_too_large" or "model_incomplete" or "model_refusal" or "sensitive_content" or "privacy_unavailable")
        ? error.Message : "unclassified";

    static byte[] Render(TaskCase item)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            void Text(string value, Point point, double size = 20)
            {
                var text = new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei"), size, Brushes.Black, 1) { MaxTextWidth = 740 };
                draw.DrawText(text, point);
            }
            draw.DrawRectangle(Brushes.White, null, Frame);
            Text(item.Family.Page, new Point(40, 30), 26);
            Text(item.State, new Point(40, 110));
            foreach (var candidate in item.Candidates)
            {
                draw.DrawRectangle(Brushes.AliceBlue, new Pen(Brushes.SteelBlue, 1), candidate.Bounds);
                Text(candidate.Name + (candidate.ToggleState is { } state ? state == "On" ? "  [开启]" : "  [关闭]" : ""),
                    new Point(candidate.Bounds.X + 12, candidate.Bounds.Y + 10), 18);
            }
        }
        var bitmap = new RenderTargetBitmap((int)Frame.Width, (int)Frame.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Capture.EncodeRegion(bitmap, Frame, Frame);
    }
}
