using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Pure tests over <see cref="JiraSource.ComputeWindows"/> — no HTTP, no DB.
/// Covers the windowing, clamping, and ordering rules that the XML download
/// loop relies on.
/// </summary>
public class JiraSourceDownloadXmlTests
{
    [Fact]
    public void DefaultWindow_ProducesPerDayWindows()
    {
        DateOnly start = new(2026, 4, 15);
        DateOnly today = new(2026, 4, 17);

        List<(DateOnly Start, DateOnly End)> windows = JiraSource.ComputeWindows(start, today, 1).ToList();

        Assert.Equal(3, windows.Count);
        Assert.Equal((new DateOnly(2026, 4, 15), new DateOnly(2026, 4, 15)), windows[0]);
        Assert.Equal((new DateOnly(2026, 4, 16), new DateOnly(2026, 4, 16)), windows[1]);
        Assert.Equal((new DateOnly(2026, 4, 17), new DateOnly(2026, 4, 17)), windows[2]);
    }

    [Fact]
    public void SevenDayWindow_ProducesThreeWindowsOver20Days()
    {
        DateOnly start = new(2026, 4, 1);
        DateOnly today = new(2026, 4, 20);

        List<(DateOnly Start, DateOnly End)> windows = JiraSource.ComputeWindows(start, today, 7).ToList();

        Assert.Equal(3, windows.Count);
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 7)), windows[0]);
        Assert.Equal((new DateOnly(2026, 4, 8), new DateOnly(2026, 4, 14)), windows[1]);
        Assert.Equal((new DateOnly(2026, 4, 15), new DateOnly(2026, 4, 20)), windows[2]);
    }

    [Fact]
    public void FinalPartialWindow_IsClampedToToday()
    {
        DateOnly start = new(2026, 4, 1);
        DateOnly today = new(2026, 4, 10);

        List<(DateOnly Start, DateOnly End)> windows = JiraSource.ComputeWindows(start, today, 7).ToList();

        Assert.Equal(2, windows.Count);
        Assert.Equal(new DateOnly(2026, 4, 7), windows[0].End);
        Assert.Equal(new DateOnly(2026, 4, 8), windows[1].Start);
        // Window would run through 4/14 but must clamp to today.
        Assert.Equal(today, windows[1].End);
    }

    [Fact]
    public void StartAfterToday_ProducesNoWindows()
    {
        DateOnly start = new(2026, 4, 20);
        DateOnly today = new(2026, 4, 17);

        List<(DateOnly Start, DateOnly End)> windows = JiraSource.ComputeWindows(start, today, 7).ToList();
        Assert.Empty(windows);
    }

    [Fact]
    public void InvalidWindow_ClampedDefensively()
    {
        DateOnly start = new(2026, 4, 15);
        DateOnly today = new(2026, 4, 17);

        List<(DateOnly Start, DateOnly End)> windows = JiraSource.ComputeWindows(start, today, 0).ToList();

        Assert.Equal(3, windows.Count);
        Assert.All(windows, w => Assert.Equal(w.Start, w.End));
    }

    [Fact]
    public void LargeWindow_CoversEntireSpanInOneStep()
    {
        DateOnly start = new(2025, 1, 1);
        DateOnly today = new(2025, 12, 31);

        List<(DateOnly Start, DateOnly End)> windows = JiraSource.ComputeWindows(start, today, 365).ToList();

        Assert.Single(windows);
        Assert.Equal(start, windows[0].Start);
        Assert.Equal(today, windows[0].End);
    }

    [Fact]
    public void SingleDay_SpansOneWindow()
    {
        DateOnly start = new(2026, 4, 17);
        DateOnly today = new(2026, 4, 17);

        List<(DateOnly Start, DateOnly End)> windows = JiraSource.ComputeWindows(start, today, 7).ToList();
        Assert.Single(windows);
        Assert.Equal((today, today), windows[0]);
    }
}
