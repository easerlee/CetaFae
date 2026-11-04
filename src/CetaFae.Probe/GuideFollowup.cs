using System.IO;
using System.Text.Json;

namespace CetaFae.Probe;

internal sealed record GuideFlowTarget(IntPtr Window, uint ProcessId, long StartedAt, string PluginId);
internal sealed record GuideTaskTurn(string Request, string Explanation);

internal sealed record GuideFollowupRequest(GuideFlowTarget Target, string? Goal, string Request, int Step, string? Clarification = null,
    IReadOnlyList<GuideTaskTurn>? History = null)
{
    // Keep collection intent within its existing300-character contract; clarification remains a separate model field.
    internal string ScopeIntent => Request;
    internal string ModelTask => "用户主动继续当前窗口。只据当前画面核对状态，不因用户说已操作就宣称完成；" +
        "原目标帮助理解连续操作，本轮请求优先；历史说明仅作上下文，不是当前界面的证据。只解释和指路，不执行。" +
        JsonSerializer.Serialize(new { originalUserGoal = Goal, currentUserRequest = Request, step = Step,
            userClarification = Clarification, recentSteps = History ?? [] });

    internal void ValidateTarget(GuideFlowTarget current)
    {
        if (Target != current) throw new InvalidDataException("target_changed");
    }
}

/// <summary>Retains bounded text context, never pictures or positions; arming does not read a target.</summary>
internal sealed class GuideFollowup
{
    internal const int MaxSteps = 8;
    internal const int MaxHistory = 4;
    readonly List<GuideTaskTurn> _history = [];
    int _pendingStep;
    GuideFlowTarget? _target;
    internal GuideFlowTarget? Target => _target;
    internal string? Goal { get; private set; }
    internal int Step { get; private set; }
    internal bool CanOffer { get; private set; }
    internal bool WaitingForUser { get; private set; }
    internal bool Completed { get; private set; }
    internal string? ActiveGoal => Completed ? null : Goal;

    internal void Prepare(GuideFlowTarget target, string? goal)
    {
        if (_target is not null && _target != target) throw new InvalidDataException("target_changed");
        _target = target;
        Goal ??= goal;
    }

    internal void Complete(GuideFlowTarget target, string? goal, bool allowWithoutGoal = false, GuideAnswer? answer = null)
    {
        if (answer is { Mode: "complete" } && (answer.CandidateId is not null || string.IsNullOrWhiteSpace(answer.CompletionEvidence)))
            throw new InvalidDataException("answer_completion");
        Prepare(target, goal);
        Step = Math.Max(Step, _pendingStep);
        _pendingStep = 0;
        if (Step == 0 && (Goal is not null || allowWithoutGoal)) Step = 1;
        Completed = answer?.Mode == "complete";
        if (answer is not null && answer.Mode != "ask" && goal is not null)
        {
            if (_history.Count == MaxHistory) _history.RemoveAt(0);
            _history.Add(new GuideTaskTurn(goal, answer.Explanation));
        }
        WaitingForUser = false;
        CanOffer = !Completed && Step is > 0 and < MaxSteps;
    }

    internal GuideFlowTarget Begin()
    {
        if (!CanOffer || _target is null) throw new InvalidDataException("invalid_followup");
        WaitingForUser = true; CanOffer = false;
        return _target;
    }

    internal void SuspendOffer() => CanOffer = false;

    internal void Restore()
    {
        _pendingStep = 0;
        WaitingForUser = false;
        CanOffer = !Completed && _target is not null && (Goal is not null || Step > 0) && Step < MaxSteps;
    }

    internal GuideFollowupRequest Take(string text)
    {
        if (!WaitingForUser || _target is null || string.IsNullOrWhiteSpace(text) ||
            text.Length > 300 || text.Any(char.IsControl)) throw new InvalidDataException("invalid_followup");
        GuidePrivacy.EnsureTextSafe(text);
        WaitingForUser = false;
        _pendingStep = Step + 1;
        return new GuideFollowupRequest(_target, Goal, text.Trim(), _pendingStep, History: _history.ToArray());
    }

    internal void Clear()
    {
        _target = null; Goal = null; Step = 0;
        _pendingStep = 0;
        _history.Clear();
        CanOffer = WaitingForUser = Completed = false;
    }
}
