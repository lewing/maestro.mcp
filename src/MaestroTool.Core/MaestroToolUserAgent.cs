using System.Net.Http.Headers;

namespace MaestroTool.Core;

public static class MaestroToolUserAgent
{
    public const string ToolName = "maestro.mcp";
    public const string ToolHeaderName = "X-Maestro-Mcp-Tool";
    public const string ToolHeaderValue = ToolName;

    public static string ProductIdentifier { get; private set; } = $"{ToolName}/0.0.0";

    public static void Initialize(string version)
    {
        ProductIdentifier = $"{ToolName}/{version}";
    }

    public static void Apply(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!client.DefaultRequestHeaders.UserAgent.Any(IsToolProduct))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductIdentifier);

        if (!client.DefaultRequestHeaders.Contains(ToolHeaderName))
            client.DefaultRequestHeaders.Add(ToolHeaderName, ToolHeaderValue);
    }

    private static bool IsToolProduct(ProductInfoHeaderValue value)
        => string.Equals(value.Product?.Name, ToolName, StringComparison.OrdinalIgnoreCase);
}
