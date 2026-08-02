namespace WindowsGSH.Core.Readiness;

public sealed class FirstRunReadinessRunner(
    Func<Task<IReadOnlyList<ReadinessCheckResult>>> runReadiness,
    Action<Exception>? setupFailure = null)
{
    public async Task<IReadOnlyList<ReadinessCheckResult>> RunAsync(Func<Task>? setup = null)
    {
        if (setup is not null)
        {
            try
            {
                await setup().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                setupFailure?.Invoke(ex);
            }
        }

        return await runReadiness().ConfigureAwait(false);
    }
}
