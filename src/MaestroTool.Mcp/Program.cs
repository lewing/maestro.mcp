using System.Reflection;
using MaestroTool.Core;
using ModelContextProtocol.Server;

MaestroToolUserAgent.Initialize(Assembly.GetExecutingAssembly());

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IMaestroApiClient>(_ =>
    new MaestroApiClient(Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN")));
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<IGitHubApiClient, GitHubApiClient>();
builder.Services.AddSingleton<MaestroService>(sp =>
    new MaestroService(
        sp.GetRequiredService<IMaestroApiClient>(),
        sp.GetRequiredService<CacheService>(),
        sp.GetRequiredService<IGitHubApiClient>()
    ));

var enableDestructive = bool.TryParse(
    Environment.GetEnvironmentVariable("MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS"),
    out var enabled) && enabled;

builder.Services.AddSingleton(new MaestroToolOptions
{
    EnableDestructiveActions = enableDestructive
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "maestro", Version = "0.15.0" };
        
        // Add filters for better parameter validation and error messages
        options.AddBindingErrorFilter()
               .AddUnknownParameterFilter(typeof(MaestroMcpTools).Assembly);
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(MaestroMcpTools).Assembly, new System.Text.Json.JsonSerializerOptions
    {
        // Reject unknown parameters at binding time so callers get a structured error
        // instead of silent data loss. The AddBindingErrorFilter above catches the resulting
        // ArgumentException(paramName:"arguments") and wraps it as McpException.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        // Required: SDK calls MakeReadOnly() on options before schema gen; without a
        // TypeInfoResolver set, CreateJsonSchemaCore tries to assign one post-lock → InvalidOperationException.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    });

var app = builder.Build();
app.MapMcp();
app.Run();
