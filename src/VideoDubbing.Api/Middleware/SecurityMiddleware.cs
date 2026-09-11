using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VideoDubbing.Api.Middleware;

public sealed class ExceptionMappingHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Invalid Request"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            _ => (StatusCodes.Status500InternalServerError, "Server Error")
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = title,
            Detail = exception.Message,
            Status = status
        }, cancellationToken);
        return true;
    }
}

public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var required = _configuration.GetValue("Security:RequireApiKey", false);
        if (!required || context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/swagger") || context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        var expected = _configuration["Security:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected) || context.Request.Headers["X-Api-Key"] != expected)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key." });
            return;
        }

        await _next(context);
    }
}
