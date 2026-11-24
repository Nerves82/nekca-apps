namespace NekcaApps.Core;

/// <summary>
/// Retries a file step that fails because something has the files open for a moment: on Windows that's usually the
/// virus scanner reading files just written, or the search indexer. A file that stays locked still fails in the end.
/// </summary>
public static class IoRetry
{
    public const int DefaultAttempts = 10;

    /// <summary>Waits between tries; replaced in tests.</summary>
    public static Action<TimeSpan> Wait { get; set; } = Thread.Sleep;

    public static void Run(Action step, int attempts = DefaultAttempts)
    {
        for (var i = 1; ; i++)
        {
            try { step(); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < attempts)
            {
                // 0.2 s, 0.4 s, … up to 1.5 s: about 9 seconds in all
                Wait(TimeSpan.FromMilliseconds(Math.Min(1500, 200 * i)));
            }
        }
    }
}
