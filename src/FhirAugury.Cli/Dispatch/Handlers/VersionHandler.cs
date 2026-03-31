using System.Reflection;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class VersionHandler
{
    public static Task<object> HandleAsync()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";

        return Task.FromResult<object>(new { version });
    }
}
