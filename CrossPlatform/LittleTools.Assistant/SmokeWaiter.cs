using Avalonia.Threading;

namespace LittleTools.Assistant;

/// <summary>
/// Condition polling for the smoke runs.
///
/// The smokes used fixed delays, which is what made them flaky: a render pass, a
/// data refresh or a focus transition can take longer than the delay on a busy
/// desktop, so a run could assert before the state it needed existed. Every step
/// now waits for the condition it actually depends on and reports the state it
/// observed when the timeout expires.
/// </summary>
internal static class SmokeWaiter
{
    /// <summary>One UI thread turn: queued work and a render pass get to run.</summary>
    public static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(20);
    }

    /// <summary>Pumps the UI thread until the condition holds, or the timeout expires.</summary>
    public static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (condition()) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await PumpAsync();
        }
    }

    /// <summary>Waits for the condition and throws with the observed state when it never holds.</summary>
    public static async Task WaitOrThrowAsync(Func<bool> condition, TimeSpan timeout, Func<string> describe)
    {
        if (await WaitAsync(condition, timeout)) return;
        throw new InvalidOperationException(
            "Timed out after " + timeout.TotalSeconds.ToString("0.#") + "s waiting for " + describe() + ".");
    }
}
