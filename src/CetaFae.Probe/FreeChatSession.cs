using System.IO;

namespace CetaFae.Probe;

internal sealed record ChatTurn(string Question, string Reply, bool RequiresWindow = false);

internal sealed class FreeChatSession(Func<IReadOnlyList<ChatTurn>, string, CancellationToken, Task<string?>> reply)
{
    internal const int MaxTurns = 4;
    internal const int MaxQuestionLength = 300;
    readonly List<ChatTurn> _history = [];
    CancellationTokenSource? _pending;

    internal async Task<ChatTurn?> SubmitAsync(string input, CancellationToken lifetime = default)
    {
        if (_pending is not null) return null;
        var question = input.Trim();
        if (question.Length is 0 or > MaxQuestionLength || question.Any(char.IsControl))
            throw new InvalidDataException("chat_question");
        GuidePrivacy.EnsureTextSafe(question);

        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _pending = request;
        try
        {
            request.Token.ThrowIfCancellationRequested();
            var answer = await reply(_history.ToArray(), question, request.Token).WaitAsync(request.Token);
            if (request.IsCancellationRequested) return null;
            return answer is null ? new ChatTurn(question, "", RequiresWindow: true) : Remember(question, answer);
        }
        catch (Exception) when (request.IsCancellationRequested) { return null; }
        finally { if (ReferenceEquals(_pending, request)) _pending = null; }
    }

    internal ChatTurn Remember(string question, string answer)
    {
        var turn = new ChatTurn(question, ValidateReply(answer));
        if (_history.Count == MaxTurns) _history.RemoveAt(0);
        _history.Add(turn);
        return turn;
    }

    internal void Reset()
    {
        var pending = _pending;
        _pending = null;
        _history.Clear();
        pending?.Cancel();
    }

    internal static string ValidateReply(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1200 ||
            value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InvalidDataException("chat_reply");
        GuidePrivacy.EnsureTextSafe(value);
        return value.Trim();
    }
}
