using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OsmondLocalApi.Middleware;
using OsmondLocalApi.Models;
using OsmondLocalApi.Services;
using Serilog;

var programDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
var appRoot = Path.Combine(programDataRoot, "OsmondLocalApi");
var configPath = Path.Combine(appRoot, "appsettings.json");
var logsPath = Path.Combine(appRoot, "logs");

Directory.CreateDirectory(appRoot);
Directory.CreateDirectory(logsPath);

if (!File.Exists(configPath))
{
    var bootstrapConfig = new AppConfig();
    File.WriteAllText(configPath, JsonSerializer.Serialize(bootstrapConfig, new JsonSerializerOptions { WriteIndented = true }));
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(appRoot)
    .AddJsonFile(configPath, optional: false, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "OSMONDLOCALAPI_");

builder.Services.Configure<AppConfig>(builder.Configuration);
builder.Services.AddSingleton<OsmondReaderService>();
builder.Services.AddSingleton<IOsmondReaderService>(sp => sp.GetRequiredService<OsmondReaderService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OsmondReaderService>());

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.RollingFile(Path.Combine(logsPath, "log-{Date}.txt"))
    .CreateLogger();

builder.Host.UseSerilog();
builder.Host.UseWindowsService(options => options.ServiceName = "OsmondLocalApi");

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var config = context.Configuration.Get<AppConfig>() ?? new AppConfig();
    options.Listen(IPAddress.Loopback, config.Port);
});

var app = builder.Build();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapPost("/read", async (IOsmondReaderService reader, HttpContext context, CancellationToken ct) =>
{
    var response = await reader.ReadAsync(ct);

    var status = response.InternalCode switch
    {
        ResponseCode.Ok => StatusCodes.Status200OK,
        ResponseCode.ReadInProgress => StatusCodes.Status409Conflict,
        ResponseCode.Unauthorized => StatusCodes.Status401Unauthorized,
        ResponseCode.NoDocument => StatusCodes.Status404NotFound,
        ResponseCode.Timeout => StatusCodes.Status408RequestTimeout,
        ResponseCode.DeviceNotFound or ResponseCode.DeviceOpenFailed => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    return Results.Json(response, statusCode: status);
});

app.MapGet("/health", (IOptionsSnapshot<AppConfig> cfg) => Results.Ok(new
{
    ok = true,
    port = cfg.Value.Port,
    timeoutSeconds = cfg.Value.TimeoutSeconds
}));

await app.RunAsync();
