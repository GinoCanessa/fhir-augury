using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Common.OpenApi;

public static class AuguryOpenApiServiceCollectionExtensions
{
    public const string ConfigurationSectionPath = "Augury:OpenApi";
    public const string DocumentName = "v1";

    public static IServiceCollection AddAuguryOpenApi(
        this IServiceCollection services,
        Action<AuguryOpenApiOptions>? configure = null)
    {
        AuguryOpenApiOptions options = new();

        IConfiguration? config = services
            .BuildServiceProvider(validateScopes: false)
            .GetService<IConfiguration>();
        config?.GetSection(ConfigurationSectionPath).Bind(options);

        configure?.Invoke(options);

        PopulateMissingFromAssembly(options, Assembly.GetCallingAssembly());

        services.AddSingleton(options);

        services.AddOpenApi(DocumentName, docOptions =>
        {
            docOptions.AddDocumentTransformer((document, context, ct) =>
            {
                if (!string.IsNullOrEmpty(options.Title))
                {
                    document.Info.Title = options.Title;
                }
                if (!string.IsNullOrEmpty(options.Description))
                {
                    document.Info.Description = options.Description;
                }
                if (!string.IsNullOrEmpty(options.Version))
                {
                    document.Info.Version = options.Version;
                }
                return Task.CompletedTask;
            });

            docOptions.AddDocumentTransformer(new AuguryOpenApiDocumentTransformer(options));
        });

        return services;
    }

    private static void PopulateMissingFromAssembly(AuguryOpenApiOptions options, Assembly assembly)
    {
        if (string.IsNullOrEmpty(options.Title))
        {
            options.Title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
                ?? assembly.GetName().Name
                ?? "FHIR Augury Service";
        }

        if (string.IsNullOrEmpty(options.Version))
        {
            options.Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        if (string.IsNullOrEmpty(options.Description))
        {
            options.Description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
                ?? string.Empty;
        }
    }
}
