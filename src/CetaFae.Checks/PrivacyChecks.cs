using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using CetaFae.Probe;

namespace CetaFae.Checks;

static class PrivacyChecks
{
    public static void CheckDisclosureLayout(Action<bool, string> check)
    {
        var panel = new GuidePanel(GuideOptions.Create("http://127.0.0.1:8787/v1", "generated"));
        try
        {
            check(!panel.IsVisible && !panel.ShowInTaskbar, "idle card is hidden and absent from taskbar before any native launch");
            var card = panel.Card;
            card.ShowConsent();
            card.Measure(new Size(panel.Width, panel.MaxHeight));
            card.Arrange(new Rect(new Size(panel.Width, panel.MaxHeight)));
            card.UpdateLayout();
            check(DockPanel.GetDock(card.Actions) == Dock.Bottom, "cancel and consent actions are fixed outside scrollable content");
            bool ActionsVisible() => card.Actions.Children.Cast<Button>().Where(button => button.Visibility == Visibility.Visible).All(button =>
            {
                var bounds = button.TransformToAncestor(card).TransformBounds(new Rect(button.RenderSize));
                return bounds.Top >= 0 && bounds.Bottom <= card.ActualHeight + 1 &&
                    bounds.Left >= 0 && bounds.Right <= card.ActualWidth + 1;
            });
            check(ActionsVisible(), "personal consent and return remain visible without scrolling through disclosure");
            card.BodyScroll.ScrollToBottom();
            card.UpdateLayout();
            check(ActionsVisible(), "personal consent and return remain fixed after disclosure scrolling");
            card.SetStatus(new string('测', 600));
            card.ShowQuestion();
            card.Measure(new Size(panel.Width, panel.MaxHeight));
            card.Arrange(new Rect(new Size(panel.Width, panel.MaxHeight)));
            card.UpdateLayout();
            check(card.BodyScroll.ExtentHeight > card.BodyScroll.ViewportHeight && card.BodyScroll.ViewportHeight > 0,
                "long question and result overflow is scrollable rather than clipped");
            check(new WindowInteropHelper(panel).Handle == IntPtr.Zero,
                "disclosure layout check creates no native window");
        }
        finally { panel.Close(); }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        void Reject(Action action, string name, string expected = "sensitive_content")
        {
            var rejected = false;
            try { action(); }
            catch (InvalidDataException ex) { rejected = ex.Message == expected; }
            check(rejected, name);
        }
        var hidden = new GeneratedNode("Hidden branch", true);
        var visible = new GeneratedNode("Visible sibling");
        var nested = new GeneratedNode("Visible descendant");
        var visibleParent = new GeneratedNode("Visible parent") { Children = [nested] };
        var root = new GeneratedNode("Root") { Children = [hidden, visible, visibleParent] };
        var parents = new Dictionary<GeneratedNode, GeneratedNode> { [hidden] = root, [visible] = root,
            [visibleParent] = root, [nested] = visibleParent };
        GeneratedNode? FirstChild(GeneratedNode node)
        {
            if (node == hidden) throw new InvalidOperationException("hidden_subtree_accessed");
            return node.Children.FirstOrDefault();
        }
        GeneratedNode? NextSibling(GeneratedNode node)
        {
            var siblings = parents[node].Children;
            var index = Array.IndexOf(siblings, node) + 1;
            return index < siblings.Length ? siblings[index] : null;
        }
        var walked = GuideObservation.WalkVisible(root, FirstChild, NextSibling,
            node => node.Offscreen, _ => false, CancellationToken.None).ToArray();
        check(walked.SequenceEqual(new[] { visible, visibleParent, nested }),
            "hidden subtrees are never entered while visible siblings and descendants remain readable");
        var hiddenRoot = new GeneratedNode("Hidden root", true);
        check(!GuideObservation.WalkVisible(hiddenRoot, _ => throw new InvalidOperationException("hidden_root_accessed"),
                _ => null, n => n.Offscreen, _ => false, CancellationToken.None).Any(),
            "a target that becomes hidden is not entered");
        check(GuideObservation.WalkVisible(root, FirstChild, NextSibling,
                n => n.Offscreen ? true : AutomationElement.NotSupported, _ => false, CancellationToken.None)
                .SequenceEqual(new[] { visible, visibleParent, nested }),
            "unsupported structural visibility does not silently hide visible descendants");
        check(GuideObservation.WalkVisible(root, FirstChild, NextSibling,
                n => n.Offscreen, n => n == visibleParent, CancellationToken.None).SequenceEqual(new[] { visible }),
            "excluded chrome subtrees do not remove their visible siblings");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var stopped = false;
            try { GuideObservation.WalkVisible(root, _ => throw new InvalidOperationException("provider_called"),
                _ => null, _ => false, _ => false, cancelled.Token).ToArray(); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped, "cancelled traversal stops before invoking a provider");
        }
        var chain = new GeneratedNode("Depth root");
        var leaf = chain;
        for (var i = 0; i < 50; i++) { var child = new GeneratedNode("Depth node"); leaf.Children = [child]; leaf = child; }
        Reject(() => GuideObservation.WalkVisible(chain, n => n.Children.FirstOrDefault(), _ => null,
            _ => false, _ => false, CancellationToken.None).ToArray(),
            "visible depth remains bounded", "uia_traversal_limit");
        Reject(() => GuideObservation.WalkVisible(root, n => n == root ? visible : null, _ => visible,
            _ => false, _ => false, CancellationToken.None).Count(),
            "cyclic or oversized visible trees remain bounded", "uia_traversal_limit");
        var reads = 0;
        string ReadName() { reads++; return "Generated visible control"; }
        Reject(() => GuidePrivacy.ReadVisibleName(false, true, ReadName), "visible password metadata stops name access");
        check(reads == 0, "a password element's name getter is never invoked");
        Reject(() => GuidePrivacy.ReadVisibleName(false, null, ReadName),
            "unknown password metadata fails closed", "privacy_unavailable");
        check(reads == 0 && GuidePrivacy.ReadVisibleName(true, true, ReadName) is null && reads == 0,
            "offscreen password content is neither read nor exposed");
        check(GuidePrivacy.ReadVisibleName(false, false, ReadName) == "Generated visible control" && reads == 1,
            "ordinary visible metadata remains usable");
        foreach (var role in new[] { "ControlType.TitleBar", "ControlType.MenuBar", "ControlType.MenuItem", "ControlType.Button" })
            check(GuidePrivacy.ReadVisibleName(false, AutomationElement.NotSupported, () => "Generated caption", role, true) == "Generated caption",
                "native noneditable caption metadata uses the documented password default");
        Reject(() => GuidePrivacy.ReadVisibleName(false, null, ReadName, "ControlType.Edit", true),
            "a caption ancestor cannot excuse missing password metadata on editable content", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName(false, null, ReadName, "ControlType.Button", false),
            "the caption default cannot bypass missing metadata in page content", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName(false, true, ReadName, "ControlType.Button", true),
            "an explicit password flag still refuses a caption control");

        var unsupported = AutomationElement.NotSupported;
        check(GuidePrivacy.ReadVisibleName(unsupported, false, () => "Generated document", "ControlType.Document") == "Generated document",
            "an explicitly non-password document accepts only the exact unsupported visibility default");
        var documentReads = 0;
        string ReadDocument() { documentReads++; return "Generated document"; }
        Reject(() => GuidePrivacy.ReadVisibleName(unsupported, true, ReadDocument, "ControlType.Document"),
            "a document password flag refuses before its name getter");
        foreach (var flag in new object?[] { unsupported, null, "invalid" })
            Reject(() => GuidePrivacy.ReadVisibleName(unsupported, flag, ReadDocument, "ControlType.Document"),
                "document visibility defaults require an explicit false password flag", "privacy_unavailable");
        foreach (var flag in new object?[] { null, "invalid" })
            Reject(() => GuidePrivacy.ReadVisibleName(flag, false, ReadDocument, "ControlType.Document"),
                "null or malformed document visibility cannot become a default", "privacy_unavailable");
        check(GuidePrivacy.ReadVisibleName(true, false, ReadDocument, "ControlType.Document") is null && documentReads == 0,
            "document defaults never expose hidden or rejected document names");
        Reject(() => GuidePrivacy.ReadVisibleName(unsupported, false, () => "API key", "ControlType.Document"),
            "the document visibility default retains credential-label refusal");
        Reject(() => GuidePrivacy.ReadVisibleName(unsupported, false, () => "password=generated-value", "ControlType.Document"),
            "the document visibility default retains credential-content refusal");
        foreach (var role in new[] { "ControlType.Button", "ControlType.Tab", "ControlType.TabItem", "ControlType.Text",
                     "ControlType.ComboBox", "ControlType.Tree", "ControlType.TreeItem", "ControlType.Group",
                     "ControlType.RadioButton", "ControlType.CheckBox" })
        {
            check(GuidePrivacy.ReadVisibleName(false, unsupported, () => "Generated standard control", role) ==
                    "Generated standard control", "an explicitly visible standard control uses the exact unsupported password default");
            Reject(() => GuidePrivacy.ReadVisibleName(unsupported, unsupported, ReadName, role),
                "standard content defaults never infer visibility", "privacy_unavailable");
            Reject(() => GuidePrivacy.ReadVisibleName(false, null, ReadName, role),
                "standard content defaults never replace null password metadata", "privacy_unavailable");
            Reject(() => GuidePrivacy.ReadVisibleName(false, "invalid", ReadName, role),
                "standard content defaults never replace malformed password metadata", "privacy_unavailable");
            var protectedReads = 0;
            Reject(() => GuidePrivacy.ReadVisibleName(false, true, () => { protectedReads++; return "Not read"; }, role),
                "an explicit password flag overrides standard control defaults");
            check(protectedReads == 0, "protected standard controls never expose their names");
        }
        foreach (var role in new[] { "ControlType.Edit", "ControlType.Custom", "ControlType.Document", "Unknown" })
            Reject(() => GuidePrivacy.ReadVisibleName(false, unsupported, ReadName, role),
                "editable and unknown content cannot use standard control password defaults", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName(false, unsupported, () => "Password", "ControlType.Text"),
            "standard control defaults preserve credential label refusal");
        var standardRoot = new GeneratedNode("Generated parent")
        {
            Children = [new GeneratedNode("Not read")]
        };
        var standardChildReads = 0;
        Reject(() =>
        {
            foreach (var node in GuideObservation.WalkVisible(standardRoot, n => n.Children.FirstOrDefault(), _ => null,
                         _ => false, _ => false, CancellationToken.None))
                GuidePrivacy.ReadVisibleName(false, node == standardRoot ? unsupported : true,
                    () => { if (node != standardRoot) standardChildReads++; return node.Name; },
                    node == standardRoot ? "ControlType.ComboBox" : "ControlType.Edit");
        }, "a standard parent does not bypass a protected editable descendant");
        check(standardChildReads == 0, "protected editable descendants are refused before reading names");
        check(GuidePrivacy.ReadVisibleName(false, unsupported, () => "Generated window wrapper", "ControlType.Window") ==
                "Generated window wrapper",
            "a visible standard window accepts the documented unsupported password default");
        var wrapperReads = 0;
        string ReadWrapper() { wrapperReads++; return "Generated structural wrapper"; }
        foreach (var role in new[] { "ControlType.Window", "ControlType.Pane" })
        {
            check(GuidePrivacy.ReadVisibleName(unsupported, unsupported, ReadWrapper, role) == "Generated structural wrapper",
                "a standard structural wrapper accepts the documented false defaults for two unsupported properties");
            check(GuidePrivacy.ReadVisibleName(false, unsupported, ReadWrapper, role) == "Generated structural wrapper",
                "structural password defaults do not depend on visibility also being unsupported");
            check(GuidePrivacy.ReadVisibleName(unsupported, false, ReadWrapper, role) == "Generated structural wrapper",
                "structural visibility defaults do not depend on password metadata also being unsupported");
        }
        check(wrapperReads == 6, "structural defaults still inspect wrapper names instead of skipping them");
        foreach (var role in new[] { "ControlType.TitleBar", "ControlType.MenuBar", "ControlType.MenuItem", "ControlType.Button" })
        {
            check(GuidePrivacy.ReadVisibleName(unsupported, unsupported, () => "Generated native caption", role, true) ==
                    "Generated native caption", "verified noneditable caption roles support two exact default sentinels");
            Reject(() => GuidePrivacy.ReadVisibleName(unsupported, unsupported, ReadName, role, false),
                "two unsupported flags cannot excuse unverified page chrome", "privacy_unavailable");
            Reject(() => GuidePrivacy.ReadVisibleName(unsupported, unsupported, () => "API key", role, true),
                "native caption defaults preserve credential label refusal");
        }
        foreach (var role in new[] { "ControlType.Edit", "ControlType.Custom", "ControlType.Document", "ControlType.Text" })
            Reject(() => GuidePrivacy.ReadVisibleName(unsupported, unsupported, ReadName, role),
                "unsupported content properties cannot inherit the structural default", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName(null, null, ReadName, "ControlType.Pane"),
            "a null structural property is not the UIA NotSupported sentinel", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName(false, null, ReadName, "ControlType.Pane"),
            "null password metadata is not a structural default", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName(unsupported, "invalid", ReadName, "ControlType.Window"),
            "one documented default cannot excuse a malformed structural property", "privacy_unavailable");
        foreach (var role in new[] { "ControlType.Edit", "ControlType.Custom", "ControlType.Document", "ControlType.Text" })
            Reject(() => GuidePrivacy.ReadVisibleName(unsupported, unsupported, ReadName, role, true),
                "a native caption ancestor cannot default editable or page-content flags", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName(unsupported, unsupported, () => "API key", "ControlType.Pane"),
            "structural defaults do not bypass credential labels");
        Reject(() => GuidePrivacy.ReadVisibleName(false, true, ReadName, "ControlType.Pane"),
            "an explicitly protected structural node still refuses name access");
        Reject(() => GuidePrivacy.ReadVisibleName(false, null, ReadName, "ControlType.Button", true),
            "caption defaults cannot turn null or malformed metadata into false", "privacy_unavailable");
        Reject(() => GuidePrivacy.ReadVisibleName("invalid", false, ReadName, "ControlType.Pane"),
            "malformed visibility does not become a structural default", "privacy_unavailable");

        var replay = new (string Role, object Offscreen, object Password)[]
        {
            ("ControlType.Window", unsupported, unsupported),
            ("ControlType.Pane", unsupported, unsupported),
            ("ControlType.Pane", unsupported, unsupported),
            ("ControlType.Edit", false, false),
            ("ControlType.Button", false, false)
        };
        var replayReads = 0;
        foreach (var node in replay)
            GuidePrivacy.ReadVisibleName(node.Offscreen, node.Password, () => { replayReads++; return "Generated visible node"; }, node.Role);
        check(replayReads == replay.Length, "the generated native wrapper shape continues through editable and action descendants");
        var passwordReads = 0;
        Reject(() => GuidePrivacy.ReadVisibleName(false, true, () => { passwordReads++; return "Not read"; }, "ControlType.Edit"),
            "a password descendant after structural wrappers still refuses the observation");
        check(passwordReads == 0, "structural normalization never reads a password descendant name");

        var generatedToken = "ghp_" + new string('x', 36);
        foreach (var text in new[] { "-----BEGIN PRIVATE KEY-----", "-----BEGIN RSA PRIVATE KEY-----", generatedToken,
                     "github_pat_" + new string('y', 30), "sk-proj-" + new string('z', 40),
                     "AKIA" + new string('A', 16), "api_key=generated-value", "密码：generated-value",
                     "Authorization: Bearer generated-value", "https://example.invalid/?token=generated-value" })
            Reject(() => GuidePrivacy.EnsureTextSafe(text), "generated credential form is refused without echoing it");
        Reject(() => GuidePrivacy.ReadVisibleName(false, false, () => generatedToken),
            "a credential in visible non-password text also refuses the observation");
        foreach (var label in new[] { "API key", "密码：", "Client secret", "访问令牌" })
            Reject(() => GuidePrivacy.ReadVisibleName(false, false, () => label),
                "an explicit credential field label refuses pixels without reading its value");
        Reject(() => GuidePrivacy.EnsureTextSafe(new string('a', 32769)),
            "oversized privacy text fails closed", "privacy_unavailable");
        foreach (var text in new[] { "下载前询问的开关在哪里？只指出位置，不修改。", "密码与安全", "API key settings",
                     "更新已暂停，直到2026/10/29为止", "D:\\Downloads", "https://example.invalid/?page=downloads" })
        {
            GuidePrivacy.EnsureTextSafe(text);
            check(true, "ordinary settings metadata and Chinese intent pass the privacy gate");
        }

        var candidate = new GuideCandidate(1, "Generated control", "generated-id", "ControlType.Button", new Rect(0, 0, 10, 10), [1]);
        var options = GuideOptions.Create("http://127.0.0.1:8787/v1", "generated", false);
        var disclosure = GuidePanel.Disclosure(options);
        check(disclosure.Contains("回答") && disclosure.Contains("复核") && disclosure.Contains("用户输入"),
            "consent discloses answer-triggered reads, local revalidation and submitted user intent");
        check(disclosure.Contains(options.BaseUrl.AbsoluteUri) && disclosure.Contains(options.Model),
            "consent identifies the configured endpoint path and model");
        check(disclosure.Contains("不含图片") && !disclosure.Contains("发送所选整窗画面"),
            "text-only consent does not claim an image is sent");
        check(GuidePanel.Disclosure(options with { IncludeImage = true }).Contains("目标未明") &&
              GuidePanel.Disclosure(options with { IncludeImage = true }).Contains("局部画面") &&
              GuidePanel.Disclosure(options with { IncludeImage = true }).Contains("本机内存绘制所选整窗"),
            "image consent distinguishes scoped outbound content from initial and local whole-window rendering");
        check(disclosure.Contains("7天") && disclosure.Contains("不保存") && disclosure.Contains("会话结束") &&
              disclosure.Contains("代理") && disclosure.Contains("模型服务商") && disclosure.Contains("正式发布") &&
              disclosure.Contains("无法从上游撤回"),
            "consent separates client retention, unknown upstream policy and revocation limits");
        foreach (var (task, current) in new[]
                 {
                     ("Generated fixture: -----BEGIN PRIVATE KEY-----", candidate),
                     ("Generated task", candidate with { Name = generatedToken }),
                     ("Generated task", candidate with { AutomationId = "api_key=generated-value" })
                 })
        {
            var sent = 0;
            using var model = new GuideModel(options, new Handler(() => sent++));
            var rejected = false;
            try { await model.AskAsync(task, [current], null, CancellationToken.None); }
            catch (InvalidDataException ex) { rejected = ex.Message == "sensitive_content"; }
            check(rejected && sent == 0, "sensitive task or candidate metadata is refused before HTTP dispatch");
        }
        using (var model = new GuideModel(options, new Handler(() => { }, generatedToken)))
        {
            var rejected = false;
            try { await model.AskAsync("Generated ordinary task", [candidate], null, CancellationToken.None); }
            catch (InvalidDataException ex) { rejected = ex.Message == "sensitive_content"; }
            check(rejected, "a generated credential in a model response is withheld");
        }
        check(ObservationWorker.SafeError("sensitive_content") == "sensitive_content" &&
              ObservationWorker.SafeError("privacy_unavailable") == "privacy_unavailable" &&
              ObservationWorker.SafeError(generatedToken) == "worker_failed",
            "worker IPC preserves fixed privacy codes and discards arbitrary error content");
        check(GuideSession.FailureMessage(new InvalidDataException("sensitive_content")).Contains("密码字段或疑似密钥") &&
              GuideSession.FailureMessage(new InvalidDataException("privacy_unavailable")).Contains("无法完成敏感内容检查"),
            "privacy refusals have actionable fixed messages rather than a model failure");
    }

    sealed class GeneratedNode(string name, bool offscreen = false)
    {
        public string Name { get; } = name;
        public bool Offscreen { get; } = offscreen;
        public GeneratedNode[] Children { get; set; } = [];
    }

    sealed class Handler(Action sent, string explanation = "Generated answer") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            sent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new
                        { content = JsonSerializer.Serialize(new { mode = "explain", explanation, candidateId = (int?)null }) } } }
                }))
            });
        }
    }
}
