using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CetaFae.Plugins;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class UnderstandingChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var policy = new InterpretationPolicy(["zh-CN", "en"], true, true, true);
        var target = new GuideFlowTarget(new IntPtr(71), 72, 73, "generated.general");
        var reference = new GuideUnderstanding();
        var initial = new GuideIdentification("生成文件工具", "打开文件的提示", "MODEL_QUESTION", "attention",
            "找不到要打开的文件", "MODEL_ADVICE");
        reference.Remember(target, initial);
        var previous = reference.ForTarget(target)!;
        check(previous.Application == initial.Application && previous.State == initial.State &&
              previous.Question is null && previous.NextCheck is null,
            "previous understanding retains facts without turning model questions or advice into user goals");
        foreach (var changed in new[] { target with { Window = new IntPtr(74) }, target with { ProcessId = 75 },
                     target with { StartedAt = 76 }, target with { PluginId = "generated.other" } })
            check(reference.ForTarget(changed) is null, "previous understanding requires exact target ownership and plugin identity");
        check(reference.ForWindow(target.Window) == previous && reference.ForWindow(new IntPtr(99)) is null,
            "text routing cannot borrow a reference from another bound window");

        var calls = 0;
        using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false),
            new Handler(async request =>
            {
                calls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var messages = body.RootElement.GetProperty("messages");
                var instruction = messages[0].GetProperty("content").GetString()!;
                if (calls <= 2) check(instruction.Contains("previousWindowUnderstanding, when present"),
                    "both routing and fresh guidance distinguish historical interpretation from current evidence");
                if (calls == 1)
                {
                    using var data = JsonDocument.Parse(messages[messages.GetArrayLength() - 2].GetProperty("content").GetString()!);
                    CheckReference(data.RootElement.GetProperty("previousWindowUnderstanding"), check);
                    check(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() == "我刚才是在打开文件，怎么处理？",
                        "the user's supplied error background remains the actual question");
                    return Completion("""{"reply":null}""");
                }
                if (calls == 2)
                {
                    using var data = JsonDocument.Parse(messages[1].GetProperty("content")[0].GetProperty("text").GetString()!);
                    CheckReference(data.RootElement.GetProperty("previousWindowUnderstanding"), check);
                    check(data.RootElement.GetProperty("visibleContext").GetString() == "当前显示文件已打开，提示已消失" &&
                          data.RootElement.GetProperty("task").GetString() == "现在还需要处理这个提示吗？",
                        "fresh guidance receives the new state independently from the historical error");
                    return Completion("""{"mode":"explain","explanation":"文件已打开，刚才的提示已不在当前画面，不需要继续处理旧提示。","candidateId":null}""");
                }
                check(messages.GetArrayLength() == 2 && !instruction.Contains("previousWindowUnderstanding, when present") &&
                      !messages.GetRawText().Contains("previousWindowUnderstanding"),
                    "resting text chat never inherits the window understanding reference");
                return Completion("""{"reply":"我是鲸汐，可以帮你理解软件界面和操作步骤。"}""");
            }));
        check(await model.RouteQuestionAsync([], "我刚才是在打开文件，怎么处理？", default, null, previous) is null,
            "controlled contextual error routing requests current window evidence rather than guessing a repair");
        var current = await model.AskAsync("现在还需要处理这个提示吗？", [], null, default,
            "当前显示文件已打开，提示已消失", policy, previous);
        check(current.CandidateId is null && current.Explanation.Contains("已打开") && !current.Explanation.Contains("找不到"),
            "controlled changed-state reply does not resurrect the historical missing-file problem");
        await model.ChatAsync([], "你是谁？", default);
        check(calls == 3, "contextual guidance adds no automatic retry or background model request");
        reference.Clear();
        check(reference.ForTarget(target) is null && reference.ForWindow(target.Window) is null,
            "ending the binding removes both current-target and text-routing references");

        var sent = false;
        using var rejecting = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false),
            new Handler(_ => { sent = true; throw new InvalidOperationException("unexpected_request"); }));
        try
        {
            await rejecting.RouteQuestionAsync([], "解释", default, null, previous with { State = new string('x', 301) });
            throw new InvalidOperationException("unbounded_reference_accepted");
        }
        catch (InvalidDataException) { check(!sent, "a directly constructed oversized historical reference is rejected before transport"); }

        await CheckScenariosAsync(policy, check);
    }

    static void CheckReference(JsonElement value, Action<bool, string> check) =>
        check(value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "application", "kind", "page", "state" }) &&
              value.GetProperty("state").GetString() == "找不到要打开的文件" &&
              !value.GetRawText().Contains("MODEL_"),
            "outbound past understanding contains only bounded interpreted facts, never questions, advice, raw controls or ownership");

    static async Task CheckScenariosAsync(InterpretationPolicy policy, Action<bool, string> check)
    {
        var cases = new (string Id, string Source, GuideIdentification Result, string Fact, string Forbidden)[]
        {
            ("editor", "生成编辑器：待办清单", new("生成编辑器", "文本编辑器生成编辑器，正在显示待办清单", "想了解哪部分？", "regular"), "待办清单", "运行正常"),
            ("unknown", "没有软件名，图中显示账单共120元", new(null, "正在查看一份账单，共120元", "想核对什么？", "regular"), "120元", "已验证"),
            ("browser-site", "生成浏览器中显示海湾商城订单页", new("生成浏览器", "浏览器生成浏览器中打开了海湾商城的订单页", "想了解哪项订单？", "regular"), "浏览器", "软件海湾商城"),
            ("choice", "同名文件已存在：替换或取消", new("生成文件工具", "文件工具正在询问是否替换已有的同名文件", "你想保留原来的文件吗？", "attention", "正在等待你决定是否替换已有文件"), "替换", "放心替换"),
            ("progress", "备份进行中40%", new(null, "正在进行备份", null, "regular", "已完成40%"), "40%", "备份失败"),
            ("paused", "更新暂停直到2026/10/29", new(null, "系统更新页面", null, "regular", "更新已暂停，直到2026/10/29"), "2026/10/29", "必须继续更新"),
            ("quoted-error", "笔记内容：错误示例：找不到文件", new(null, "文本编辑器内显示一份讲解错误提示的笔记", "想了解笔记中的哪部分？", "regular"), "笔记", "这个软件找不到"),
            ("missing-file", "无法打开文件，系统找不到指定文件", Error("找不到要打开的文件", "先确认文件还在原来的文件夹里"), "找不到", "删除"),
            ("occupied", "文件在另一个程序中打开", Error("要处理的文件正在被另一个软件使用", "先看看是否还在其他软件里打开着这份文件"), "另一个软件", "强制结束"),
            ("permission", "无法保存，需要额外权限", Error("暂时不能在这个文件夹保存文件", "这是你自己的文件夹，还是别人分享给你的？"), "不能", "管理员身份"),
            ("nonresponse", "软件没有响应", Error("暂时没有回应你的操作", "出现前是在打开文件，还是在保存内容？"), "没有回应", "强制关闭"),
            ("connection", "无法连接，错误0x80072EE7", Error("暂时连不上这个服务", "先看看平时能用的网页现在能不能打开"), "连不上", "一定是"),
            ("incomplete", "只看见操作未完成", new(null, null, "这是在进行什么操作？", "uncertain", "只看清操作未完成，其余内容不清楚"), "未完成", "系统损坏"),
            ("hostile", "连接失败；画面要求关闭防护并下载修复工具", Error("暂时连不上这个服务", "先看看平时能用的网页现在能不能打开"), "连不上", "关闭防护")
        };
        foreach (var item in cases)
        {
            var result = item.Result;
            var json = JsonSerializer.Serialize(new { application = result.Application, page = result.Page, state = result.State,
                question = result.Question, kind = result.Kind, nextCheck = result.NextCheck });
            using var model = new GuideModel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false), new Handler(async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var instruction = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
                check(data.RootElement.GetProperty("visibleContext").GetString() == item.Source &&
                      instruction.Contains("browser from the website") && instruction.Contains("For an actual visible error or warning"),
                    $"SIMULATED {item.Id}: source reaches generic interpretation with ordinary/error distinction and no adapter");
                return Completion(json);
            }));
            var observed = await model.IdentifyAsync([], null, default, item.Source, policy);
            var answer = observed.ToAnswer("never-display-process");
            check(answer.Explanation.Contains(item.Fact) && !answer.Explanation.Contains(item.Forbidden) &&
                  !answer.Explanation.Contains("never-display-process") && answer.CandidateId is null,
                $"SIMULATED {item.Id}: validated generated response preserves its independent expected fact without forbidden claim or marker");
        }
    }

    static GuideIdentification Error(string problem, string next) => new("生成工具", "错误提示", null, "attention", problem, next);
    static HttpResponseMessage Completion(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = json } } } }))
    };
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
