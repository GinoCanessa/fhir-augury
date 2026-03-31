using Grpc.Core;

namespace FhirAugury.Common.Grpc;

/// <summary>
/// Extension methods for classifying gRPC exceptions.
/// </summary>
public static class RpcExceptionExtensions
{
    /// <summary>
    /// Returns true if the exception is a transient gRPC error (Unavailable or Cancelled)
    /// that typically occurs during service startup or shutdown.
    /// When true, <paramref name="statusDescription"/> contains the status code name.
    /// </summary>
    public static bool IsTransientGrpcError(this Exception ex, out string statusDescription)
    {
        if (ex is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.Cancelled } rpcEx)
        {
            statusDescription = rpcEx.StatusCode.ToString();
            return true;
        }

        statusDescription = "";
        return false;
    }
}
