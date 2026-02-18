using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class MaestroToolOptionsTests
{
    [Fact]
    public void EnableDestructiveActions_DefaultsToFalse()
    {
        var options = new MaestroToolOptions();

        Assert.False(options.EnableDestructiveActions);
    }
}
