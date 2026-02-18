using MaestroTool.Core;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IMaestroApiClient>(_ =>
    new MaestroApiClient(Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN")));
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<MaestroService>();

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
        options.ServerInfo = new() { Name = "maestro", Version = "0.2.0" };
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(MaestroMcpTools).Assembly);

var app = builder.Build();
app.MapMcp();
app.Run();
