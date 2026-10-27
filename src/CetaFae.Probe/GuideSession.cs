using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public sealed class GuideSession : IDisposable
{
    readonly BallWindow _ball;
    readonly GuidePanel _panel;
    readonly OverlayWindow _marker = new();
    readonly GuideRunGate _gate = new();
    readonly GuideOptions _options;
    readonly GuideModel _model;
    readonly PluginCatalog _plugins;
    CancellationTokenSource? _request;
    GuideObservation? _context;
    int _contextVersion;
    int? _activationVersion;
    IntPtr _activationSource;
    bool _checkingContext;
    bool _contextCheckPending;
    readonly bool _preview;

    public GuideSession(BallWindow ball, GuideOptions options, bool preview, PluginCatalog plugins)
    {
        _ball = ball;
        _options = options;
        _plugins = plugins;
        _model = new GuideModel(options);
        _panel = new GuidePanel(options);
        _preview = preview;
        _panel.CloudToggled += ToggleCloud;
        _panel.Cancelled += () => _ball.StopTracking("cancelled");
        _panel.AnswerSubmitted += OnAnswerSubmitted;
        _ball.AllowsForeground = IsAllowedForeground;
        _ball.TargetSelected += OnSelected;
        _ball.TrackingStopped += Cancel;
        _ball.TargetMoved += OnTargetChanged;
        _panel.Show();
        if (preview)
        {
            _panel.SetStatus("静态预览：已找到下一步。这里展示较长的中文解释，验证换行、滚动和按钮是否可见。" +
                string.Concat(Enumerable.Repeat("窗口内容只是演示数据，不读取其他应用，也不发送模型请求。", 8)));
            _panel.ShowQuestion();
            _marker.Left = SystemParameters.WorkArea.Left + 100;
            _marker.Top = SystemParameters.WorkArea.Top + 180;
            _marker.Width = 180;
            _marker.Height = 44;
            _marker.Show();
        }
    }

    void ToggleCloud()
    {
        if (_preview) { _panel.SetStatus("这是静态预览，不会启用采集或发送请求。"); return; }
        _gate.SetCloud(!_gate.CloudEnabled);
        _panel.SetCloud(_gate.CloudEnabled);
        _ball.StopTracking("cloud_changed");
        _panel.SetStatus(_gate.CloudEnabled
            ? "已启用。请将问号球拖到已启用插件支持的窗口。目标不明确时会短问一句。"
            : "云端已关闭，不再读取或发送。已发送的内容无法从上游撤回。");
        ProbeLog.Log("guide_consent", ("enabled", _gate.CloudEnabled));
    }

    void Cancel(string reason)
    {
        _activationVersion = null;
        _context = null;
        _contextCheckPending = false;
        _gate.Cancel();
        _request?.Cancel();
        _panel.ClearQuestion();
        if (_marker.IsVisible) _marker.Hide();
        if (_gate.CloudEnabled) _panel.SetStatus("本次指路已清除，请重新拖放悬浮球。");
        ProbeLog.Log("guide_cancel", ("reason", reason));
    }

    async void OnTargetChanged()
    {
        _contextCheckPending = true;
        if (_checkingContext || _context is null || IsInputForeground(Native.GetForegroundWindow())) return;
        _checkingContext = true;
        try
        {
            while (_contextCheckPending && _context is { } context && _gate.Accepts(_contextVersion))
            {
                if (IsInputForeground(Native.GetForegroundWindow())) break;
                _contextCheckPending = false;
                var version = _contextVersion;
                var token = _request!.Token;
                try
                {
                    using var deadline = new GuideDeadline(_options.Timeout, token);
                    var current = await ReadContextAsync(context, deadline.Token);
                    if (_gate.Accepts(version) && !GuideObservation.HasSameContext(context.Candidates, current))
                        GuideObservation.LogContextDifference(context.Candidates, current);
                    if (_gate.CancelIfContextChanged(version, context.Candidates, current))
                        _ball.StopTracking("target_changed");
                    else if (_gate.Accepts(version)) ProbeLog.Log("guide_context_unchanged");
                }
                catch (OperationCanceledException) when (!_gate.Accepts(version)) { }
                catch (Exception)
                {
                    if (_gate.Accepts(version) && !IsInputForeground(Native.GetForegroundWindow()))
                        _ball.StopTracking("target_context_unavailable");
                }
            }
        }
        finally { _checkingContext = false; }
    }

    bool IsInputForeground(IntPtr window) => window == _panel.Handle && _panel.IsAnswering &&
        _context is not null && _gate.CanClarify(_contextVersion);

    bool IsAllowedForeground(IntPtr window) => IsInputForeground(window) ||
        _activationVersion is { } version && _gate.Accepts(version) &&
        (window == IntPtr.Zero || window == _activationSource);

    async void OnAnswerSubmitted(string text)
    {
        if (_preview || _context is not { } previous || !_gate.CanClarify(_contextVersion)) return;
        string intent;
        try { intent = _gate.TakeClarification(_contextVersion, text); }
        catch (InvalidDataException)
        {
            _panel.SetStatus("请用一行文字说明你想处理的事（最多300字）。");
            return;
        }
        _panel.ClearQuestion();
        // Submission returns to this target through the same bounded activation path as a drop.
        await RecognizeAsync(previous.Window, intent, previous);
    }

    async void OnSelected(IntPtr window) => await RecognizeAsync(window, null, null);

    async Task RecognizeAsync(IntPtr window, string? intent, GuideObservation? previous)
    {
        if (_preview) return;
        if (!_gate.CloudEnabled)
        {
            _ball.StopTracking("consent_required");
            _panel.SetStatus("请先阅读说明并启用云端；尚未读取窗口内容或发送请求。");
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var request = new CancellationTokenSource();
        _request = request;
        using var deadline = new GuideDeadline(_options.Timeout, request.Token);
        var token = deadline.Token;
        var version = _gate.Begin();
        _context = null;
        _panel.ClearQuestion();
        var total = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) || Native.IsIconic(window))
                throw new InvalidDataException("target_unavailable");
            using var targetProcess = Process.GetProcessById((int)Native.ProcessId(window));
            var plugin = _plugins.Resolve(targetProcess.ProcessName, Native.Title(window));
            if (previous is not null && previous.PluginId != plugin.Id)
                throw new InvalidDataException("target_changed");
            if (!Native.IsNonElevated(window)) throw new InvalidDataException("elevated_target");
            _panel.Place(GuideObservation.WindowRect(window));
            _panel.SetStatus("正在读取所选窗口…");
            if (Native.GetForegroundWindow() != window)
            {
                // The release selects this target explicitly; never activate the panel or retry in the background.
                _activationVersion = version;
                _activationSource = Native.GetForegroundWindow();
                try
                {
                    var accepted = Native.SetForegroundWindow(window);
                    ProbeLog.Log("guide_target_activation", ("target", Native.Hex(window)),
                        ("fg", Native.Hex(Native.GetForegroundWindow())), ("accepted", accepted));
                    var confirmed = await WaitForForegroundAsync(window, Native.GetForegroundWindow, token);
                    if (!_gate.Accepts(version)) return;
                    ProbeLog.Log("guide_target_activation_complete", ("target", Native.Hex(window)),
                        ("fg", Native.Hex(Native.GetForegroundWindow())), ("confirmed", confirmed));
                    if (!confirmed) throw new InvalidDataException("target_activation_failed");
                }
                finally { if (_activationVersion == version) _activationVersion = null; }
            }
            if (previous is not null)
                previous.ValidateContext(await ReadContextAsync(previous, token), null);
            var observation = await deadline.RunAsync(t => ObservationWorker.ReadAsync(window, _options.IncludeImage, plugin, t));
            if (!_gate.Accepts(version)) return;
            _context = observation with { Png = null };
            _contextVersion = version;
            if (_contextCheckPending) OnTargetChanged();
            var observeMs = total.ElapsedMilliseconds;
            _panel.SetStatus("正在识别下一步…");
            ProbeLog.Log("guide_request_start", ("observeMs", observeMs),
                ("candidates", observation.Candidates.Count), ("image", _options.IncludeImage));
            var task = intent is null ? GuideObservation.TaskDescription :
                "用户明确提出的目标：" + intent + "。只解释和指路，不执行任何操作；目标已满足时说明现状，不指向相反动作。";
            var answer = (await deadline.RunAsync(t => _model.AskAsync(task, observation.Candidates, observation.Png, t))).ForIntent(intent);
            var modelMs = total.ElapsedMilliseconds - observeMs;
            ProbeLog.Log("guide_response_received", ("modelMs", modelMs), ("mode", answer.Mode));
            if (!_gate.Accepts(version)) return;
            if (!GuideObservation.IsCurrent(window, observation.ProcessId, observation.Frame))
                throw new InvalidDataException("target_changed");
            var bounds = observation.ValidateContext(await ReadContextAsync(observation, token), answer.CandidateId);
            if (answer.CandidateId is { } candidateId &&
                !plugin.AllowsPoint(intent, observation.Candidates.Single(c => c.Id == candidateId).Name))
                throw new InvalidDataException("stale_candidate");
            token.ThrowIfCancellationRequested();
            if (!_gate.Accepts(version)) return;
            if (!GuideObservation.IsCurrent(window, observation.ProcessId, observation.Frame))
                throw new InvalidDataException("target_changed");
            _marker.Hide();
            if (bounds is { } rect)
            {
                _marker.Left = rect.Left / _ball.Scale;
                _marker.Top = rect.Top / _ball.Scale;
                _marker.Width = rect.Width / _ball.Scale;
                _marker.Height = rect.Height / _ball.Scale;
                _marker.Show();
            }
            _panel.Place(observation.Frame, bounds);
            _panel.SetStatus(answer.Explanation);
            if (answer.Mode == "ask")
            {
                _gate.AwaitClarification(version);
                _panel.ShowQuestion();
            }
            token.ThrowIfCancellationRequested();
            ProbeLog.Log("guide_result", ("mode", answer.Mode), ("candidates", observation.Candidates.Count),
                ("observeMs", observeMs), ("modelMs", modelMs), ("totalMs", total.ElapsedMilliseconds),
                ("withinThreeSeconds", total.ElapsedMilliseconds <= 3000), ("image", _options.IncludeImage));
        }
        catch (OperationCanceledException)
        {
            var cancelled = request.IsCancellationRequested;
            if (_gate.Accepts(version))
            {
                _ball.StopTracking("processing_timeout");
                _panel.SetStatus("识别超时，请重新拖放。此次没有指向任何位置。");
            }
            ProbeLog.Log("guide_failure", ("category", cancelled ? "cancelled" : "timeout"));
        }
        catch (Exception ex)
        {
            if (ex is InvalidDataException changed && changed.Message == "target_changed")
            {
                if (_gate.Accepts(version)) _ball.StopTracking("target_changed");
                return;
            }
            if (_gate.Accepts(version))
            {
                _ball.StopTracking("recognition_failed");
                _panel.SetStatus(FailureMessage(ex));
            }
            // Exception messages can contain provider content or URLs; log categories only.
            ProbeLog.Log("guide_failure", ("category", ex is InvalidDataException ? "validation" :
                ex is JsonException ? "invalid_json" : ex is HttpRequestException ? "network" : ex.GetType().Name));
        }
        finally
        {
            // Keep the active token available for event-driven revalidation after displaying the answer.
            if (!ReferenceEquals(_request, request)) request.Dispose();
        }
    }

    public static async Task<bool> WaitForForegroundAsync(IntPtr target, Func<IntPtr> getForeground,
        CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (getForeground() == target) return true;
            var remainingMs = 500 - elapsed.ElapsedMilliseconds;
            if (remainingMs <= 0) return false;
            await Task.Delay((int)Math.Min(25, remainingMs), token);
        }
    }

    public static string FailureMessage(Exception ex) => ex switch
    {
        HttpRequestException => "识别服务不可用，请检查本机模型代理。",
        InvalidDataException { Message: "target_not_foreground" } =>
            "目标窗口不在前台。请先点击目标窗口的空白处，再拖入问号球；此次未读取窗口内容或发送请求。",
        InvalidDataException { Message: "target_activation_failed" } =>
            "系统未允许激活目标窗口。请先点击目标窗口的空白处，再拖入问号球；此次未读取窗口内容或发送请求。",
        InvalidDataException { Message: "unsupported_target" } =>
            "没有已启用插件支持这个窗口，此次未读取窗口内容或发送请求。",
        InvalidDataException { Message: "plugin_conflict" } =>
            "多个已启用插件匹配这个窗口，暂不识别。请调整插件启用清单后重启。",
        _ => "未取得可靠的窗口信息或模型结果，此次不标记位置。"
    };

    async Task<IReadOnlyList<GuideCandidate>> ReadContextAsync(GuideObservation previous, CancellationToken token)
    {
        if (!GuideObservation.IsCurrent(previous.Window, previous.ProcessId, previous.Frame))
            throw new InvalidDataException("target_changed");
        var current = await ObservationWorker.ReadAsync(previous.Window, false, _plugins.Get(previous.PluginId), token);
        if (current.ProcessId != previous.ProcessId || current.Frame != previous.Frame)
            throw new InvalidDataException("target_changed");
        return current.Candidates;
    }

    public void Dispose()
    {
        _ball.TargetSelected -= OnSelected;
        _ball.TrackingStopped -= Cancel;
        _ball.TargetMoved -= OnTargetChanged;
        _ball.AllowsForeground = null;
        _panel.AnswerSubmitted -= OnAnswerSubmitted;
        _panel.ClearQuestion();
        _request?.Cancel();
        _request?.Dispose();
        _gate.SetCloud(false);
        _model.Dispose();
        if (_marker.IsLoaded) _marker.Close();
        if (_panel.IsLoaded) _panel.Close();
    }
}
