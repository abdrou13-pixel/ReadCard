using OsmondLocalApi.Models;

namespace OsmondLocalApi.Middleware;

public sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-API-Key";
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        var config = configuration.Get<AppConfig>() ?? new AppConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            await _next(context);
            return;
        }

        var incomingKey = context.Request.Headers[ApiKeyHeader].ToString();
        if (string.Equals(incomingKey, config.ApiKey, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(ReadResponse.Failure(
            ResponseCode.Unauthorized,
            "Missing or invalid X-API-Key header."));
    }
}
