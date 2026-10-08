using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace OrbitalSentinel.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Request failed with trace {TraceId}", context.TraceIdentifier);
        var unavailable = exception is NpgsqlException or TimeoutException;
        context.Response.StatusCode = unavailable ? 503 : 500;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = context.Response.StatusCode,
            Title = unavailable ? "A required service is temporarily unavailable." : "The request could not be completed.",
            Extensions = { ["traceId"] = context.TraceIdentifier }
        }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
