using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public sealed class GuideSession : IDisposable
{
    enum ProcessingStage { Activation, SourceValidation, Collection, TargetValidation, Payload, Model, ResultValidation }
    readonly BallWindow _ball;
    readonly GuidePanel _panel;
    readonly OverlayWindow _marker = new(control: true);
    readonly GuideRunGate _gate = new();
    readonly GuideFollowup _followup = new();
    GuideFollowupRequest? _activeFollowup;
    int _followupVersion;
    readonly GuideOptions _options;
    readonly GuideModel _model;
    readonly FreeChatSession _chat;
    readonly FreeChatSession _questions;
    FreeChatWindow? _chatWindow;
    readonly CloudConsentStore _consent;
    readonly GuideMenu _menu;
    readonly PluginCatalog _plugins;
    CancellationTokenSource? _request;
    GuideObservation? _context;
    int _contextVersion;
    int? _activationVersion;
    int? _initialCollectionVersion;
    int? _modelVersion;
    int? _routingVersion;
    bool _retryInitialOnResume;
    TaskCompletionSource? _foregroundReturn;
    IntPtr _activationSource;
    bool _checkingContext;
    bool _contextCheckPending;
    bool _boundQuestionReady;
    RegionPicker? _picker;
    string? _pendingIntent;
    readonly bool _preview;

    public GuideSession(BallWindow ball, GuideOptions options, bool preview, PluginCatalog plugins,
        Action? settings = null, bool restoreConsent = true)
    {
        _ball = ball;
        _options = options;
        _plugins = plugins;
        _model = new GuideModel(options, windowGuidanceAvailable:
            plugins.Plugins.Any(plugin => plugin.Kind is "general" or "application"));
        _chat = new FreeChatSession(async (history, question, token) => await _model.ChatAsync(history, question, token));
        _questions = new FreeChatSession(_model.RouteQuestionAsync);
        _panel = new GuidePanel(options);
        _panel.BindEntry(() => _ball.FeedbackBounds, () => _ball.Scale, () => _preview || _ball.HasTarget, () => _ball.TopAttached,
            () => _ball.Workspace);
        _ball.FullscreenHidden += OnFullscreenHidden;
        _ball.LocationChanged += OnEntryChanged;
        _ball.SizeChanged += OnEntryChanged;
        _preview = preview;
        _consent = new CloudConsentStore(options);
        var saved = preview || !restoreConsent ? default : _consent.Read();
        _gate.SetCloud(saved.Enabled);
        _panel.SetCloud(saved.Enabled);
        _menu = new GuideMenu(ball, () => _ball.StopTracking("cancelled"), settings: settings);
        _menu.SetCloud(saved.Enabled);
        _panel.MenuRequested += OnMenuRequested;
        _panel.Cancelled += () => _ball.StopTracking("cancelled");
        _panel.AnswerSubmitted += OnAnswerSubmitted;
        _ball.AnswerSubmitted += OnAnswerSubmitted;
        _ball.HomeClicked += ToggleChat;
        _panel.RegionRequested += OnRegionRequested;
        _panel.FollowupRequested += OnFollowupRequested;
        _ball.AllowsForeground = IsAllowedForeground;
        _ball.TargetSelected += OnSelected;
        _ball.TrackingStopped += Cancel;
        _ball.TargetMoved += OnTargetChanged;
        _ball.BindingSuspendedChanged += OnBindingSuspended;
        _ball.BindingResumed += OnBindingResumed;
        _ball.GuidanceCleared += OnGuidanceCleared;
        _ball.MenuRequested += OnMenuRequested;
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

    void OnMenuRequested()
    {
        _menu.Show();
    }

    void OnFullscreenHidden()
    {
        CloseChat();
        _panel.DismissNotice();
    }

    void ToggleChat()
    {
        if (_plugins.ChatProvider is null) return;
        if (_chatWindow is { } existing) { existing.Close(); return; }
        if (_preview || !_gate.CloudEnabled)
        {
            _panel.SetStatus(_preview ? "这是静态预览。安装后可以使用自由问答。" : "请先通过安装程序确认使用协议，再使用自由问答。");
            return;
        }
        if (_ball.HasTarget) return;
        ResetRecognition();
        var capsule = new FreeChatWindow { Owner = _ball };
        _chatWindow = capsule;
        capsule.Submitted += text => SubmitChat(capsule, text);
        capsule.Closed += (_, _) =>
        {
            if (_chatWindow != capsule) return;
            _chatWindow = null;
            _chat.Reset();
        };
        capsule.Place(_ball.FeedbackBounds, _ball.Workspace);
        capsule.Show();
    }

    async void SubmitChat(FreeChatWindow capsule, string text)
    {
        if (_chatWindow != capsule || !_gate.CloudEnabled) return;
        capsule.SetStatus("鲸汐正在回答…", busy: true);
        capsule.Place(_ball.FeedbackBounds, _ball.Workspace);
        try
        {
            var turn = await _chat.SubmitAsync(text);
            if (_chatWindow == capsule && turn is not null) capsule.ShowTurn(turn);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (_chatWindow == capsule)
                capsule.SetStatus(ex.Message == "sensitive_content" ? "请去掉密钥等敏感内容后再发送。" : "这次没能回答，请再试一次。");
        }
        if (_chatWindow == capsule) capsule.Place(_ball.FeedbackBounds, _ball.Workspace);
    }

    void CloseChat()
    {
        if (_chatWindow is not { } capsule) return;
        _chatWindow = null;
        _chat.Reset();
        capsule.Close();
    }

    internal bool CloudEnabled => _gate.CloudEnabled;
    internal void EndForSettings() => ResetRecognition();
    internal void RevokeForConfiguration()
    {
        _ball.StopTracking("configuration_changed");
        _gate.SetCloud(false);
        _panel.SetCloud(false); _menu.SetCloud(false);
    }
    internal void ManageConsent() { if (_gate.CloudEnabled) RevokeConsent(); else ShowConsent(); }
    internal void ShowConfigurationWarning() => _panel.SetStatus(
        "本机设置无法读取，云端保持关闭。请从鲸鱼右键或托盘打开鲸汐设置，核对并保存后重新运行安装程序确认协议。");

    internal void ShowConsent()
    {
        _panel.ShowConsent();
    }

    void RevokeConsent()
    {
        if (_preview) { _panel.SetStatus("这是静态预览，不会更改使用同意。"); return; }
        _ball.StopTracking("consent_withdrawn");
        _gate.SetCloud(false); _panel.SetCloud(false); _menu.SetCloud(false);
        var saved = _consent.Revoke();
        _panel.SetStatus("已撤回同意，不再读取窗口或发起识别请求。已发送的内容无法从上游撤回。" +
            (saved ? "再次使用请重新运行安装程序确认协议。" : "撤回未能保存；本次运行已停止识别，下次启动前请修复配置目录权限，避免旧授权恢复。"));
        ProbeLog.Log("guide_consent", ("enabled", false), ("saved", saved));
    }

    void Cancel(string reason)
    {
        var explain = reason == "target_changed" &&
            _context is { } context && Native.GetForegroundWindow() == context.Window;
        ResetRecognition();
        if (explain)
            _panel.SetStatus(FailureMessage(new InvalidDataException(reason)));
        ProbeLog.Log("guide_cancel", ("reason", reason));
    }

    void ResetRecognition(bool preserveConversation = false)
    {
        CloseChat();
        if (!preserveConversation) _questions.Reset();
        _boundQuestionReady = false;
        _activationVersion = null;
        _initialCollectionVersion = _modelVersion = null;
        _routingVersion = null;
        _retryInitialOnResume = false;
        _foregroundReturn = null;
        var picker = _picker;
        _picker = null;
        picker?.Abort();
        _pendingIntent = null;
        _activeFollowup = null;
        _followup.Clear();
        _panel.SetRegionAvailable(false);
        _context = null;
        _contextCheckPending = false;
        _gate.Cancel();
        _request?.Cancel();
        _panel.ClearQuestion();
        _ball.ClearQuestion();
        if (_marker.IsVisible) _marker.HideAndReset();
        _panel.EndSession();
    }

    void OnBindingSuspended()
    {
        if (_modelVersion is { } sent && _gate.Accepts(sent))
        {
            _foregroundReturn ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            _panel.Hide();
            _marker.HideAndReset();
            return;
        }
        var retry = _initialCollectionVersion is { } collecting && _gate.Accepts(collecting);
        ResetRecognition();
        _retryInitialOnResume = retry;
    }

    async void OnBindingResumed()
    {
        if (_foregroundReturn is { } waiting)
        {
            _foregroundReturn = null;
            _ball.ConfirmBinding();
            _ball.ResumeProcessing();
            waiting.TrySetResult();
        }
        else if (_retryInitialOnResume)
        {
            _retryInitialOnResume = false;
            await RecognizeAsync(_ball.BoundTarget, null, null);
        }
        else ReadyBoundQuestion("已回到这个窗口。你想了解什么？");
    }

    Task<bool?> WaitForPresentationAsync(int version, CancellationToken requestToken) =>
        WaitForPresentationAsync(_ball.UpdateBoundPresence, () => _gate.Accepts(version),
            () => _foregroundReturn?.Task, requestToken);

    internal static async Task<bool?> WaitForPresentationAsync(Action refreshPresence, Func<bool> accepts,
        Func<Task?> foregroundReturn, CancellationToken requestToken)
    {
        if (!accepts() || requestToken.IsCancellationRequested) return null;
        var deferred = false;
        refreshPresence();
        while (accepts() && foregroundReturn() is { } waiting)
        {
            deferred = true;
            try { await waiting.WaitAsync(requestToken); }
            catch (OperationCanceledException) { return null; }
            if (!accepts() || requestToken.IsCancellationRequested) return null;
            refreshPresence();
        }
        return accepts() && !requestToken.IsCancellationRequested ? deferred : null;
    }
    void OnGuidanceCleared() => ReadyBoundQuestion("本次识别已停止。你想了解什么？");

    void ReadyBoundQuestion(string message, bool preserveConversation = false)
    {
        ResetRecognition(preserveConversation);
        if (!_gate.CloudEnabled || !_ball.HasTarget || _ball.BindingSuspended) return;
        _contextVersion = _gate.Begin();
        _gate.AwaitClarification(_contextVersion);
        _boundQuestionReady = true;
        _ball.ReadyBoundInput();
        if (!_ball.HasTarget || _ball.BindingSuspended) return;
        _panel.SetStatus(message);
        ProbeLog.Log("guide_bound_ready");
    }

    void ShowRecognitionFailure(IntPtr window, uint processId, string reason, string message)
    {
        if (_ball.IsBoundForeground(window, processId))
        {
            _ball.ConfirmBinding();
            ResetRecognition();
            _ball.SetFailure();
        }
        else if (_ball.BoundTarget == window && _ball.BindingConfirmed)
        {
            ResetRecognition();
            return;
        }
        else
            _ball.StopTracking(reason);
        _panel.SetStatus(message);
    }

    async void OnTargetChanged()
    {
        if (_ball.BindingSuspended || _chatWindow is not null || _routingVersion is not null) return;
        _contextCheckPending = true;
        if (_context is { Snapshot: true } snapshot)
        {
            // A snapshot can track declared page anchors without re-reading its image.
            if (!Native.IsWindow(snapshot.Window) || !Native.IsWindowVisible(snapshot.Window) ||
                Native.IsIconic(snapshot.Window) || Native.ProcessId(snapshot.Window) != snapshot.ProcessId ||
                GuideObservation.WindowRect(snapshot.Window) != snapshot.Frame)
            {
                ReadyBoundQuestion("窗口已有变化，旧说明已清除。你想了解什么？");
                return;
            }
            if (snapshot.PageAnchors is null || _modelVersion == _contextVersion)
            {
                _contextCheckPending = false;
                return;
            }
        }
        if (_followup.WaitingForUser && _followup.Target is { } armed)
        {
            // Geometry/lifetime checks are allowed while armed, but do not inspect changed content.
            if (!Native.IsWindow(armed.Window) || !Native.IsWindowVisible(armed.Window) || Native.IsIconic(armed.Window) ||
                Native.ProcessId(armed.Window) != armed.ProcessId) ReadyBoundQuestion("窗口已有变化，旧说明已清除。你想了解什么？");
            _contextCheckPending = false;
            return;
        }
        if (_picker is not null && _context is { } selecting)
        {
            if (!Native.IsWindow(selecting.Window) || !Native.IsWindowVisible(selecting.Window) ||
                Native.IsIconic(selecting.Window) || Native.ProcessId(selecting.Window) != selecting.ProcessId ||
                GuideObservation.WindowRect(selecting.Window) != selecting.Frame)
                ReadyBoundQuestion("窗口已有变化，旧选区已清除。你想了解什么？");
            return;
        }
        if (_checkingContext || _context is null || IsInputForeground(Native.GetForegroundWindow())) return;
        _checkingContext = true;
        try
        {
            while (_contextCheckPending && _context is { } context && _gate.Accepts(_contextVersion) && _routingVersion is null)
            {
                if (_picker is not null || IsInputForeground(Native.GetForegroundWindow())) break;
                _contextCheckPending = false;
                var version = _contextVersion;
                var token = _request!.Token;
                try
                {
                    using var deadline = new GuideDeadline(_options.Timeout, token);
                    var current = await ReadContextAsync(context, deadline.Token);
                    if (_routingVersion is not null) break;
                    // A picker/input may have opened while the native worker was reading.
                    // Defer that result; resuming recognition performs the bound revalidation.
                    if (_picker is not null || IsInputForeground(Native.GetForegroundWindow()))
                    { _contextCheckPending = true; break; }
                    if (_gate.Accepts(version) && !GuideObservation.HasSameContext(context.Candidates, current.Candidates))
                        GuideObservation.LogContextDifference(context.Candidates, current.Candidates);
                    if (_gate.CancelIfContextChanged(version, context.Candidates, current.Candidates) ||
                        _gate.Accepts(version) && (!context.HasSamePage(current) ||
                            !context.HasSameScope(current) || !context.HasSameVisual(current)))
                        ReadyBoundQuestion("窗口已有变化，旧说明已清除。你想了解什么？");
                    else if (_gate.Accepts(version)) ProbeLog.Log("guide_context_unchanged");
                }
                catch (OperationCanceledException) when (!_gate.Accepts(version) || token.IsCancellationRequested) { }
                catch (InvalidDataException ex) when (context.Snapshot && ex.Message == "sensitive_content")
                {
                    if (_gate.Accepts(version) && !_ball.BindingSuspended && _routingVersion is null)
                        ShowRecognitionFailure(context.Window, context.ProcessId, ex.Message, FailureMessage(ex));
                }
                catch (Exception)
                {
                    if (_gate.Accepts(version) && _routingVersion is null && _picker is null && !IsInputForeground(Native.GetForegroundWindow()))
                        ShowRecognitionFailure(context.Window, context.ProcessId, "target_context_unavailable",
                            FailureMessage(new InvalidDataException("target_context_unavailable")));
                }
            }
        }
        finally { _checkingContext = false; }
    }

    void OnEntryChanged(object? sender, EventArgs args)
    {
        if (_panel.IsVisible) _panel.Reposition();
        _chatWindow?.Place(_ball.FeedbackBounds, _ball.Workspace);
    }

    bool IsInputForeground(IntPtr window) =>
        (window == _panel.Handle && _panel.IsSessionInteraction || window == _ball.Handle && _ball.IsSessionInteraction) &&
        ((_context is not null || _boundQuestionReady) && _gate.Accepts(_contextVersion) || _followup.WaitingForUser && _gate.Accepts(_followupVersion));

    bool IsAllowedForeground(IntPtr window) => IsInputForeground(window) ||
        _chatWindow is { } capsule && window != IntPtr.Zero && window == capsule.Handle ||
        _menu.OwnsForeground(window) ||
        _picker is not null && (window == _picker.Handle || window == IntPtr.Zero) ||
        _activationVersion is { } version && _gate.Accepts(version) &&
        (window == IntPtr.Zero || window == _activationSource);

    async void OnAnswerSubmitted(string text)
    {
        if (_plugins.ChatProvider is null)
        {
            await SubmitWindowQuestionAsync(text);
            return;
        }
        var version = _followup.WaitingForUser ? _followupVersion : _contextVersion;
        if (_preview || !_gate.CloudEnabled || !_ball.HasTarget || _ball.BindingSuspended ||
            !_gate.Accepts(version) || _routingVersion is not null ||
            !(_boundQuestionReady || _followup.WaitingForUser || _followup.CanOffer || _context is not null && _gate.CanClarify(version))) return;
        _request?.Cancel();
        _request?.Dispose();
        var request = new CancellationTokenSource();
        _request = request;
        _routingVersion = _modelVersion = version;
        _ball.ClearQuestion();
        _panel.ClearQuestion();
        _marker.HideAndReset();
        _panel.SetControl(null);
        _panel.SetPoint(false);
        _panel.SetRegionAvailable(false);
        _panel.SetFollowupAvailable(false);
        _panel.BeginProcessing();
        _ball.SetProcessing(true);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var answer = await _questions.SubmitAsync(text, request.Token);
            var remaining = _options.Timeout - elapsed.Elapsed;
            if (answer is null || await WaitForPresentationAsync(version, request.Token) is null) return;
            _routingVersion = null;
            if (_modelVersion == version) _modelVersion = null;
            _ball.SetProcessing(false);
            if (answer.RequiresWindow)
            {
                if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
                await SubmitWindowQuestionAsync(text, remaining);
            }
            else ReadyBoundQuestion(answer.Reply, preserveConversation: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (await WaitForPresentationAsync(version, request.Token) is not null)
                ReadyBoundQuestion(ex is InvalidDataException { Message: "chat_question" }
                    ? "请用一行文字提问，最多300字。" : FailureMessage(ex), preserveConversation: true);
        }
        finally
        {
            if (_routingVersion == version) _routingVersion = null;
            if (_modelVersion == version) _modelVersion = null;
            if (!ReferenceEquals(_request, request)) request.Dispose();
        }
    }

    async Task SubmitWindowQuestionAsync(string text, TimeSpan? budget = null)
    {
        if (!_preview && _boundQuestionReady && _ball.HasTarget && !_ball.BindingSuspended && _gate.CanClarify(_contextVersion))
        {
            string request;
            try { request = _gate.TakeClarification(_contextVersion, text); }
            catch (InvalidDataException) { _panel.SetStatus("请用一行文字说明你想了解什么（最多300字）。"); return; }
            _boundQuestionReady = false;
            await RecognizeAsync(_ball.BoundTarget, request, null, budget: budget);
            return;
        }
        // Direct result input uses the same guarded transition as the existing continue action.
        if (!_preview && _followup.CanOffer) OnFollowupRequested();
        if (!_preview && _followup.WaitingForUser && _gate.Accepts(_followupVersion))
        {
            GuideFollowupRequest next;
            try { next = _followup.Take(text); }
            catch (InvalidDataException error)
            {
                if (error.Message == "sensitive_content")
                {
                    var target = _followup.Target!;
                    ShowRecognitionFailure(target.Window, target.ProcessId, "sensitive_input", FailureMessage(error));
                }
                else { _panel.SetStatus("请用一行文字说明本轮问题（最多300字）。"); _ball.ShowQuestion(); }
                return;
            }
            _panel.ClearQuestion(); _ball.ClearQuestion();
            await RecognizeAsync(next.Target.Window, next.Request, null, followup: next, budget: budget);
            return;
        }
        if (_preview || _context is not { } previous || !_gate.CanClarify(_contextVersion)) return;
        string intent;
        try { intent = _gate.TakeClarification(_contextVersion, text); }
        catch (InvalidDataException)
        {
            _panel.SetStatus("请用一行文字说明你想处理的事（最多300字）。");
            _ball.ShowQuestion();
            return;
        }
        _panel.ClearQuestion();
        _ball.ClearQuestion();
        // Submission returns to this target through the same bounded activation path as a drop.
        var followup = _activeFollowup is { } active ? active with { Clarification = intent } : null;
        if (followup is not null) intent = followup.ScopeIntent;
        await RecognizeAsync(previous.Window, intent, previous, previous.Scope?.UserSelected == true ? previous.Scope.Region : null, followup, budget);
    }

    void OnFollowupRequested()
    {
        if (_preview || _plugins.GeneralProvider?.InterpretationPolicy?.UserLedFollowup != true ||
            _context is null || !_gate.Accepts(_contextVersion) || !_followup.CanOffer || _picker is not null) return;
        _followup.Begin();
        _request?.Cancel(); _followupVersion = _gate.Begin();
        _context = null; _contextCheckPending = false; _pendingIntent = null; _activeFollowup = null;
        _marker.HideAndReset(); _panel.SetControl(null); _panel.SetRegionAvailable(false); _panel.ClearQuestion();
        _panel.SetStatus("可先在这个窗口手动操作，再输入本轮问题。提交后才重新读取；切到别处时暂停，回来后可重新提问。");
        _ball.ShowQuestion();
        ProbeLog.Log("guide_followup_armed", ("step", _followup.Step + 1));
    }

    async void OnSelected(IntPtr window) => await RecognizeAsync(window, null, null);

    void OnRegionRequested()
    {
        if (_preview || !_gate.CloudEnabled || _context is not { } previous || !_gate.Accepts(_contextVersion) || _picker is not null) return;
        var version = _contextVersion;
        var intent = _pendingIntent;
        var followup = _activeFollowup;
        _marker.HideAndReset();
        _picker = new RegionPicker(previous.Frame, _ball.Scale);
        var picker = _picker;
        picker.Selected += async region =>
        {
            if (!ReferenceEquals(_picker, picker)) return;
            if (region is null || !_gate.Accepts(version) || !_gate.CloudEnabled)
            { ReadyBoundQuestion("已取消框选。你想了解什么？"); return; }
            // Admit the owned picker until the existing activation path resumes the bound target.
            picker.Hide();
            var processing = RecognizeAsync(previous.Window, intent, previous, region, followup);
            _picker = null;
            picker.Abort();
            await processing;
        };
        ProbeLog.Log("guide_selection_start");
        picker.Show();
    }

    async Task RecognizeAsync(IntPtr window, string? intent, GuideObservation? previous, Rect? selection = null,
        GuideFollowupRequest? followup = null, TimeSpan? budget = null)
    {
        if (_preview) return;
        if (!_gate.CloudEnabled)
        {
            _ball.StopTracking("consent_required");
            _panel.SetStatus("请重新运行鲸汐安装程序确认使用协议；尚未读取窗口内容或发送请求。");
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var request = new CancellationTokenSource();
        _request = request;
        var requestToken = request.Token;
        using var deadline = new GuideDeadline(budget ?? _options.Timeout, requestToken);
        var token = deadline.Token;
        var version = _gate.Begin();
        _boundQuestionReady = false;
        _context = null;
        _pendingIntent = null;
        _activeFollowup = followup;
        _followup.SuspendOffer();
        _panel.SetRegionAvailable(false);
        _panel.ClearQuestion();
        _ball.ClearQuestion();
        var total = Stopwatch.StartNew();
        var stage = ProcessingStage.Activation;
        var processId = Native.ProcessId(window);
        string? identifiedProcess = null;
        string? CurrentOwnership() => stage == ProcessingStage.Model && identifiedProcess is not null &&
            _contextVersion == version && _context is { } context &&
            GuideObservation.IsCurrent(window, context.ProcessId, context.Frame) ? identifiedProcess : null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) || Native.IsIconic(window))
                throw new InvalidDataException("target_unavailable");
            using var targetProcess = Process.GetProcessById((int)Native.ProcessId(window));
            using var targetOwner = Native.PinProcess((uint)targetProcess.Id);
            var selectedStart = targetProcess.StartTime.ToUniversalTime().Ticks;
            var plugin = _plugins.Resolve(targetProcess.ProcessName, Native.Title(window));
            if (previous is not null && previous.PluginId != plugin.Id)
                throw new InvalidDataException("target_changed");
            var flowTarget = new GuideFlowTarget(window, (uint)targetProcess.Id, selectedStart, plugin.Id);
            followup?.ValidateTarget(flowTarget);
            if (!Native.IsNonElevated(window)) throw new InvalidDataException("elevated_target");
            var activated = await ActivateSelectedAsync(window, version, token);
            if (!_gate.Accepts(version)) return;
            if (!activated) throw new InvalidDataException("target_activation_failed");
            if (intent is null && previous is null && selection is null && followup is null)
                _initialCollectionVersion = version;
            _ball.ConfirmBinding();
            if (!_gate.Accepts(version)) return;
            if (previous is null && followup is null) _ball.BeginDock();
            _panel.SetControl(null);
            _panel.BeginProcessing();
            _ball.SetProcessing(true);
            _panel.Reposition();
            stage = ProcessingStage.SourceValidation;
            if (previous is null && intent is not null && followup is null)
            {
                // A resumed question needs only a fresh identity/geometry anchor before scoped capture.
                // Do not make unknown applications pass a full accessibility traversal first.
                previous = new GuideObservation(window, flowTarget.ProcessId, GuideObservation.WindowRect(window),
                    [], null, plugin.UsesRawView, plugin.Id, Snapshot: true);
            }
            else if (previous is null && followup is not null)
            {
                // An armed step may follow manual changes; obtain a fresh local baseline, never the prior result's data.
                _contextCheckPending = false;
                previous = await deadline.RunAsync(t => ObservationWorker.ReadAsync(window, false, plugin, t));
                if (!_gate.Accepts(version)) return;
                if (previous.ProcessId != flowTarget.ProcessId || !Native.ProcessAlive(targetOwner))
                    throw new InvalidDataException("target_changed");
            }
            else if (previous is not null)
                previous.ValidateContext(await ReadContextAsync(previous, token), null);
            stage = ProcessingStage.Collection;
            var observation = await deadline.RunAsync(t => ObservationWorker.ReadAsync(window, _options.IncludeImage, plugin, t, intent, selection,
                intent is null && selection is null ? _plugins.IdentityProvider : null));
            if (!_gate.Accepts(version)) return;
            stage = ProcessingStage.TargetValidation;
            _ball.UpdateBoundPresence();
            if (!_gate.Accepts(version)) return;
            previous?.ValidateTargetContext(observation);
            if (intent is null)
            {
                // The PID has survived collection/privacy checks; retain ownership locally only.
                identifiedProcess = GuideIdentification.BindProcess(targetProcess.Id, observation.ProcessId, targetProcess.ProcessName);
                if (observation.Identity is { } identity && identity.StartedAt != selectedStart)
                    throw new InvalidDataException("target_changed");
            }
            _context = observation with { Png = null };
            _contextVersion = version;
            _pendingIntent = intent;
            if (_contextCheckPending) OnTargetChanged();
            var observeMs = total.ElapsedMilliseconds;

            stage = ProcessingStage.Payload;
            var payload = GuidePayload.From(observation);
            observation = observation with { Png = null };
            ProbeLog.Log("guide_request_start", ("observeMs", observeMs),
                ("candidates", payload.Candidates.Count), ("image", _options.IncludeImage),
                ("localTextRegions", observation.Candidates.Count(c => c.ControlType == GuideTextRegions.ControlType)),
                ("sentTextRegions", payload.Candidates.Count(c => c.ControlType == GuideTextRegions.ControlType)),
                ("scoped", observation.Scope is not null), ("imageBytes", payload.Png?.Length ?? 0));
            var task = intent is null ? GuideObservation.TaskDescription :
                "用户明确提出的目标：" + intent + "。只解释和指路，不执行任何操作；目标已满足时说明现状，不指向相反动作。";
            if (followup is not null) task = followup.ModelTask;
            if (!_gate.Accepts(version)) return;
            stage = ProcessingStage.Model;
            _initialCollectionVersion = null;
            _modelVersion = version;
            GuideIdentification? identification = null;
            GuideAnswer modelAnswer;
            var policy = _plugins.GeneralProvider?.InterpretationPolicy;
            try
            {
                if (intent is null)
                {
                    identification = await deadline.RunAsync(t => _model.IdentifyAsync(payload.Candidates, payload.Png, t, payload.VisibleContext, policy));
                    modelAnswer = identification.ToAnswer(identifiedProcess!, observation.Identity);
                }
                else
                    modelAnswer = await deadline.RunAsync(t => _model.AskAsync(task, payload.Candidates, payload.Png, t, payload.VisibleContext,
                        policy));
            }
            finally { payload = payload with { Png = null }; }
            var answer = identification?.NextCheck is not null ? modelAnswer :
                modelAnswer.ForIntent(intent, allowQuestion: followup is { Clarification: null });
            var modelMs = total.ElapsedMilliseconds - observeMs;
            ProbeLog.Log("guide_response_received", ("modelMs", modelMs), ("mode", answer.Mode),
                ("clarificationLimited", intent is not null && modelAnswer.Mode == "ask" && answer.Mode != "ask"));
            var remaining = _options.Timeout - total.Elapsed;
            var deferred = await WaitForPresentationAsync(version, requestToken);
            if (deferred is null) return;
            _modelVersion = null;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
            using var resumedDeadline = deferred.Value ? new GuideDeadline(remaining, requestToken) : null;
            var resultToken = resumedDeadline?.Token ?? token;
            if (!Native.ProcessAlive(targetOwner)) throw new InvalidDataException("target_changed");
            stage = ProcessingStage.ResultValidation;
            if (!GuideObservation.IsCurrent(window, observation.ProcessId, observation.Frame))
                throw new InvalidDataException("target_changed");
            var bounds = observation.ValidateContext(await ReadContextAsync(observation, resultToken), answer.CandidateId);
            if (answer.CandidateId is { } candidateId &&
                !plugin.AllowsPoint(intent, observation.Candidates.Single(c => c.Id == candidateId).Name))
                throw new InvalidDataException("stale_candidate");
            resultToken.ThrowIfCancellationRequested();
            if (!_gate.Accepts(version)) return;
            if (!GuideObservation.IsCurrent(window, observation.ProcessId, observation.Frame))
                throw new InvalidDataException("target_changed");
            _marker.HideAndReset();
            if (bounds is { } rect)
            {
                if (_marker.PlaceControl(rect, observation.Frame, _ball.Scale)) _marker.Show();
            }
            _panel.SetControl(bounds);
            _ball.SetProcessing(false);
            var textRegion = answer.CandidateId is { } selected &&
                observation.Candidates.Single(c => c.Id == selected).ControlType == GuideTextRegions.ControlType;
            _panel.SetStatus(textRegion ? "这里标出的是文字所在区域，不是按钮的点击范围。\n" + answer.Explanation : answer.Explanation);
            _panel.SetPoint(bounds is not null && _marker.IsVisible);
            if (intent is not null && _plugins.ChatProvider is not null)
                _questions.Remember(followup?.Clarification ?? intent, answer.Explanation[..Math.Min(answer.Explanation.Length, 1200)]);
            _ball.SetResult();
            _panel.SetRegionAvailable(identification is { Page: null });
            if (answer.Mode == "ask")
            {
                _gate.AwaitClarification(version);
                _ball.ShowQuestion();
            }
            else if (policy?.UserLedFollowup == true)
            {
                _followup.Complete(flowTarget, intent, allowWithoutGoal: identification?.CanStartErrorFollowup(policy) == true);
                _panel.SetFollowupAvailable(_followup.CanOffer);
                _ball.SetResult(_followup.CanOffer);
                _activeFollowup = null;
            }
            resultToken.ThrowIfCancellationRequested();
            ProbeLog.Log("guide_result", ("mode", answer.Mode), ("candidates", observation.Candidates.Count),
                ("observeMs", observeMs), ("modelMs", modelMs), ("totalMs", total.ElapsedMilliseconds),
                ("withinThreeSeconds", total.ElapsedMilliseconds <= 3000), ("image", _options.IncludeImage));
        }
        catch (OperationCanceledException)
        {
            var cancelled = request.IsCancellationRequested;
            if (await WaitForPresentationAsync(version, requestToken) is not null)
            {
                var ownership = CurrentOwnership();
                var localIdentity = ownership is null ? null : _context?.Identity;
                var message = TimeoutMessage(stage == ProcessingStage.Model);
                ShowRecognitionFailure(window, processId, "processing_timeout",
                    ownership is not null ? GuideIdentification.Unavailable(ownership, message, localIdentity) : message);
            }
            ProbeLog.Log("guide_failure", ("category", cancelled ? "cancelled" : "timeout"), ("stage", stage.ToString()));
        }
        catch (Exception ex)
        {
            if (await WaitForPresentationAsync(version, requestToken) is null) return;
            if (ex is InvalidDataException { Message: "scope_unavailable" } && previous is not null && _gate.Accepts(version))
            {
                _context = previous with { Png = null };
                _contextVersion = version;
                _pendingIntent = intent;
                _marker.HideAndReset();
                _panel.SetRegionAvailable(true);
                _panel.SetStatus("请帮我圈一下你想了解的地方。\n点击“选择识别区域”，把相关文字或按钮框进去即可，不用重新回答。圈好前不会发送这一步的新内容。");
                ProbeLog.Log("guide_selection_required");
                return;
            }
            if (ex is InvalidDataException changed && changed.Message == "target_changed")
            {
                if (_gate.Accepts(version)) ReadyBoundQuestion("窗口已有变化，本次识别已停止。你想了解什么？");
                return;
            }
            if (_gate.Accepts(version))
            {
                var ownership = CurrentOwnership();
                var localIdentity = ownership is null ? null : _context?.Identity;
                ShowRecognitionFailure(window, processId, "recognition_failed", ownership is not null &&
                    ex is not InvalidDataException { Message: "sensitive_content" }
                    ? GuideIdentification.Unavailable(ownership, FailureMessage(ex), localIdentity) : FailureMessage(ex));
            }
            // Exception messages can contain provider content or URLs; log categories only.
            ProbeLog.Log("guide_failure", ("category", ex is InvalidDataException ? "validation" :
                ex is JsonException ? "invalid_json" : ex is HttpRequestException ? "network" : ex.GetType().Name),
                ("stage", stage.ToString()), ("code", ex is InvalidDataException ? ObservationWorker.SafeError(ex.Message) : "none"));
        }
        finally
        {
            if (_initialCollectionVersion == version) _initialCollectionVersion = null;
            if (_modelVersion == version) _modelVersion = null;
            if (_gate.Accepts(version)) _ball.SetProcessing(false);
            // Keep the active token available for event-driven revalidation after displaying the answer.
            if (!ReferenceEquals(_request, request)) request.Dispose();
        }
    }

    async Task<bool> ActivateSelectedAsync(IntPtr window, int version, CancellationToken token)
    {
        if (Native.GetForegroundWindow() == window) return true;
        // Only an explicit drop or answer may reactivate its selected target once.
        _activationVersion = version;
        _activationSource = Native.GetForegroundWindow();
        try
        {
            var confirmed = await ActivateOnceAsync(window, () =>
            {
                var accepted = Native.SetForegroundWindow(window);
                ProbeLog.Log("guide_target_activation", ("target", Native.Hex(window)),
                    ("fg", Native.Hex(Native.GetForegroundWindow())), ("accepted", accepted));
            }, Native.GetForegroundWindow, token);
            if (!_gate.Accepts(version)) return false;
            ProbeLog.Log("guide_target_activation_complete", ("target", Native.Hex(window)),
                ("fg", Native.Hex(Native.GetForegroundWindow())), ("confirmed", confirmed));
            return confirmed;
        }
        finally { if (_activationVersion == version) _activationVersion = null; }
    }

    internal static async Task<bool> ActivateOnceAsync(IntPtr target, Action activate,
        Func<IntPtr> getForeground, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (getForeground() == target) return true;
        activate();
        return await WaitForForegroundAsync(target, getForeground, token);
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
        InvalidDataException { Message: "capture_protected" } =>
            "这个窗口不允许获取画面，本次没有识别。\n你可以用文字描述想了解的提示。",
        InvalidDataException { Message: "target_changed" } =>
            "窗口内容或位置发生了变化，本次识别已停止。\n请把鲸鱼重新拖到要了解的窗口。",
        InvalidDataException { Message: "target_context_unavailable" } =>
            "这次没能核对窗口是否保持原样，已停止识别。\n请保持窗口可见，稍后再把鲸鱼拖进去。",
        InvalidDataException { Message: "scope_unavailable" } =>
            "还不知道该看哪一部分。\n请重新把鲸鱼拖入窗口，并说明想了解什么；范围确定前不会发送这一步的新内容，也不会改发整个窗口。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } =>
            "识别服务没有允许这次请求。\n请在“鲸汐设置 → 连接”检查服务是否可用；这不代表你选择的窗口有问题。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } =>
            "识别服务暂时无法接受更多请求。\n可能是请求过多或可用额度不足，请稍后再试或查看服务账户。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } =>
            "识别服务提示可用额度不足。\n请检查所用服务的账户额度；恢复后可以直接再问，不用重启鲸汐。",
        HttpRequestException { StatusCode: not null } =>
            "识别服务这次未能完成请求。\n请稍后再试；这不代表你选择的窗口有问题。",
        HttpRequestException => "暂时连接不上识别服务。\n请稍后再试，或在“鲸汐设置”里查看连接状态。",
        JsonException => "识别服务返回的内容暂时无法使用。\n这次不猜测窗口内容，也不标出操作位置，请稍后再试。",
        InvalidDataException data when data.Message.StartsWith("identification_", StringComparison.Ordinal) ||
            data.Message is "model_response_too_large" or "model_response_invalid" or "model_incomplete" or "model_refusal" =>
            "识别服务返回的内容暂时无法使用。\n这次不猜测窗口内容，也不标出操作位置，请稍后再试。",
        InvalidDataException { Message: "worker_failed" or "worker_reply" or "worker_reply_too_large" or "worker_request_invalid" } =>
            "鲸汐这次没能完成窗口读取。\n请保持要了解的窗口可见，稍后再拖一次；这不代表那个软件出了故障。",
        InvalidDataException { Message: "target_unavailable" } =>
            "刚才选择的窗口已经无法查看。\n请重新打开或恢复那个窗口，再把鲸鱼拖进去。",
        InvalidDataException { Message: "target_not_foreground" } =>
            "请先点一下要了解的窗口，再把鲸鱼拖进去。\n这次先停止，避免看错窗口。",
        InvalidDataException { Message: "target_activation_failed" } =>
            "没能切换到你选择的窗口。\n请先点一下那个窗口，再把鲸鱼拖进去。",
        InvalidDataException { Message: "elevated_target" } =>
            "这个窗口暂时不能查看。\n它可能受到系统保护，鲸汐不会绕过保护。请换一个普通窗口继续使用。",
        InvalidDataException { Message: "capture_target_unavailable" or "capture_dimensions" or "capture_unusable" } =>
            "这次没看清窗口里的画面。\n请先把窗口打开并保持可见，再拖一次鲸鱼；不会改为查看整个屏幕。",
        InvalidDataException { Message: "unsupported_target" } =>
            "暂时还不能解释这个窗口。\n你可以在“鲸汐设置 → 功能”里确认“通用界面理解”是否开启。",
        InvalidDataException { Message: "plugin_conflict" } =>
            "鲸汐的功能设置有冲突，这次先不查看。\n请到“鲸汐设置 → 功能”关闭重复的功能后再试。",
        InvalidDataException { Message: "sensitive_content" } =>
            "这里可能有密码或其他私密信息，鲸汐已停止识别。\n请先隐藏这些内容，再拖一次鲸鱼。",
        InvalidDataException { Message: "privacy_unavailable" } =>
            "还没完成隐私检查，这次先不识别。\n这不代表发现了私密信息。请稍后再试；仍然失败时，请反馈这段提示。",
        _ => "鲸汐这次没能完成识别，暂时不能给出可靠的说明。\n请稍后再试；这不代表你选择的软件出了故障，这次没有标出操作位置。"
    };

    internal static string TimeoutMessage(bool waitingForService) => waitingForService
        ? "识别服务这次没有及时返回结果。\n请稍后再试；这不代表你选择的软件出了故障，这次没有标出操作位置。"
        : "鲸汐这次读取和核对窗口用时过长，已停止处理。\n请保持窗口可见，稍后再试；这次没有标出操作位置。";

    async Task<GuideObservation> ReadContextAsync(GuideObservation previous, CancellationToken token)
    {
        if (!GuideObservation.IsCurrent(previous.Window, previous.ProcessId, previous.Frame))
            throw new InvalidDataException("target_changed");
        if (previous.Identity is { } identity)
        {
            using var process = Process.GetProcessById((int)previous.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartedAt) throw new InvalidDataException("target_changed");
        }
        token.ThrowIfCancellationRequested();
        if (previous.Snapshot)
        {
            if (previous.PageAnchors is null) return previous;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMilliseconds(1200));
            try
            {
                var page = await ObservationWorker.ReadAsync(previous.Window, false, _plugins.Get(previous.PluginId),
                    budget.Token, pageCheck: true);
                previous.ValidateTargetContext(page);
                return previous with { PageAnchors = page.PageAnchors };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return previous; }
            catch (InvalidDataException ex) when (ex.Message is "worker_failed" or "privacy_unavailable") { return previous; }
        }
        GuideObservation current;
        try { current = await ObservationWorker.ReadAsync(previous.Window, previous.VisualHash is not null, _plugins.Get(previous.PluginId), token,
            previous.ScopeIntent, previous.Scope?.UserSelected == true ? previous.Scope.Region : null); }
        catch (InvalidDataException ex) when (ex.Message == "scope_unavailable")
        { throw new InvalidDataException("target_changed"); }
        if (current.ProcessId != previous.ProcessId || current.Frame != previous.Frame)
            throw new InvalidDataException("target_changed");
        return current;
    }

    public void Dispose()
    {
        CloseChat();
        _questions.Reset();
        _ball.HomeClicked -= ToggleChat;
        _ball.FullscreenHidden -= OnFullscreenHidden;
        _ball.LocationChanged -= OnEntryChanged;
        _ball.SizeChanged -= OnEntryChanged;
        _ball.TargetSelected -= OnSelected;
        _ball.TrackingStopped -= Cancel;
        _ball.TargetMoved -= OnTargetChanged;
        _ball.BindingSuspendedChanged -= OnBindingSuspended;
        _ball.BindingResumed -= OnBindingResumed;
        _ball.GuidanceCleared -= OnGuidanceCleared;
        _ball.MenuRequested -= OnMenuRequested;
        _panel.MenuRequested -= OnMenuRequested;
        _ball.AllowsForeground = null;
        _panel.AnswerSubmitted -= OnAnswerSubmitted;
        _ball.AnswerSubmitted -= OnAnswerSubmitted;
        _panel.RegionRequested -= OnRegionRequested;
        _panel.FollowupRequested -= OnFollowupRequested;
        _activeFollowup = null;
        _followup.Clear();
        _picker?.Abort();
        _picker = null;
        _panel.EndSession();
        _request?.Cancel();
        _request?.Dispose();
        _gate.SetCloud(false);
        _menu.Dispose();
        _model.Dispose();
        if (_marker.IsLoaded) _marker.Close();
        _panel.Close();
    }
}
