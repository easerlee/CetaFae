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
                kind == "destination" ? "请选择目标文件的位置和名称。" : "需要你指定这次处理的文件，不会自动读取其他文档。", "选择位置", () =>
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
                    var picker = new OpenFileDialog { Title = "选择这次处理的文件", Multiselect = false, CheckFileExists = true };
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
        var plugin = _plugins.ChatProvider ?? throw new InvalidDataException("plugin_program_unavailable");
        var response = await _model.ConverseAsync(history, question, attached, token, attached ? _followup.ActiveGoal : null,
            attached ? _understanding.ForWindow(_ball.BoundTarget) : null, taskAvailable: true);
        if (response.Task is null) return response.Reply;
        if (_taskActive) return "上一个处理还未结束，请先停止或完成它。";
        _taskActive = true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromMinutes(10));
        _taskStop = stop;
        var task = new TaskCoordinator(_taskRecords, TaskInteractionFor(attached));
        try
        {
            return await TaskWorkflow.RunAsync(task, plugin, response,
                (facts, nextToken) => _model.ConverseAsync(history, question, attached, nextToken,
                    taskAvailable: true, taskEvidence: facts), stop.Token);
        }
        catch (OperationCanceledException)
        {
            return "已停止后续处理。" + (task.LatestReceipt is { State: "verified" } ? "已完成的改动仍保留，可从操作记录恢复。" :
                "若操作已经发出，请先查看操作记录核对结果，不要重复执行。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or PlatformFailure or System.Security.Cryptography.CryptographicException)
        {
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
                var operation = entry.Operation switch { "file.write" => "保存文字", "file.copy" => "复制文件", "file.move" => "移动文件", "file.delete" => "删除文件", "recovery.restore" => "恢复文件", _ => "文件处理" };
                _panel.SetStatus($"操作记录 {index + 1}/{entries.Length} · {operation}\n" + entry.At.ToLocalTime().ToString("MM-dd HH:mm") + " · " + state);
                var surface = _panel.Card.TaskActions;
                surface.Present(string.Join("\n", entry.Resources), null, null, null);
                if (entry.State is "pending" or "outcome_unknown") surface.AddAction("核对结果", () => Process(entry, reconcile: true));
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
