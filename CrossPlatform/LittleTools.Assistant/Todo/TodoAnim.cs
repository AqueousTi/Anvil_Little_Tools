using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Small manual animator. Avalonia's animation API is awkward to drive with a
/// completion callback, and the todo module needs "fade out, change the model,
/// then reflow" sequencing, so frames are driven from a dispatcher timer.
/// </summary>
internal static class TodoAnim
{
    private const int FrameMilliseconds = 16;

    public static void Fade(Visual visual, double from, double to, int milliseconds, Action? completed = null)
    {
        visual.Opacity = from;
        if (milliseconds <= 0)
        {
            visual.Opacity = to;
            completed?.Invoke();
            return;
        }

        var elapsed = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMilliseconds) };
        timer.Tick += (_, _) =>
        {
            elapsed += FrameMilliseconds;
            var t = Math.Min(1, elapsed / (double)milliseconds);
            visual.Opacity = from + (to - from) * Ease(t);
            if (t < 1) return;
            timer.Stop();
            visual.Opacity = to;
            completed?.Invoke();
        };
        timer.Start();
    }

    /// <summary>Fades in while sliding up, used for the staggered card entrance.</summary>
    public static void Materialize(Visual visual, double fromY, int milliseconds, int delayMilliseconds = 0)
    {
        var transform = new TranslateTransform { Y = fromY };
        visual.RenderTransform = transform;
        visual.Opacity = 0;

        void Start()
        {
            var elapsed = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMilliseconds) };
            timer.Tick += (_, _) =>
            {
                elapsed += FrameMilliseconds;
                var t = Math.Min(1, elapsed / (double)milliseconds);
                var eased = Ease(t);
                visual.Opacity = eased;
                transform.Y = fromY * (1 - eased);
                if (t < 1) return;
                timer.Stop();
                visual.Opacity = 1;
                transform.Y = 0;
            };
            timer.Start();
        }

        if (delayMilliseconds <= 0)
        {
            Start();
            return;
        }
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMilliseconds) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            Start();
        };
        delay.Start();
    }

    /// <summary>
    /// Animates a numeric value. The Windows module used CubicEase; easeOut maps
    /// to EaseOut and the default to EaseInOut, matching the durations it used for
    /// card placement (145ms) and the post-completion reflow (335ms).
    /// </summary>
    public static void Tween(double from, double to, int milliseconds, Action<double> apply,
        Action? completed = null, bool easeOut = false)
    {
        apply(from);
        if (milliseconds <= 0)
        {
            apply(to);
            completed?.Invoke();
            return;
        }

        var elapsed = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMilliseconds) };
        timer.Tick += (_, _) =>
        {
            elapsed += FrameMilliseconds;
            var t = Math.Min(1, elapsed / (double)milliseconds);
            var eased = easeOut ? Ease(t) : InOut(t);
            apply(from + (to - from) * eased);
            if (t < 1) return;
            timer.Stop();
            apply(to);
            completed?.Invoke();
        };
        timer.Start();
    }

    private static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

    private static double InOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
}
