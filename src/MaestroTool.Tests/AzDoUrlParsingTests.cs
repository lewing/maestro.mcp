using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class AzDoUrlParsingTests
{
    [Fact]
    public void ParseAzDoUrl_StandardFormat_ReturnsComponents()
    {
        // Arrange
        var url = "https://dev.azure.com/dnceng/internal/_git/dotnet-runtime";

        // Act
        var result = MaestroService.ParseAzDoUrl(url);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("dnceng", result.Value.org);
        Assert.Equal("internal", result.Value.project);
        Assert.Equal("dotnet-runtime", result.Value.repo);
    }

    [Fact]
    public void ParseAzDoUrl_LegacyFormat_ReturnsComponents()
    {
        // Arrange
        var url = "https://dnceng.visualstudio.com/internal/_git/dotnet-runtime";

        // Act
        var result = MaestroService.ParseAzDoUrl(url);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("dnceng", result.Value.org);
        Assert.Equal("internal", result.Value.project);
        Assert.Equal("dotnet-runtime", result.Value.repo);
    }

    [Fact]
    public void ParseAzDoUrl_GitHubUrl_ReturnsNull()
    {
        // Arrange
        var url = "https://github.com/dotnet/runtime";

        // Act
        var result = MaestroService.ParseAzDoUrl(url);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseAzDoUrl_MalformedUrl_ReturnsNull()
    {
        // Arrange
        var url = "not-a-url";

        // Act
        var result = MaestroService.ParseAzDoUrl(url);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseAzDoUrl_TrailingSlash_ReturnsComponents()
    {
        // Arrange
        var url = "https://dev.azure.com/dnceng/internal/_git/dotnet-runtime/";

        // Act
        var result = MaestroService.ParseAzDoUrl(url);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("dnceng", result.Value.org);
        Assert.Equal("internal", result.Value.project);
        Assert.Equal("dotnet-runtime", result.Value.repo);
    }

    [Fact]
    public void ParseAzDoUrl_WithQueryParams_ReturnsComponents()
    {
        // Arrange
        var url = "https://dev.azure.com/dnceng/internal/_git/dotnet-runtime?version=GBmain";

        // Act
        var result = MaestroService.ParseAzDoUrl(url);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("dnceng", result.Value.org);
        Assert.Equal("internal", result.Value.project);
        Assert.Equal("dotnet-runtime", result.Value.repo);
    }
}
