namespace Egap.Spreadsheets;

/// <summary>
/// The time that NOW, TODAY and the date filters and rules use: the system clock, unless a test pins it for its own flow.
/// </summary>
internal static class Clock
{
    private static readonly AsyncLocal<DateTime?> Pinned = new();

    /// <summary>The local date and time.</summary>
    internal static DateTime Now => Pinned.Value ?? DateTime.Now;

    /// <summary>The local date.</summary>
    internal static DateTime Today => Now.Date;

    /// <summary>Pins the clock to <paramref name="now"/> until the returned scope is disposed.</summary>
    internal static IDisposable Pin(DateTime now)
    {
        DateTime? before = Pinned.Value;
        Pinned.Value = now;
        return new Scope(before);
    }

    private sealed class Scope(DateTime? before) : IDisposable
    {
        public void Dispose() => Pinned.Value = before;
    }
}
