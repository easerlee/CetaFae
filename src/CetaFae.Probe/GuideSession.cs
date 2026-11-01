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
    readonly CloudConsentStore _consent;
    readonly GuideMenu _menu;
    readonly PluginCatalog _plugins;
    readonly SoftwareReference _references = new();
    CancellationTokenSource? _referenceRequest;
    CancellationTokenSource? _request;
    GuideObservation? _context;
    int _contextVersion;
    int? _activationVersion;
    IntPtr _activationSource;
    bool _checkingContext;
    bool _contextCheckPending;
    RegionPicker? _picker;
    string? _pendingIntent;
    readonly bool _preview;

    public GuideSession(BallWindow ball, GuideOptions options, bool preview, PluginCatalog plugins,
        Action? settings = null, bool restoreConsent = true)
    {
        _ball = ball;
        _options = options;
        _plugins = plugins;
        _model = new GuideModel(options);
        _panel = new GuidePanel(options);
        _panel.BindEntry(() => _ball.FeedbackBounds, () => _ball.Scale, () => _preview || _ball.HasTarget);
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
        _panel.ConsentAccepted += AcceptConsent;
        _panel.MenuRequested += OnMenuRequested;
        _panel.Cancelled += () => _ball.StopTracking("cancelled");
        _panel.AnswerSubmitted += OnAnswerSubmitted;
        _ball.AnswerSubmitted += OnAnswerSubmitted;
        _panel.RegionRequested += OnRegionRequested;
        _panel.ReferenceRequested += OnReferenceRequested;
        _panel.FollowupRequested += OnFollowupRequested;
        _ball.AllowsForeground = IsAllowedForeground;
        _ball.TargetSelected += OnSelected;
        _ball.TrackingStopped += Cancel;
        _ball.TargetMoved += OnTargetChanged;
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

    void OnFullscreenHidden() => _panel.DismissNotice();

    internal bool CloudEnabled => _gate.CloudEnabled;
    internal void EndForSettings() => _ball.StopTracking("settings_opened");
    internal void RevokeForConfiguration()
    {
        _ball.StopTracking("configuration_changed");
        _gate.SetCloud(false);
        _panel.SetCloud(false); _menu.SetCloud(false);
    }
    internal void ManageConsent() { if (_gate.CloudEnabled) RevokeConsent(); else ShowConsent(); }
    internal void ShowConfigurationWarning() => _panel.SetStatus(
        "本机设置无法读取，云端保持关闭。请从鲸鱼右键或托盘打开鲸汐设置，核对并保存后亲自授权。");

    internal void ShowConsent()
    {
        _panel.ShowConsent();
    }

    void AcceptConsent()
    {
        if (_preview) { _panel.SetStatus("这是静态预览，不会确认使用或发送请求。"); return; }
        if (_gate.CloudEnabled) return;
        if (!_consent.Save(true))
        { _panel.SetStatus("无法保存使用同意，尚未开始。请检查本机用户配置目录权限后重试。"); return; }
        _gate.SetCloud(true); _panel.SetCloud(true); _menu.SetCloud(true);
        _ball.StopTracking("consent_accepted");
        _panel.SetStatus("已准备好。以后直接拖动小鲸鱼到要了解的窗口即可；无需再次开启。设置的隐私页可撤回同意。");
        ProbeLog.Log("guide_consent", ("enabled", true));
    }

    void RevokeConsent()
    {
        if (_preview) { _panel.SetStatus("这是静态预览，不会更改使用同意。"); return; }
        _ball.StopTracking("consent_withdrawn");
        _gate.SetCloud(false); _panel.SetCloud(false); _menu.SetCloud(false);
        var saved = _consent.Revoke();
        _panel.SetStatus("已撤回同意，不再读取窗口或发起识别请求。已发送的内容无法从上游撤回。" +
            (saved ? "再次使用请到设置隐私页重新确认。" : "撤回未能保存；本次运行已停止识别，下次启动前请修复配置目录权限，避免旧授权恢复。"));
        ProbeLog.Log("guide_consent", ("enabled", false), ("saved", saved));
    }

    void Cancel(string reason)
    {
        _referenceRequest?.Cancel();
        _panel.SetReferenceQuery(null);
        _activationVersion = null;
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
        ProbeLog.Log("guide_cancel", ("reason", reason));
    }

    async void OnReferenceRequested(string query)
    {
        if (_preview || !_gate.CloudEnabled || _context is not { } context || !_gate.Accepts(_contextVersion) ||
            context.Identity?.ReferenceQuery != query || _plugins.IdentityProvider is null) return;
        var version = _contextVersion;
        _referenceRequest?.Cancel();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _referenceRequest = request;
        var token = request.Token;
        var validating = true;
        bool Current() => _gate.Accepts(version) && ReferenceEquals(_context, context) &&
            GuideObservation.IsCurrent(context.Window, context.ProcessId, context.Frame);
        try
        {
            // A deliberately focused answer box may own focus when the user confirms lookup.
            if (IsInputForeground(Native.GetForegroundWindow()))
            {
                if (!Native.IsWindow(context.Window) || Native.ProcessId(context.Window) != context.ProcessId ||
                    GuideObservation.WindowRect(context.Window) != context.Frame || !Native.IsNonElevated(context.Window))
                    throw new InvalidDataException("target_changed");
                var activated = await ActivateSelectedAsync(context.Window, version, token);
                if (!_gate.Accepts(version)) return;
                if (!activated) throw new InvalidDataException("target_activation_failed");
            }
            context.ValidateContext(await ReadContextAsync(context, token), null);
            token.ThrowIfCancellationRequested();
            if (!Current()) return;
            validating = false;
            _panel.SetReferenceResult("正在查询公共百科资料；本机信息和画面判断保留。\n资料仅为候选，不代表本机身份认证。");
            var result = await _references.FindAsync(query, token);
            validating = true;
            context.ValidateContext(await ReadContextAsync(context, token), null);
            token.ThrowIfCancellationRequested();
            if (Current()) _panel.SetReferenceResult(result);
        }
        catch (InvalidDataException ex) when (ex.Message is "target_changed" or "sensitive_content" or "privacy_unavailable")
        { if (_gate.Accepts(version)) _ball.StopTracking("target_changed"); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidDataException or JsonException)
        {
            if (_gate.Accepts(version) && validating) _ball.StopTracking("target_context_unavailable");
            else if (Current()) _panel.SetReferenceResult("公共资料查询未完成；本机信息与画面判断保留。可重新拖入后再试，不会自动重发。");
        }
        catch (Exception)
        { if (_gate.Accepts(version)) _ball.StopTracking("reference_failed"); }
        finally { if (ReferenceEquals(_referenceRequest, request)) _referenceRequest = null; }
    }

    async void OnTargetChanged()
    {
        _contextCheckPending = true;
        if (_followup.WaitingForUser && _followup.Target is { } armed)
        {
            // Geometry/lifetime checks are allowed while armed, but do not inspect changed content.
            if (!Native.IsWindow(armed.Window) || !Native.IsWindowVisible(armed.Window) || Native.IsIconic(armed.Window) ||
                Native.ProcessId(armed.Window) != armed.ProcessId) _ball.StopTracking("target_changed");
            _contextCheckPending = false;
            return;
        }
        if (_picker is not null && _context is { } selecting)
        {
            if (!Native.IsWindow(selecting.Window) || !Native.IsWindowVisible(selecting.Window) ||
                Native.IsIconic(selecting.Window) || Native.ProcessId(selecting.Window) != selecting.ProcessId ||
                GuideObservation.WindowRect(selecting.Window) != selecting.Frame)
                _ball.StopTracking("target_changed");
            return;
        }
        if (_checkingContext || _context is null || IsInputForeground(Native.GetForegroundWindow())) return;
        _checkingContext = true;
        try
        {
            while (_contextCheckPending && _context is { } context && _gate.Accepts(_contextVersion))
            {
                if (_picker is not null || IsInputForeground(Native.GetForegroundWindow())) break;
                _contextCheckPending = false;
                var version = _contextVersion;
                var token = _request!.Token;
                try
                {
                    using var deadline = new GuideDeadline(_options.Timeout, token);
                    var current = await ReadContextAsync(context, deadline.Token);
                    // A picker/input may have opened while the native worker was reading.
                    // Defer that result; resuming recognition performs the bound revalidation.
                    if (_picker is not null || IsInputForeground(Native.GetForegroundWindow()))
                    { _contextCheckPending = true; break; }
                    if (_gate.Accepts(version) && !GuideObservation.HasSameContext(context.Candidates, current.Candidates))
                        GuideObservation.LogContextDifference(context.Candidates, current.Candidates);
                    if (_gate.CancelIfContextChanged(version, context.Candidates, current.Candidates) ||
                        _gate.Accepts(version) && (!context.HasSameScope(current) || !context.HasSameVisual(current)))
                        _ball.StopTracking("target_changed");
                    else if (_gate.Accepts(version)) ProbeLog.Log("guide_context_unchanged");
                }
                catch (OperationCanceledException) when (!_gate.Accepts(version)) { }
                catch (Exception)
                {
                    if (_gate.Accepts(version) && _picker is null && !IsInputForeground(Native.GetForegroundWindow()))
                        _ball.StopTracking("target_context_unavailable");
                }
            }
        }
        finally { _checkingContext = false; }
    }

    void OnEntryChanged(object? sender, EventArgs args)
    {
        if (_panel.IsVisible) _panel.Reposition();
    }

    bool IsInputForeground(IntPtr window) =>
        (window == _panel.Handle && _panel.IsSessionInteraction || window == _ball.Handle && _ball.IsSessionInteraction) &&
        (_context is not null && _gate.Accepts(_contextVersion) || _followup.WaitingForUser && _gate.Accepts(_followupVersion));

    bool IsAllowedForeground(IntPtr window) => IsInputForeground(window) ||
        _menu.OwnsForeground(window) ||
        _picker is not null && (window == _picker.Handle || window == IntPtr.Zero) ||
        _activationVersion is { } version && _gate.Accepts(version) &&
        (window == IntPtr.Zero || window == _activationSource);

    async void OnAnswerSubmitted(string text)
    {
        if (!_preview && _followup.WaitingForUser && _gate.Accepts(_followupVersion))
        {
            GuideFollowupRequest next;
            try { next = _followup.Take(text); }
            catch (InvalidDataException error)
            {
                if (error.Message == "sensitive_content")
                { _ball.StopTracking("sensitive_input"); _panel.SetStatus(FailureMessage(error)); }
                else { _panel.SetStatus("请用一行文字说明本轮问题（最多300字）。"); _ball.ShowQuestion(); }
                return;
            }
            _panel.ClearQuestion(); _ball.ClearQuestion();
            await RecognizeAsync(next.Target.Window, next.Request, null, followup: next);
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
        await RecognizeAsync(previous.Window, intent, previous, previous.Scope?.UserSelected == true ? previous.Scope.Region : null, followup);
    }

    void OnFollowupRequested()
    {
        if (_preview || _plugins.GeneralProvider?.InterpretationPolicy?.UserLedFollowup != true ||
            _context is null || !_gate.Accepts(_contextVersion) || !_followup.CanOffer || _picker is not null) return;
        _followup.Begin();
        _referenceRequest?.Cancel(); _panel.SetReferenceQuery(null); _panel.SetReferenceResult("");
        _request?.Cancel(); _followupVersion = _gate.Begin();
        _context = null; _contextCheckPending = false; _pendingIntent = null; _activeFollowup = null;
        _marker.HideAndReset(); _panel.SetControl(null); _panel.SetRegionAvailable(false); _panel.ClearQuestion();
        _panel.SetStatus("继续提问已准备。可先在当前窗口手动操作，再在上方输入本轮问题（例如“下一步看哪里”）。提交后才重新读取，切换到其他窗口会结束本次指导。");
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
            { _ball.StopTracking("selection_cancelled"); return; }
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
        GuideFollowupRequest? followup = null)
    {
        if (_preview) return;
        _referenceRequest?.Cancel();
        _panel.SetReferenceQuery(null);
        _panel.SetReferenceResult("");
        if (!_gate.CloudEnabled)
        {
            _ball.StopTracking("consent_required");
            _panel.SetStatus("请先在鲸汐设置的隐私页阅读说明并开始使用；尚未读取窗口内容或发送请求。");
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
        _pendingIntent = null;
        _activeFollowup = followup;
        _followup.SuspendOffer();
        _panel.SetRegionAvailable(false);
        _panel.ClearQuestion();
        _ball.ClearQuestion();
        var total = Stopwatch.StartNew();
        var stage = ProcessingStage.Activation;
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
            var selectedStart = targetProcess.StartTime.ToUniversalTime().Ticks;
            var plugin = _plugins.Resolve(targetProcess.ProcessName, Native.Title(window));
            if (previous is not null && previous.PluginId != plugin.Id)
                throw new InvalidDataException("target_changed");
            var flowTarget = new GuideFlowTarget(window, (uint)targetProcess.Id, selectedStart, plugin.Id);
            followup?.ValidateTarget(flowTarget);
            if (!Native.IsNonElevated(window)) throw new InvalidDataException("elevated_target");
            if (previous is null && followup is null) _ball.BeginDock();
            _panel.SetControl(null);
            _panel.SetStatus("正在读取所选窗口…");
            var activated = await ActivateSelectedAsync(window, version, token);
            if (!_gate.Accepts(version)) return;
            if (!activated) throw new InvalidDataException("target_activation_failed");
            _panel.Reposition();
            stage = ProcessingStage.SourceValidation;
            if (previous is null && followup is not null)
            {
                // An armed step may follow manual changes; obtain a fresh local baseline, never the prior result's data.
                _contextCheckPending = false;
                previous = await deadline.RunAsync(t => ObservationWorker.ReadAsync(window, false, plugin, t));
                if (!_gate.Accepts(version)) return;
                if (previous.ProcessId != flowTarget.ProcessId || targetProcess.HasExited)
                    throw new InvalidDataException("target_changed");
            }
            else if (previous is not null)
                previous.ValidateContext(await ReadContextAsync(previous, token), null);
            stage = ProcessingStage.Collection;
            var observation = await deadline.RunAsync(t => ObservationWorker.ReadAsync(window, _options.IncludeImage, plugin, t, intent, selection,
                intent is null && selection is null ? _plugins.IdentityProvider : null));
            if (!_gate.Accepts(version)) return;
            stage = ProcessingStage.TargetValidation;
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
            _panel.SetStatus(identifiedProcess is null ? "正在识别下一步…" :
                GuideIdentification.Ownership(identifiedProcess) + (observation.Identity is null ? "" : "\n" + observation.Identity.Describe()) +
                "\n正在识别软件和当前界面…");
            stage = ProcessingStage.Payload;
            var payload = GuidePayload.From(observation);
            ProbeLog.Log("guide_request_start", ("observeMs", observeMs),
                ("candidates", payload.Candidates.Count), ("image", _options.IncludeImage),
                ("scoped", observation.Scope is not null), ("imageBytes", payload.Png?.Length ?? 0));
            var task = intent is null ? GuideObservation.TaskDescription :
                "用户明确提出的目标：" + intent + "。只解释和指路，不执行任何操作；目标已满足时说明现状，不指向相反动作。";
            if (followup is not null) task = followup.ModelTask;
            stage = ProcessingStage.Model;
            var modelAnswer = intent is null
                ? (await deadline.RunAsync(t => _model.IdentifyAsync(payload.Candidates, payload.Png, t, payload.VisibleContext)))
                    .ToAnswer(identifiedProcess!, observation.Identity)
                : await deadline.RunAsync(t => _model.AskAsync(task, payload.Candidates, payload.Png, t, payload.VisibleContext,
                    _plugins.GeneralProvider?.InterpretationPolicy));
            var answer = modelAnswer.ForIntent(intent, allowQuestion: followup is { Clarification: null });
            var modelMs = total.ElapsedMilliseconds - observeMs;
            ProbeLog.Log("guide_response_received", ("modelMs", modelMs), ("mode", answer.Mode),
                ("clarificationLimited", intent is not null && modelAnswer.Mode == "ask" && answer.Mode != "ask"));
            if (!_gate.Accepts(version)) return;
            if (targetProcess.HasExited) throw new InvalidDataException("target_changed");
            stage = ProcessingStage.ResultValidation;
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
            _marker.HideAndReset();
            if (bounds is { } rect)
            {
                if (_marker.PlaceControl(rect, observation.Frame, _ball.Scale)) _marker.Show();
            }
            _panel.SetControl(bounds);
            _panel.SetStatus(answer.Explanation);
            _panel.SetPoint(bounds is not null && _marker.IsVisible);
            _ball.SetResult();
            _panel.SetReferenceQuery(observation.Identity?.ReferenceQuery);
            _panel.SetRegionAvailable(true);
            if (answer.Mode == "ask")
            {
                _gate.AwaitClarification(version);
                _ball.ShowQuestion();
            }
            else if (_plugins.GeneralProvider?.InterpretationPolicy?.UserLedFollowup == true)
            {
                _followup.Complete(flowTarget, intent);
                _panel.SetFollowupAvailable(_followup.CanOffer);
                _activeFollowup = null;
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
                var ownership = CurrentOwnership();
                var localIdentity = ownership is null ? null : _context?.Identity;
                _ball.StopTracking("processing_timeout");
                var message = "识别超时，请重新拖放。此次没有指向任何位置。";
                _panel.SetStatus(ownership is not null ? GuideIdentification.Unavailable(ownership, message, localIdentity) : message);
            }
            ProbeLog.Log("guide_failure", ("category", cancelled ? "cancelled" : "timeout"), ("stage", stage.ToString()));
        }
        catch (Exception ex)
        {
            if (ex is InvalidDataException { Message: "scope_unavailable" } && previous is not null && _gate.Accepts(version))
            {
                _context = previous with { Png = null };
                _contextVersion = version;
                _pendingIntent = intent;
                _marker.HideAndReset();
                _panel.SetRegionAvailable(true);
                _panel.SetStatus("没有可靠的自动识别范围。请点击“选择识别区域”，在此窗口内框选将发送的画面；无需重新回答。尚未发送本阶段的新请求。");
                ProbeLog.Log("guide_selection_required");
                return;
            }
            if (ex is InvalidDataException changed && changed.Message == "target_changed")
            {
                if (_gate.Accepts(version)) _ball.StopTracking("target_changed");
                return;
            }
            if (_gate.Accepts(version))
            {
                var ownership = CurrentOwnership();
                var localIdentity = ownership is null ? null : _context?.Identity;
                _ball.StopTracking("recognition_failed");
                _panel.SetStatus(ownership is not null &&
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
            // Keep the active token available for event-driven revalidation after displaying the answer.
            if (!ReferenceEquals(_request, request)) request.Dispose();
        }
    }

    async Task<bool> ActivateSelectedAsync(IntPtr window, int version, CancellationToken token)
    {
        if (Native.GetForegroundWindow() == window) return true;
        // Only an explicit drop, answer or confirmed lookup may reactivate its selected target once.
        _activationVersion = version;
        _activationSource = Native.GetForegroundWindow();
        try
        {
            var accepted = Native.SetForegroundWindow(window);
            ProbeLog.Log("guide_target_activation", ("target", Native.Hex(window)),
                ("fg", Native.Hex(Native.GetForegroundWindow())), ("accepted", accepted));
            var confirmed = await WaitForForegroundAsync(window, Native.GetForegroundWindow, token);
            if (!_gate.Accepts(version)) return false;
            ProbeLog.Log("guide_target_activation_complete", ("target", Native.Hex(window)),
                ("fg", Native.Hex(Native.GetForegroundWindow())), ("confirmed", confirmed));
            return confirmed;
        }
        finally { if (_activationVersion == version) _activationVersion = null; }
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
        InvalidDataException { Message: "scope_unavailable" } =>
            "无法可靠确定此目标所需的最小范围，此处理阶段未发送新的识别请求，也不退回发送整窗。请说明更具体的目标，或等待该插件补充支持。",
        HttpRequestException => "识别服务不可用，请检查本机模型代理。",
        InvalidDataException { Message: "target_not_foreground" } =>
            "目标窗口不在前台，本次处理已停止。请先点击目标窗口的空白处，再拖入问号球。",
        InvalidDataException { Message: "target_activation_failed" } =>
            "系统未允许激活目标窗口，本次处理已停止。请先点击目标窗口的空白处，再拖入问号球。",
        InvalidDataException { Message: "elevated_target" } =>
            "目标窗口以管理员权限运行，或无法确认其权限。鲸汐仅支持可确认的非管理员窗口，本次已停止识别，不会尝试提权或绕过限制。",
        InvalidDataException { Message: "capture_target_unavailable" or "capture_dimensions" or "capture_unusable" } =>
            "无法取得可靠的目标窗口画面，本次已停止识别。请恢复并保持目标窗口可见后重新拖放；不会改为读取整个屏幕。",
        InvalidDataException { Message: "unsupported_target" } =>
            "没有已启用插件支持这个窗口，本次处理已停止。",
        InvalidDataException { Message: "plugin_conflict" } =>
            "多个已启用插件匹配这个窗口，暂不识别。请调整插件启用清单后重启。",
        InvalidDataException { Message: "sensitive_content" } =>
            "检测到密码字段或疑似密钥，本次识别已停止，不显示解释或标记。请隐藏敏感内容后重新拖放。",
        InvalidDataException { Message: "privacy_unavailable" } =>
            "无法完成敏感内容检查，本次识别已停止，不显示解释或标记。这不代表检测到了敏感内容；请稍后重试，若仍失败请报告此提示。",
        _ => "未取得可靠的窗口信息或模型结果，此次不标记位置。"
    };

    async Task<GuideObservation> ReadContextAsync(GuideObservation previous, CancellationToken token)
    {
        if (!GuideObservation.IsCurrent(previous.Window, previous.ProcessId, previous.Frame))
            throw new InvalidDataException("target_changed");
        if (previous.Identity is { } identity)
        {
            using var process = Process.GetProcessById((int)previous.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartedAt) throw new InvalidDataException("target_changed");
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
        _ball.FullscreenHidden -= OnFullscreenHidden;
        _ball.LocationChanged -= OnEntryChanged;
        _ball.SizeChanged -= OnEntryChanged;
        _ball.TargetSelected -= OnSelected;
        _ball.TrackingStopped -= Cancel;
        _ball.TargetMoved -= OnTargetChanged;
        _ball.MenuRequested -= OnMenuRequested;
        _panel.MenuRequested -= OnMenuRequested;
        _ball.AllowsForeground = null;
        _panel.AnswerSubmitted -= OnAnswerSubmitted;
        _ball.AnswerSubmitted -= OnAnswerSubmitted;
        _panel.RegionRequested -= OnRegionRequested;
        _panel.ReferenceRequested -= OnReferenceRequested;
        _panel.FollowupRequested -= OnFollowupRequested;
        _panel.ConsentAccepted -= AcceptConsent;
        _activeFollowup = null;
        _followup.Clear();
        _referenceRequest?.Cancel();
        _references.Dispose();
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
