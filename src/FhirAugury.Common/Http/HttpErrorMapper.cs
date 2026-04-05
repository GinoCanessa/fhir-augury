using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Http;

/// <summary>
/// Maps exceptions to HTTP status codes for consistent error handling in Minimal API endpoints.
/// </summary>
public static class HttpErrorMapper
{
    /// <summary>
    /// Wraps an async operation with standard HTTP error mapping.
    /// </summary>
    public static async Task<IResult> HandleAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        string endpointName)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid argument in {EndpointName}", endpointName);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogDebug(ex, "Not found in {EndpointName}", endpointName);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Internal error in {EndpointName}", endpointName);
            return Results.StatusCode(500);
        }
    }

    /// <summary>
    /// Wraps an async operation (no return value) with standard HTTP error mapping.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        Func<Task> operation,
        ILogger logger,
        string endpointName)
    {
        try
        {
            await operation();
            return Results.Ok();
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid argument in {EndpointName}", endpointName);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogDebug(ex, "Not found in {EndpointName}", endpointName);
            return Results.NotFound(new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Internal error in {EndpointName}", endpointName);
            return Results.StatusCode(500);
        }
    }
}
