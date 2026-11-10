using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Features.Ecommerce;

namespace SupermarketErp.Api.Infrastructure;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (InvalidCredentialsException ex)
        {
            await WriteError(context, StatusCodes.Status401Unauthorized, ex.Message);
        }
        catch (LoginRateLimitExceededException ex)
        {
            context.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WriteError(context, StatusCodes.Status429TooManyRequests, ex.Message);
        }
        catch (RetryableWebhookException ex)
        {
            await WriteError(context, StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await WriteError(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627 or 547)
        {
            await WriteError(context, StatusCodes.Status409Conflict, "The requested change conflicts with existing data.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled API exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteError(context, StatusCodes.Status500InternalServerError, "Une erreur inattendue est survenue");
        }
    }

    private static async Task WriteError(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        var reason = status switch
        {
            StatusCodes.Status400BadRequest => "Bad Request",
            StatusCodes.Status401Unauthorized => "Unauthorized",
            StatusCodes.Status409Conflict => "Conflict",
            StatusCodes.Status429TooManyRequests => "Too Many Requests",
            StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
            _ => "Internal Server Error"
        };
        await context.Response.WriteAsJsonAsync(new
        {
            timestamp = JavaInstantFormat.Format(DateTimeOffset.UtcNow),
            status,
            error = reason,
            message
        });
    }
}
