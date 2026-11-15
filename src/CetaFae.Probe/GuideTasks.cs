using System.IO;
using System.Text.Json;
using System.Windows;
using CetaFae.Plugins;
using Microsoft.Win32;

namespace CetaFae.Probe;

public sealed partial class GuideSession
{
    readonly TaskRecordStore _taskRecords = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CetaFae", "tasks"));
    CancellationTokenSource? _taskStop;
    bool _taskActive;

    void TaskStatus(bool attached, string message, string? primary = null, Action? accept = null)
    {
        if (attached)
        {
            if (_ball.HasTarget && _ball.BindingSuspended) _panel.Card.SetStatus("正在处理你的问题");
            else _panel.SetStatus("正在处理你的问题");
            _panel.Card.TaskActions.Present(message, primary, accept, () => _taskStop?.Cancel());
            if (!_ball.BindingSuspended) { if (!_panel.IsVisible) _panel.Show(); _panel.Reposition(); }
        }
        else if (_chatWindow is { } chat)
        {
            chat.TaskActions.Present(message, primary, accept, () => _taskStop?.Cancel());
            chat.Place(_ball.FeedbackBounds, _ball.Workspace);
        }
    }

    TaskInteraction TaskInteractionFor(bool attached)
    {
        async Task<bool> Decide(string message, string? action, CancellationToken token)
        {
            var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = token.Register(() => choice.TrySetCanceled(token));
            TaskStatus(attached, message + (action is "开始检查" ? "\n接收方：" + _options.BaseUrl.AbsoluteUri : ""), action, () => choice.TrySetResult(true));
            return await choice.Task;
        }
        Task<string?> Pick(string kind, CancellationToken token)
        {
            var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskStatus(attached, kind == "folder" ? "需要你指定要检查的文件夹，只统计这个范围。" :
                kind == "destination" ? "请选择目标文件的位置和名称。" :
                kind == "conversion-file" ? "请选择要转换的原文件。" : "需要你指定这次处理的文件，不会自动读取其他文档。",
                kind == "conversion-file" ? "选文件" : "选择位置", () =>
            {
                if (token.IsCancellationRequested) { choice.TrySetCanceled(token); return; }
                string? path = null;
                var chat = _chatWindow;
                if (chat is not null) chat.ChoosingTaskResource = true;
                try
                {
                if (kind == "folder")
                {
                    var picker = new OpenFolderDialog { Title = "选择这次检查的文件夹", Multiselect = false };
                    if (picker.ShowDialog() == true) path = picker.FolderName;
                }
                else if (kind == "destination")
                {
                    var picker = new SaveFileDialog { Title = "选择目标文件", OverwritePrompt = false };
                    if (picker.ShowDialog() == true) path = picker.FileName;
                }
                else
                {
                    var picker = new OpenFileDialog { Title = kind == "conversion-file" ? "选择要转换的文件" : "选择这次处理的文件",
                        Multiselect = false, CheckFileExists = true };
                    if (picker.ShowDialog() == true) path = picker.FileName;
                }
                }
                finally { if (chat is not null) chat.ChoosingTaskResource = false; }
                choice.TrySetResult(path);
            });
            return WaitForPick();
            async Task<string?> WaitForPick()
            {
                using var cancel = token.Register(() => choice.TrySetCanceled(token));
                return await choice.Task;
            }
        }
        return new(Decide, Pick, message => TaskStatus(attached, message));
    }

    async Task<string?> TaskReplyAsync(IReadOnlyList<ChatTurn> history, string question, bool attached, CancellationToken token)
    {
        if (_taskActive) return "上一个处理还未结束，请先停止或完成它。";
        if (await UtilityIntentAsync(question, token) is { } utility)
        {
            _ = _ball.Dispatcher.BeginInvoke(() => RunUtility(utility));
            return utility == "memory.inspect" ? "正在本机检查内存占用。" : "已打开工具；需要截图时，请把鲸鱼拖到要处理的窗口。";
        }
        var plugin = _plugins.ChatProvider ?? throw new InvalidDataException("plugin_program_unavailable");
        var response = await _model.ConverseAsync(history, question, attached, token, attached ? _followup.ActiveGoal : null,
            attached ? _understanding.ForWindow(_ball.BoundTarget) : null, taskAvailable: true);
        if (response.Task is null) return response.Reply;
        if (_taskActive) return "上一个处理还未结束，请先停止或完成它。";
        _taskActive = true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromMinutes(10));
        _taskStop = stop;
        var task = new TaskCoordinator(_taskRecords, TaskInteractionFor(attached), _plugins,
            action => { _ = _ball.Dispatcher.BeginInvoke(() => RunUtility(action)); });
        try
        {
            return await TaskWorkflow.RunAsync(task, plugin, response,
                (facts, nextToken) => _model.ConverseAsync(history, question, attached, nextToken,
                    taskAvailable: true, taskEvidence: facts), stop.Token);
        }
        catch (OperationCanceledException)
        {
            return task.LatestReceipt switch
            {
                null => "已停止，未改动你选择的文件。",
                { State: "verified" } => "已停止后续处理。已完成的改动仍保留，可从操作记录恢复。",
                _ => "已停止后续处理。请先查看操作记录，核对已发出的操作。"
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or PlatformFailure or System.Security.Cryptography.CryptographicException or System.Net.Http.HttpRequestException)
        {
            var conversionMessage = ex.Message switch
            {
                "conversion_file_limit" => "这张图超过 32 MB，请选择较小的图片。原文件仍保留。",
                "conversion_output_limit" => "转出的图片超过这版可保存的 4 MB，请选择较小的原图。原文件仍保留。",
                "conversion_invalid_source" => "这张图与文件名显示的格式不符，或文件已损坏。请先确认原图能打开。",
                "conversion_timeout" => "转换这次没有及时完成。原文件仍保留。",
                "conversion_failed" or "conversion_invalid_output" => "这张图没能成功转换。请确认原图能打开，再试另一种支持的格式。",
                "conversion_plan" or "conversion_conflict" => "当前无法确定可用的转换方案，请检查已启用的转换插件。",
                "compression_target_unavailable" => "在不过度缩小或降低画质的前提下，暂时压不到这个大小。原图保留，可以调整大小要求。",
                _ => null
            };
            if (ex.Message.StartsWith("tool_download_", StringComparison.Ordinal))
                return "转换组件这次没能下载完成，原文件和已有组件保留。可稍后再试，或在设置中导入离线包。";
            if (ex is System.Net.Http.HttpRequestException)
                return "识别服务暂时没有连通，已停止后续处理。已完成的操作可从操作记录查看，不会自动重复执行。";
            if (conversionMessage is not null) return conversionMessage;
            return "这一步未能可靠完成，已停止后续操作。" + (ex.Message switch
            {
                "task_needs_inspection" => "这个对象还有一项结果待核对的操作，请先查看操作记录。",
                "sensitive_content" => "发现可能的密码或密钥，没有发送这份文件内容。请先移除敏感信息。",
                "task_file_too_large" or "file_limit" or "text_limit" or "request_size" => "文件或文字超过本次处理上限，请选择较小的文本文件。",
                "task_storage_limit" or "recovery_storage_limit" => "操作记录或恢复空间已满，已暂停新的改动，请先查看操作记录。",
                "task_limit" => "本次处理已达到步数或时间上限，已完成的改动可从操作记录查看。",
                _ => "请查看对象是否仍存在、是否被其他软件占用；操作记录和可用恢复材料会保留。"
            });
        }
        finally
        {
            _taskActive = false; _taskStop = null;
            if (attached) _panel.Card.TaskActions.Clear(); else _chatWindow?.TaskActions.Clear();
        }
    }

    void ShowTaskRecords()
    {
        if (_taskActive) return;
        try
        {
            var entries = _taskRecords.Read().OrderByDescending(r => r.At).ToArray();
            if (entries.Length == 0) { _panel.SetStatus("还没有文件操作记录。"); return; }
            void Show(int index)
            {
                var entry = entries[index];
                var state = entry.State switch { "verified" => "已核验完成", "restored" => "已恢复", "denied" => "未执行", "failed" => "未完成", _ => "结果待核对" };
                var operation = entry.Operation switch { "file.write" => "保存文字", "file.copy" => "复制文件", "file.move" => "移动文件", "file.delete" => "删除文件", "recovery.restore" => "恢复文件", "process.trim" => "释放暂用内存", _ => "文件处理" };
                _panel.SetStatus($"操作记录 {index + 1}/{entries.Length} · {operation}\n" + entry.At.ToLocalTime().ToString("MM-dd HH:mm") + " · " + state);
                var surface = _panel.Card.TaskActions;
                surface.Present(entry.Operation == "process.trim" ? "这次明确选中的软件。释放内存不能撤销，也不能据此保证电脑会更快。" :
                    string.Join("\n", entry.Resources), null, null, null);
                if (entry.State is "pending" or "outcome_unknown")
                {
                    if (entry.Operation == "process.trim") surface.AddAction("重新查看内存", () => RunUtility("memory.inspect"));
                    else surface.AddAction("核对结果", () => Process(entry, reconcile: true));
                }
                if (entry.RecoveryId is not null && entry.State is "verified" or "outcome_unknown")
                    surface.AddAction("查看恢复", () => Process(entry, reconcile: false));
                if (index > 0) surface.AddAction("上一项", () => Show(index - 1));
                if (index + 1 < entries.Length) surface.AddAction("下一项", () => Show(index + 1));
                _panel.Reposition();
            }
            async void Process(TaskReceipt entry, bool reconcile)
            {
                if (_taskActive) return;
                _taskActive = true;
                using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10)); _taskStop = stop;
                try
                {
                    var task = new TaskCoordinator(_taskRecords, TaskInteractionFor(true));
                    _panel.SetStatus(reconcile ? await task.ReconcileAsync(entry, stop.Token) : await task.RestoreAsync(entry, stop.Token));
                }
                catch (Exception ex) when (ex is IOException or JsonException or PlatformFailure or OperationCanceledException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                { _panel.SetStatus("本次处理已停止，文件及恢复材料保留。若文件已变化，请先核对，不能直接覆盖。"); }
                finally { _panel.Card.TaskActions.Clear(); _taskStop = null; _taskActive = false; }
            }
            Show(0);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or PlatformFailure or System.Security.Cryptography.CryptographicException)
        { _panel.SetStatus("操作记录暂时无法读取，原记录没有被覆盖，新的文件修改已暂停。"); }
    }
}
