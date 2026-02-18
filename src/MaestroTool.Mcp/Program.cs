using MaestroTool.Core;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IMaestroApiClient>(_ =>
    new MaestroApiClient(Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN")));
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<MaestroService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "maestro", Version = "1.0.0" };
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(MaestroMcpTools).Assembly);

var app = builder.Build();
app.MapMcp();
app.Run();
