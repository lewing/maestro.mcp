using MaestroTool.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IMaestroApiClient>(_ =>
    new MaestroApiClient(Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN")));
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<MaestroService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "maestro", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(MaestroMcpTools).Assembly);

var app = builder.Build();
await app.RunAsync();
