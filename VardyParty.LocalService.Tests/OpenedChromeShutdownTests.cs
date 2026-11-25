using VardyParty.LocalService.Compute;
using Xunit;

namespace VardyParty.LocalService.Tests;

public sealed class OpenedChromeShutdownTests
{
    [Fact]
    public void ChromeRootsLaunchedBy_KillsOnlyOurBrowserRoots()
    {
        // Arrange
        const int host = 10;
        var rows = new OpenedChromeShutdown.ProcessRow[]
        {
            new(host, 1, "VardyParty.LocalService"),
            new(20, host, "chrome"),
            new(21, 20, "chrome"),
            new(30, host, "notepad"),
            new(40, 1, "chrome")
        };

        // Act
        var roots = OpenedChromeShutdown.ChromeRootsLaunchedBy(host, rows);

        // Assert
        Assert.Equal([20], roots);
    }

    [Fact]
    public void ChromeRootsLaunchedBy_FindsNestedLauncherThenChrome()
    {
        // Arrange
        const int host = 5;
        var rows = new OpenedChromeShutdown.ProcessRow[]
        {
            new(host, 1, "dotnet"),
            new(8, host, "VardyParty.LocalService"),
            new(9, 8, "chrome"),
            new(11, 9, "chrome")
        };

        // Act
        var roots = OpenedChromeShutdown.ChromeRootsLaunchedBy(host, rows);

        // Assert
        Assert.Equal([9], roots);
    }

    [Fact]
    public void IsChromeName_AcceptsChromeAndChromiumOnly()
    {
        // Arrange
        // Act
        // Assert
        Assert.True(OpenedChromeShutdown.IsChromeName("chrome.exe"));
        Assert.True(OpenedChromeShutdown.IsChromeName("chromium"));
        Assert.True(OpenedChromeShutdown.IsChromeName("chromium-browse"));
        Assert.True(OpenedChromeShutdown.IsChromeName("chromium-browser"));
        Assert.True(OpenedChromeShutdown.IsChromeName("google-chrome"));
        Assert.False(OpenedChromeShutdown.IsChromeName("firefox"));
        Assert.False(OpenedChromeShutdown.IsChromeName(""));
    }

    [Fact]
    public void TryParseProcStat_UsesLastParenAndTruncatedChromiumComm()
    {
        // Arrange
        const string chromium = "1234 (chromium-browse) S 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
        const string withParen = "99 (foo)bar) S 7 0 0 0 0 0 0 0 0 0 0 0 0 0 0";

        // Act
        var parsedChromium = OpenedChromeShutdown.TryParseProcStat(1234, chromium, out var chromiumRow);
        var parsedParen = OpenedChromeShutdown.TryParseProcStat(99, withParen, out var parenRow);
        var roots = OpenedChromeShutdown.ChromeRootsLaunchedBy(1, [chromiumRow]);

        // Assert
        Assert.True(parsedChromium);
        Assert.Equal("chromium-browse", chromiumRow.Name);
        Assert.Equal(1, chromiumRow.ParentPid);
        Assert.True(parsedParen);
        Assert.Equal("foo)bar", parenRow.Name);
        Assert.Equal(7, parenRow.ParentPid);
        Assert.Equal([1234], roots);
    }

    [Fact]
    public void ChromeRootsLaunchedBy_DuplicatePidKeepsFirstName()
    {
        // Arrange
        const int host = 10;
        var rows = new OpenedChromeShutdown.ProcessRow[]
        {
            new(host, 1, "dotnet"),
            new(20, host, "chromium-browse"),
            new(20, host, "firefox")
        };

        // Act
        var roots = OpenedChromeShutdown.ChromeRootsLaunchedBy(host, rows);

        // Assert
        Assert.Equal([20], roots);
    }
}
