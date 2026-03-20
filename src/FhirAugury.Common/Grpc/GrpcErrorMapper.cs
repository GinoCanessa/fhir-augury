using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Grpc;

/// <summary>
/// Maps exceptions to gRPC status codes for consistent error handling.
/// </summary>
public static class GrpcErrorMapper
{
    /// <summary>
    /// Wraps an async operation with standard gRPC error mapping.
    /// </summary>
    public static async Task<T> HandleAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        string rpcName)
    {
        try
        {
            return await operation();
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid argument in {RpcName}", rpcName);
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogDebug(ex, "Not found in {RpcName}", rpcName);
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Operation was cancelled"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Internal error in {RpcName}", rpcName);
            throw new RpcException(new Status(StatusCode.Internal, "An internal error occurred"));
        }
    }
}
