using Microsoft.AspNetCore.Diagnostics;

namespace TheKrystalShip.Api.Infrastructure;

/// <summary>
/// Turns any unhandled exception into the frozen error contract as a <c>500</c>,
/// replacing ASP.NET's default <c>ProblemDetails</c> body so the wire shape stays
/// <c>{error:{code,message}}</c>. Registered via <c>AddExceptionHandler</c> +
/// <c>UseExceptionHandler</c>; <c>AddProblemDetails</c> is also registered only to
/// satisfy the middleware's startup guard (this handler always handles, so the
/// ProblemDetails fallback never actually fires).
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception on {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        if (httpContext.Response.HasStarted)
            return false; // can't replace a response already on the wire

        await ApiErrors.WriteAsync(
            httpContext, StatusCodes.Status500InternalServerError,
            "internal_error", "An unexpected error occurred.");
        return true;
    }
}
