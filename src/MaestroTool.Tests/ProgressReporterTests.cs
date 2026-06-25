using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class ProgressReporterTests
{
    [Theory]
    [InlineData(0, 1)]      // empty: clamped to 1
    [InlineData(1, 1)]      // single item: 1
    [InlineData(9, 1)]      // <10: 1 (every item)
    [InlineData(10, 1)]     // exactly 10: 1
    [InlineData(11, 2)]     // 11–19: ceiling => 2 (≤10 updates)
    [InlineData(15, 2)]     // 15: 2 (8 updates), not 1 (15 updates)
    [InlineData(19, 2)]     // 19: 2
    [InlineData(20, 2)]     // exactly 20: 2
    [InlineData(21, 3)]     // 21: ceiling => 3
    [InlineData(99, 10)]    // 99: 10
    [InlineData(100, 10)]   // exactly 100: 10
    [InlineData(101, 11)]   // 101: ceiling => 11
    public void ItemStep_NeverExceedsTenUpdates(int total, int expectedStep)
    {
        var step = ProgressReporter.ItemStep(total);
        Assert.Equal(expectedStep, step);

        // Invariant: with this step, the count of emitted updates over `total` items is ≤ 10.
        if (total > 0)
        {
            var updates = (total + step - 1) / step; // ceil(total / step)
            Assert.True(updates <= 10, $"total={total} step={step} would emit {updates} updates");
        }
    }
}
