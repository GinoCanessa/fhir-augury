using FhirAugury.Processing.Jira.Common.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Processing.Jira.Common.Hosting;

public static class JiraProcessingServiceCollectionExtensions
{
    public static IServiceCollection AddJiraProcessing(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<JiraProcessingOptions>? configure = null)
    {
        services.AddOptions<JiraProcessingOptions>()
            .Bind(configuration.GetSection(JiraProcessingOptions.SectionName))
            .Configure(options => configure?.Invoke(options))
            .Validate(options => !options.Validate().Any(), "Processing:Jira configuration is invalid.");

        return services;
    }
}
