using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Processing.Common.Hosting;

public static class ProcessingServiceCollectionExtensions
{
    public static IServiceCollection AddProcessingService<TItem, TStore, THandler>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TStore : class, IProcessingWorkItemStore<TItem>
        where THandler : class, IProcessingWorkItemHandler<TItem>
    {
        services.AddOptions<ProcessingServiceOptions>()
            .Bind(configuration.GetSection(ProcessingServiceOptions.SectionName))
            .Validate(options => !options.Validate().Any(), "Processing configuration is invalid.");

        services.AddSingleton<ProcessingLifecycleService>();
        services.AddSingleton<IProcessingWorkItemStore<TItem>, TStore>();
        services.AddSingleton<IProcessingWorkItemHandler<TItem>, THandler>();
        services.AddSingleton<ProcessingQueueRunner<TItem>>();
        services.AddHostedService<ProcessingHostedService<TItem>>();
        return services;
    }
}
