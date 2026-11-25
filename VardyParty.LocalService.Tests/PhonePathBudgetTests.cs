using VardyParty.LocalService.Compute;
using Xunit;

namespace VardyParty.LocalService.Tests;

/// <summary>
/// A live mobile-data session reached the second page with no chips, because
/// the short chip wait expired while extra pages were still loading.
/// </summary>
public class PhonePathBudgetTests
{
    [Fact]
    public void ChromeSequence_FinishesInsideThePhoneWait_AndTheHostOutlastsThePhone()
    {
        // Arrange
        // Act
        // Assert
        Assert.Equal(TimeSpan.FromSeconds(360), PhonePathBudget.ChromeSequence);
        Assert.True(PhonePathBudget.ChromeSequence < PhonePathBudget.GuestFindingStreamsWait);
        Assert.True(PhonePathBudget.GuestFindingStreamsWait < PhonePathBudget.HostHttpTimeout);
        Assert.True(PhonePathBudget.HarvestSequence(8) < PhonePathBudget.HostHttpTimeout);
    }
}
