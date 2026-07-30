using ShelfAware.Core.Prediction;

namespace ShelfAware.Tests;

public class CountingAdviceTests
{
    [Theory]
    [InlineData(3.0)]   // milk-fast
    [InlineData(10.0)]  // the boundary itself is advised against
    public void A_fast_mover_is_advised_against(double median) =>
        Assert.True(CountingAdvice.IsHardToKeepTrue(median));

    [Theory]
    [InlineData(10.5)]  // just past the boundary
    [InlineData(21.0)]  // the beans hero's rhythm
    public void A_slow_mover_is_not(double median) =>
        Assert.False(CountingAdvice.IsHardToKeepTrue(median));

    [Fact]
    public void A_still_learning_item_is_not_advised_against()
    {
        // No rhythm says nothing either way — and §13.8's census stock is exactly the rhythm-less
        // population counting exists for, so warning here would warn against the feature's best case.
        Assert.False(CountingAdvice.IsHardToKeepTrue(null));
    }
}
