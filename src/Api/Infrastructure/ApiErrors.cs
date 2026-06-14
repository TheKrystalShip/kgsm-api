using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Infrastructure;

/// <summary>
/// Writes the frozen <see cref="ErrorEnvelope"/> contract to a response. Shared by the
/// unhandled-exception handler (<see cref="ApiExceptionHandler"/>) and the status-code
/// handler (unmatched 404s, and later 401/403 from auth), so every non-2xx the API
/// emits — from controllers or the pipeline — has the same <c>{error:{code,message}}</c>
/// shape the SPA's client normalizes into an <c>ApiError</c>.
/// </summary>
public static class ApiErrors
{
    public static Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        // WriteAsJsonAsync uses the configured Http JSON options (camelCase) — see ApiJson.
        return context.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(code, message)));
    }
}
