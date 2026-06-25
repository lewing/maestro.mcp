using System.Net;
using System.Text;
using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class HttpClientConfigurationTests
{
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

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseContent { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json")
            });
        }
    }
}
