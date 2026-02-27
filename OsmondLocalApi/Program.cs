using System.Text.Json;
using OsmondLocalApi.Config;
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
    var defaultConfig = JsonSerializer.Serialize(new AppSettings(), new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(configPath, defaultConfig);
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(appRoot)
    .AddJsonFile(configPath, optional: false, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "OSMOND_LOCAL_API_");

builder.Services.Configure<AppSettings>(builder.Configuration);
builder.Services.AddSingleton<IOsmondReaderService, OsmondReaderService>();
builder.Services.AddHostedService<ReaderHostedService>();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Async(cfg => cfg.File(
        path: Path.Combine(logsPath, "osmondlocalapi-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true))
    .CreateLogger();

builder.Host
    .UseSerilog()
    .UseWindowsService(options =>
    {
        options.ServiceName = "OsmondLocalApi";
    });

var appSettings = builder.Configuration.Get<AppSettings>() ?? new AppSettings();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(appSettings.Port);
});

var app = builder.Build();

app.MapPost("/read", async (HttpContext context, IOsmondReaderService readerService, CancellationToken cancellationToken) =>
{
    var cfg = context.RequestServices.GetRequiredService<IConfiguration>().Get<AppSettings>() ?? new AppSettings();
    var apiKey = cfg.ApiKey?.Trim();

    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        var provided = context.Request.Headers["X-API-Key"].FirstOrDefault();
        if (!string.Equals(apiKey, provided, StringComparison.Ordinal))
        {
            return Results.Json(new ReadResponse
            {
                Ok = false,
                InternalCode = ErrorCode.Unauthorized,
                Message = "Invalid API key."
            }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    var result = await readerService.ReadAsync(cancellationToken);

    if (result.InternalCode == ErrorCode.ReadInProgress)
    {
        return Results.Json(result, statusCode: StatusCodes.Status409Conflict);
    }

    if (!result.Ok)
    {
        return Results.Json(result, statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Json(result, statusCode: StatusCodes.Status200OK);
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

Log.Information("Starting OsmondLocalApi on 127.0.0.1:{Port}", appSettings.Port);

await app.RunAsync();
