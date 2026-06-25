using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class HttpClientConfigurationTests
{
    [Fact]
    public void MaestroToolUserAgent_InitializeFromAssembly_UsesInformationalVersion()
    {
        // Arrange
        var assembly = typeof(MaestroToolUserAgent).Assembly;
        
        // Act
        MaestroToolUserAgent.Initialize(assembly);
        var productIdentifier = MaestroToolUserAgent.ProductIdentifier;

        // Assert
        // Should be "maestro.mcp/X.Y.Z" (3-part semver from InformationalVersion),
        // not "maestro.mcp/X.Y.Z.0" (4-part AssemblyVersion)
        Assert.StartsWith("maestro.mcp/", productIdentifier);
        // Should not have a 4th zero component like "1.0.0.0"
        var version = productIdentifier.Substring("maestro.mcp/".Length);
        var parts = version.Split('.');
        // Either 3 parts (X.Y.Z) or more parts if prerelease/metadata, but never ending in .0
        Assert.True(parts.Length >= 3, $"Expected at least 3 version parts, got: {version}");
        // If it's exactly 4 parts and 4th is "0", that's the AssemblyVersion pattern we want to avoid
        if (parts.Length == 4 && parts[3] == "0")
        {
            Assert.Fail($"Version appears to be 4-part AssemblyVersion ({version}); should use 3-part InformationalVersion");
        }
    }
    
    [Fact]
    public void MaestroToolUserAgent_InitializeFromString_StripsGitShaSuffix()
    {
        // Arrange & Act
        MaestroToolUserAgent.Initialize("1.2.3+abc123def");
        var productIdentifier = MaestroToolUserAgent.ProductIdentifier;

        // Assert
        Assert.Equal("maestro.mcp/1.2.3", productIdentifier);
        Assert.DoesNotContain("+", productIdentifier);
    }

    [Fact]
    public void MaestroToolUserAgent_IncludesToolNameAndVersion()
    {
        // Arrange & Act
        MaestroToolUserAgent.Initialize("1.2.3");
        var productIdentifier = MaestroToolUserAgent.ProductIdentifier;

        // Assert
        Assert.Equal("maestro.mcp/1.2.3", productIdentifier);
    }

    [Fact]
    public void MaestroToolUserAgent_ApplyToHttpClient_SetsUserAgentAndCustomHeader()
    {
        // Arrange
        MaestroToolUserAgent.Initialize("1.2.3");
        var client = new HttpClient();

        // Act
        MaestroToolUserAgent.Apply(client);

        // Assert
        Assert.Contains(client.DefaultRequestHeaders.UserAgent,
            value => string.Equals(value.Product?.Name, MaestroToolUserAgent.ToolName, StringComparison.OrdinalIgnoreCase));
        Assert.True(client.DefaultRequestHeaders.TryGetValues(MaestroToolUserAgent.ToolHeaderName, out var values));
        Assert.Contains(MaestroToolUserAgent.ToolHeaderValue, values);
    }

    [Fact]
    public void MaestroToolUserAgent_ApplyMultipleTimes_DoesNotDuplicate()
    {
        // Arrange
        MaestroToolUserAgent.Initialize("1.2.3");
        var client = new HttpClient();

        // Act
        MaestroToolUserAgent.Apply(client);
        MaestroToolUserAgent.Apply(client);

        // Assert
        var userAgentCount = client.DefaultRequestHeaders.UserAgent
            .Count(value => string.Equals(value.Product?.Name, MaestroToolUserAgent.ToolName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, userAgentCount);

        var headerCount = client.DefaultRequestHeaders.GetValues(MaestroToolUserAgent.ToolHeaderName).Count();
        Assert.Equal(1, headerCount);
    }

    [Fact]
    public void MaestroToolUserAgent_ApplyToNullClient_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => MaestroToolUserAgent.Apply(null!));
    }

    [Fact]
    public void MaestroToolUserAgent_DefaultVersion_IsSet()
    {
        // The UserAgent should have a default version even if Initialize is not called
        // This ensures the tool is identifiable even in misconfigured scenarios
        var productIdentifier = MaestroToolUserAgent.ProductIdentifier;
        Assert.StartsWith("maestro.mcp/", productIdentifier);
    }
}
