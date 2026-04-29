using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Processing.Common.Hosting;

public static class ProcessingServiceCollectionExtensions
{
    public static IServiceCollection AddProcessingService(this IServiceCollection services, IConfiguration configuration) => services;
}
