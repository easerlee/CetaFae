using System.IO;
using System.Text.Json;

namespace CetaFae.Probe;

internal sealed record GuideFlowTarget(IntPtr Window, uint ProcessId, long StartedAt, string PluginId);

internal sealed record GuideFollowupRequest(GuideFlowTarget Target, string Goal, string Request, int Step, string? Clarification = null)
{
    // Keep collection intent within its existing300-character contract; clarification remains a separate model field.
    internal string ScopeIntent => Request;
    internal string ModelTask => "用户主动继续当前窗口。只据当前画面核对状态，不因用户说已操作就宣称完成；" +
        "原目标仅供参考，本轮请求优先，不复用上一步的界面状态。只解释和指路，不执行。" +
        JsonSerializer.Serialize(new { originalUserGoal = Goal, currentUserRequest = Request, step = Step, userClarification = Clarification });

    internal void ValidateTarget(GuideFlowTarget current)
    {
        if (Target != current) throw new InvalidDataException("target_changed");
    }
}

/// <summary>Keeps only a bounded user goal and process binding; arming never reads a target.</summary>
internal sealed class GuideFollowup
{
    internal const int MaxSteps = 8;
    GuideFlowTarget? _target;
    internal GuideFlowTarget? Target => _target;
    internal string? Goal { get; private set; }
    internal int Step { get; private set; }
    internal bool CanOffer { get; private set; }
    internal bool WaitingForUser { get; private set; }

    internal void Complete(GuideFlowTarget target, string? goal)
    {
        if (_target is not null && _target != target) throw new InvalidDataException("target_changed");
        _target = target;
        if (goal is not null && Goal is null) { Goal = goal; Step = 1; }
        WaitingForUser = false;
        CanOffer = Goal is not null && Step < MaxSteps;
    }

    internal GuideFlowTarget Begin()
    {
        if (!CanOffer || _target is null) throw new InvalidDataException("invalid_followup");
        WaitingForUser = true; CanOffer = false;
        return _target;
    }

    internal void SuspendOffer() => CanOffer = false;

    internal GuideFollowupRequest Take(string text)
    {
        if (!WaitingForUser || _target is null || Goal is null || string.IsNullOrWhiteSpace(text) ||
            text.Length > 300 || text.Any(char.IsControl)) throw new InvalidDataException("invalid_followup");
        GuidePrivacy.EnsureTextSafe(text);
        WaitingForUser = false;
        return new GuideFollowupRequest(_target, Goal, text.Trim(), ++Step);
    }

    internal void Clear()
    {
        _target = null; Goal = null; Step = 0;
        CanOffer = WaitingForUser = false;
    }
}
