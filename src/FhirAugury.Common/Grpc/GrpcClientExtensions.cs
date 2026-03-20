using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Common.Grpc;

/// <summary>
/// Extension methods for registering gRPC clients in DI.
/// </summary>
public static class GrpcClientExtensions
{
    /// <summary>
    /// Registers a gRPC client for a source service, reading its address from configuration.
    /// </summary>
    public static IServiceCollection AddSourceServiceClient<TClient>(
        this IServiceCollection services,
        string sourceName,
        IConfiguration config)
        where TClient : class
    {
        var address = config[$"Services:{sourceName}:GrpcAddress"]
            ?? throw new InvalidOperationException($"Missing gRPC address for source '{sourceName}' in configuration.");

        services.AddSingleton(sp =>
        {
            var channel = GrpcChannel.ForAddress(address);
            return (TClient)Activator.CreateInstance(typeof(TClient), channel)!;
        });

        return services;
    }
}
