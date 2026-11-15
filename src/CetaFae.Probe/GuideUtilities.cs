using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public sealed partial class GuideSession
{
    internal static readonly string[] UtilityIds = ["image.capture", "text.extract", "text.translate", "text.paste", "memory.inspect"];
    readonly List<Window> _utilityWindows = [];
    readonly HashSet<Window> _pins = [];
    readonly HashSet<Window> _hiddenPins = [];
    readonly CancellationTokenSource _utilityStop = new();
    readonly HashSet<CancellationTokenSource> _utilityRequests = [];
    readonly DispatcherTimer _pinPresence = new() { Interval = TimeSpan.FromMilliseconds(500) };
    MemoryMonitor? _memoryMonitor;
    string? _pendingUtility;
    CancellationTokenSource? _utilityCapture;

    async void InitializeUtilities(MonitorSettings config)
    {
        _pinPresence.Tick += (_, _) =>
        {
            var hide = Native.ForegroundFullscreen(_ball.Handle);
            foreach (var pin in _pins.ToArray())
                if (hide && pin.IsVisible) { _hiddenPins.Add(pin); pin.Hide(); }
                else if (!hide && _hiddenPins.Remove(pin)) pin.Show();
        };
        if (_preview || _plugins.UtilityProvider is not { } plugin) return;
        try
        {
            var actions = await UtilityMenuAsync(plugin, _utilityStop.Token);
            if (_utilityStop.IsCancellationRequested) return;
            _menu.SetTools(actions, RunUtility);
            _memoryMonitor = new(plugin, config,
                () => _ball.FullscreenHiddenState || _ball.HasTarget || _chatWindow is { IsVisible: true } || _utilityWindows.Any(w => w.IsVisible),
                reading =>
                {
                    _panel.SetStatus($"内存持续偏高，目前约占用 {reading.UsedPercent:N0}%。需要一起看看用在哪里吗？");
                    _panel.Card.TaskActions.Present("只检查本机占用，不会关闭软件。", "帮我处理", () => RunUtility("memory.inspect"), null);
                    _panel.Card.TaskActions.AddAction("暂时忽略2小时", () => { _memoryMonitor?.Ignore(); _panel.DismissNotice(); });
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (UtilityFailure(ex)) { ProbeLog.Log("utilities_unavailable", ("type", ex.GetType().Name)); }
    }
    internal static async Task<IReadOnlyList<(string Id, string Label)>> UtilityMenuAsync(GuidePlugin plugin, CancellationToken token = default,
        bool text = false)
    {
        using var json = JsonDocument.Parse(await GuideFeature.InvokeAsync(plugin, text ? "text-menu" : "utility-menu", new { }, token));
        var root = json.RootElement;
        if (root.EnumerateObject().Count() != 1 || !root.TryGetProperty("actions", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 8)
            throw new InvalidDataException("utility_menu");
        var actions = new List<(string, string)>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.EnumerateObject().Count() != 2 || item.GetProperty("id").GetString() is not { } id ||
                !(text ? new[] { "translate", "summarize", "rewrite", "explain" } : UtilityIds).Contains(id) ||
                item.GetProperty("label").GetString() is not { Length: > 0 and <= 40 } label || label.Any(char.IsControl) || actions.Any(a => a.Item1 == id))
                throw new InvalidDataException("utility_menu");
            actions.Add((id, label));
        }
        return actions;
    }
    void AddUtilityMenu(ChatAttachment attachment, IReadOnlyList<(string Id, string Label)> actions)
    {
        attachment.Add.ContextMenu.Items.Add(new Separator());
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Label };
            item.Click += (_, _) => RunUtility(action.Id);
            attachment.Add.ContextMenu.Items.Add(item);
        }
    }
    async Task AddChatUtilitiesAsync(FreeChatWindow window, GuidePlugin plugin)
    {
        try { var actions = await UtilityMenuAsync(plugin, _utilityStop.Token);
            if (!_utilityStop.IsCancellationRequested && ReferenceEquals(_chatWindow, window)) AddUtilityMenu(window.Attachment, actions); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (UtilityFailure(ex)) { ProbeLog.Log("chat_tools_unavailable"); }
    }
    async Task<string?> UtilityIntentAsync(string question, CancellationToken token)
    {
        if (_plugins.UtilityProvider is not { } plugin) return null;
        using var json = JsonDocument.Parse(await GuideFeature.InvokeAsync(plugin, "utility-intent", new { question }, token));
        if (json.RootElement.EnumerateObject().Count() != 1 || !json.RootElement.TryGetProperty("action", out var value))
            throw new InvalidDataException("utility_intent");
        if (value.ValueKind == JsonValueKind.Null) return null;
        var action = value.GetString();
        return UtilityIds.Contains(action) ? action : throw new InvalidDataException("utility_intent");
    }
    void RunUtility(string action)
    {
        if (_utilityStop.IsCancellationRequested || !UtilityIds.Contains(action)) return;
        _panel.DismissNotice();
        if (action == "memory.inspect") { _ = ShowMemoryAsync(); return; }
        if (action == "text.paste") { ShowTextTool("", false); return; }
        if (_ball.HasTarget) { _ = CaptureUtilityAsync(_ball.BoundTarget, action); return; }
        // Menu invocation names an operation, never an arbitrary foreground window. The next drag selects its object.
        _pendingUtility = action;
        _panel.SetStatus("把鲸鱼拖到要处理的窗口。我会打开本机截图工具，不会自动发送图片。");
    }
    async Task CaptureUtilityAsync(IntPtr window, string action)
    {
        if (!_gate.CloudEnabled) { _panel.SetStatus("使用同意已撤回，未读取窗口。仍可使用本机文字工具。"); return; }
        _utilityCapture?.Cancel();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_utilityStop.Token);
        _utilityCapture = stop;
        _utilityRequests.Add(stop);
        var needsBinding = !_ball.BindingConfirmed;
        var owner = Native.ProcessId(window);
        try
        {
            stop.CancelAfter(TimeSpan.FromSeconds(5));
            var plugin = _plugins.UtilityProvider ?? throw new InvalidDataException("utility_disabled");
            if (Native.GetForegroundWindow() != window) Native.SetForegroundWindow(window);
            await Task.Delay(120, stop.Token);
            var snapshot = await ObservationWorker.ReadAsync(window, true, plugin, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (!_gate.CloudEnabled) return;
            var png = snapshot.Png ?? throw new InvalidDataException("capture_unavailable");
            if (needsBinding)
            {
                _ball.ConfirmBinding();
                ReadyBoundQuestion("已在本机读取窗口。你也可以在这里继续提问。");
            }
            if (action == "image.capture")
            {
                var view = new UtilityImageView(UtilityUi.Decode(png));
                view.PinRequested += ShowPin;
                view.TextRequested += async (bitmap, translate) =>
                {
                    try { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var bytes = new MemoryStream(); encoder.Save(bytes);
                        var extracted = await GuideTextRegions.ExtractAsync(bytes.ToArray(), _utilityStop.Token);
                        if (!_utilityStop.IsCancellationRequested) ShowTextTool(extracted, translate); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) when (UtilityFailure(ex)) { _panel.SetStatus("这块图片暂时无法提取文字，原图保留。可换一个较清楚的区域。"); }
                };
                ShowUtility(UtilityUi.Window("鲸汐 · 截图", view, 860, 620));
                return;
            }
            // The local editor also permits cropping before extraction when the user wants a smaller scope.
            var text = await GuideTextRegions.ExtractAsync(png, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) { _panel.SetStatus("没有读到清楚的文字。可以用截图工具裁剪后再试。"); return; }
            ShowTextTool(text, action == "text.translate");
        }
        catch (OperationCanceledException)
        {
            if (_utilityStop.IsCancellationRequested || !_gate.CloudEnabled || _ball.BoundTarget != window ||
                !ReferenceEquals(_utilityCapture, stop)) return;
            const string message = "已停止本次读取，没有发送图片。";
            if (needsBinding) ShowRecognitionFailure(window, owner, "utility_cancelled", message);
            else _panel.SetStatus(message);
        }
        catch (Exception ex) when (UtilityFailure(ex))
        {
            if (_utilityStop.IsCancellationRequested || !_gate.CloudEnabled || _ball.BoundTarget != window ||
                !ReferenceEquals(_utilityCapture, stop)) return;
            var message = ex.Message.Contains("sensitive", StringComparison.Ordinal)
                ? "发现敏感内容，未打开截图。请先隐藏后再试。"
                : "暂时无法读取这个窗口。请先点一下目标窗口，再选择工具并拖入鲸鱼。";
            if (needsBinding) ShowRecognitionFailure(window, owner, "utility_capture_failed", message);
            else _panel.SetStatus(message);
        }
        finally
        {
            _utilityRequests.Remove(stop);
            if (ReferenceEquals(_utilityCapture, stop)) _utilityCapture = null;
        }
    }
    void ShowUtility(Window window)
    {
        _utilityWindows.Add(window);
        window.Closed += (_, _) => { _utilityWindows.Remove(window); _pins.Remove(window); _hiddenPins.Remove(window); if (_pins.Count == 0) _pinPresence.Stop(); };
        window.Show();
    }
    void ShowPin(BitmapSource bitmap)
    {
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        var content = new DockPanel();
        var actions = new WrapPanel();
        var window = UtilityUi.Window("鲸汐 · 贴图", content, Math.Min(600, bitmap.PixelWidth), Math.Min(450, bitmap.PixelHeight + 50));
        window.Topmost = true; window.ShowActivated = false;
        actions.Children.Add(UtilityUi.Button("复制", () => { try { Clipboard.SetImage(bitmap); } catch (ExternalException) { } }));
        actions.Children.Add(UtilityUi.Button("关闭", window.Close));
        DockPanel.SetDock(actions, Dock.Top); content.Children.Add(actions); content.Children.Add(image);
        image.MouseLeftButtonDown += (_, _) => window.DragMove();
        image.MouseWheel += (_, e) => { var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            window.Width = Math.Clamp(window.Width * factor, 300, SystemParameters.WorkArea.Width);
            window.Height = Math.Clamp(window.Height * factor, 220, SystemParameters.WorkArea.Height); e.Handled = true; };
        _pins.Add(window); ShowUtility(window); _pinPresence.Start();
    }
    async void ShowTextTool(string text, bool translation)
    {
        var layout = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel(); header.Children.Add(UtilityUi.Title("文字工具"));
        var status = UtilityUi.Text("本机提取的文字可直接复制。点击翻译、概括或改写才会发送下面的文字。接收方：" + _options.BaseUrl.AbsoluteUri);
        header.Children.Add(status); var actions = new WrapPanel(); header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var source = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 16,
            Padding = new Thickness(10), MaxLength = 16000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        layout.Children.Add(source);
        var window = UtilityUi.Window("鲸汐 · 文字", layout);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_utilityStop.Token);
        var lifetimeToken = lifetime.Token;
        window.Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        actions.Children.Add(UtilityUi.Button("粘贴文字", () =>
        { try { if (Clipboard.ContainsText()) { var pasted = Clipboard.GetText(); source.Text = pasted[..Math.Min(pasted.Length, 16000)]; } } catch (ExternalException) { status.Text = "剪贴板暂时不可用。"; } }));
        actions.Children.Add(UtilityUi.Button("复制文字", () => { try { Clipboard.SetText(source.Text); } catch (ExternalException) { status.Text = "暂时无法复制。"; } }));
        var busy = false;
        async void Process(string goal)
        {
            if (busy || string.IsNullOrWhiteSpace(source.Text)) return;
            if (!_gate.CloudEnabled) { status.Text = "联网使用同意已撤回，仍可在本机复制文字。"; return; }
            busy = true; status.Text = "正在处理…";
            var submitted = source.Text;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            _utilityRequests.Add(stop);
            try
            {
                var result = await _model.ProcessTextAsync(_plugins.UtilityProvider!, submitted, goal, stop.Token);
                if (!stop.IsCancellationRequested && _gate.CloudEnabled)
                {
                    source.Text = "原文：\n" + submitted + "\n\n结果：\n" + result;
                    status.Text = "结果已就绪，可复制或选中文字继续处理。";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (UtilityFailure(ex) || ex is System.Net.Http.HttpRequestException)
            { status.Text = "这次没有取得结果，原文保留。可稍后再试。"; }
            finally { _utilityRequests.Remove(stop); busy = false; }
        }
        ShowUtility(window);
        if (translation) status.Text = "已在本机提取文字。点击“翻译”发送下方原文；可先删去不需要发送的部分。接收方：" + _options.BaseUrl.AbsoluteUri;
        try
        {
            foreach (var action in await UtilityMenuAsync(_plugins.UtilityProvider!, lifetimeToken, text: true))
                if (!lifetimeToken.IsCancellationRequested) actions.Children.Add(UtilityUi.Button(action.Label, () => Process(action.Id)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (UtilityFailure(ex)) { status.Text = "文字处理插件暂不可用，仍可复制本机文字。"; }
    }

    async Task ShowMemoryAsync()
    {
        var layout = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel(); header.Children.Add(UtilityUi.Title("内存用在哪里"));
        var status = UtilityUi.Text("正在本机观察几秒钟… 不会读取画面或发送软件列表。"); header.Children.Add(status);
        var actions = new WrapPanel(); header.Children.Add(actions); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var list = new ListBox { FontSize = 16, BorderThickness = new Thickness(0) }; layout.Children.Add(list);
        var window = UtilityUi.Window("鲸汐 · 内存", layout, 630, 530);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_utilityStop.Token);
        var token = stop.Token;
        window.Closed += (_, _) => { stop.Cancel(); stop.Dispose(); };
        ShowUtility(window);
        var busy = true;
        try
        {
            var start = Stopwatch.StartNew(); var before = await Task.Run(MemoryDiagnostics.Snapshot, token);
            await Task.Delay(4000, token);
            var after = await Task.Run(MemoryDiagnostics.Snapshot, token); var reading = MemoryMonitor.Read();
            var growing = after.Count(p => before.Any(b => b.Id == p.Id && b.StartedAtTicks == p.StartedAtTicks && p.PrivateBytes - b.PrivateBytes > 16 * 1048576));
            var cpu = after.Sum(p => before.Where(b => b.Id == p.Id && b.StartedAtTicks == p.StartedAtTicks)
                .Sum(b => Math.Max(0, p.CpuSeconds - b.CpuSeconds))) / start.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
            var summary = await GuideFeature.TextAsync(_plugins.UtilityProvider!, "memory-summary", new { memoryFacts = new {
                percent = reading.UsedPercent, availableMB = reading.Available / 1048576d, growing, cpuPercent = cpu } }, token);
            if (token.IsCancellationRequested) return;
            status.Text = summary; foreach (var p in after) list.Items.Add(p); busy = false;
            actions.Children.Add(UtilityUi.Button("释放所选软件的暂用内存", async () =>
            {
                if (busy || list.SelectedItem is not MemoryConsumer selected) return;
                busy = true;
                status.Text = "正在尝试释放所选软件的暂用内存，不会关闭软件。此操作不能撤销，可能很快重新占用。";
                try
                {
                    var call = new PlatformRequest(1, Guid.NewGuid().ToString("N"), "process.trim", JsonSerializer.SerializeToElement(new { processId = selected.Id }));
                    var receipt = _taskRecords.Begin(call, [$"process:{selected.Id}:{selected.StartedAtTicks}"]);
                    var scope = new PlatformScope { Operations = ["process.trim"], Processes = [new(selected.Id, selected.StartedAtTicks)],
                        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20) };
                    var session = new PlatformSession(scope, _taskRecords.Recovery);
                    var result = await session.ExecuteAsync(call, token); _taskRecords.Finish(receipt, result);
                    if (result.Status != "verified") { status.Text = "这个软件暂时不允许释放，或已经退出。没有关闭它，可稍后重新检查。"; return; }
                    var e = result.Evidence!.Value;
                    status.Text = $"暂用内存：{e.GetProperty("beforeBytes").GetInt64() / 1048576d:N0} → {e.GetProperty("afterBytes").GetInt64() / 1048576d:N0} MB。正在观察是否回升…";
                    await Task.Delay(5000, token);
                    var rebound = (await Task.Run(MemoryDiagnostics.Snapshot, token)).FirstOrDefault(p => p.Id == selected.Id && p.StartedAtTicks == selected.StartedAtTicks);
                    status.Text += rebound is null ? "\n暂时无法继续读取所选软件。" : $"\n5秒后约 {rebound.WorkingSet / 1048576d:N0} MB；系统现可用 {MemoryMonitor.Read().Available / 1048576d:N0} MB。回升并不代表失败，也不能据此保证电脑会更快。";
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (UtilityFailure(ex) || ex is System.Security.Cryptography.CryptographicException)
                { status.Text = "暂时无法确认结果，请查看操作记录；不会自动重试或关闭软件。"; }
                finally { busy = false; }
            }));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (UtilityFailure(ex)) { status.Text = "暂时无法取得内存信息，未改动软件。"; }
    }
    void CancelUtilityRequests()
    {
        _pendingUtility = null;
        foreach (var request in _utilityRequests.ToArray()) request.Cancel();
    }
    static bool UtilityFailure(Exception ex) => ex is IOException or InvalidOperationException or ArgumentException or JsonException or
        System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException or ExternalException;
    void DisposeUtilities()
    {
        CancelUtilityRequests(); _utilityStop.Cancel(); _memoryMonitor?.Dispose(); _pinPresence.Stop();
        foreach (var window in _utilityWindows.ToArray()) window.Close();
        _pendingUtility = null;
    }
}
