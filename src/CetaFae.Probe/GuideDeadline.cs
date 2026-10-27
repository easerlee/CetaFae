namespace CetaFae.Probe;

public sealed class GuideDeadline : IDisposable
{
    readonly CancellationTokenSource _budget;
    public CancellationToken Token => _budget.Token;

    public GuideDeadline(TimeSpan timeout, CancellationToken lifetime)
    {
        _budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _budget.CancelAfter(timeout);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        Token.ThrowIfCancellationRequested();
        var result = await operation(Token).WaitAsync(Token);
        Token.ThrowIfCancellationRequested();
        return result;
    }

    // Disarming a completed processing budget must not cancel the retained guidance session.
    public void Dispose() => _budget.Dispose();
}
