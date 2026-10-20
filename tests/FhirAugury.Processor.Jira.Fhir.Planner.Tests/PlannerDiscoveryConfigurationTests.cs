using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Hosting;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerDiscoveryConfigurationTests
{
    private const string NestedOverrideJson = """
        {
          "Processing": {
            "DatabasePath": "./synthetic/planner-discovery-configuration.db",
            "Jira": {
              "SpecificationsToInclude": ["FHIR\u0020R5\u0020Subscriptions\u0020Backport (FHIR)"],
              "LabelsToExclude": ["AwaitingMerge"]
            }
          }
        }
        """;

    private const string ShallowOverrideJson = """
        {
          "Processing": {
            "DatabasePath": "./synthetic/planner-discovery-configuration.db",
            "SpecificationsToInclude": ["FHIR\u0020R5\u0020Subscriptions\u0020Backport (FHIR)"],
            "LabelsToExclude": ["AwaitingMerge"]
          }
        }
        """;

    [Fact]
    public void ShippedConfiguration_BindsOrdinarySpaceBackportSpecification()
    {
        using ConfigurationRoot configuration = CreateConfiguration();
        using ServiceProvider provider = CreateServices(configuration);
        JiraProcessingOptions options = provider.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
        ResolvedJiraProcessingFilters filters = provider.GetRequiredService<JiraProcessingFilterResolver>().Resolve(options);

        Assert.NotNull(options.SpecificationsToInclude);
        string boundSpecification = Assert.Single(options.SpecificationsToInclude);
        Assert.Equal("FHIR\u0020R5\u0020Subscriptions\u0020Backport (FHIR)", boundSpecification, StringComparer.Ordinal);
        Assert.DoesNotContain("\u00A0", boundSpecification, StringComparison.Ordinal);
        Assert.NotNull(filters.Specifications);
        string resolvedSpecification = Assert.Single(filters.Specifications);
        Assert.Equal("FHIR\u0020R5\u0020Subscriptions\u0020Backport (FHIR)", resolvedSpecification, StringComparer.Ordinal);
        Assert.DoesNotContain("\u00A0", resolvedSpecification, StringComparison.Ordinal);
        Assert.Null(options.LabelsToInclude);
        Assert.Null(options.LabelsToExclude);
        Assert.Null(filters.LabelsToInclude);
        Assert.Null(filters.LabelsToExclude);
        Assert.False(filters.HasLabelTextFilters);
    }

    [Fact]
    public void NestedOverride_ReplacesInheritedSpecificationAndBindsExclusion()
    {
        using ConfigurationRoot configuration = CreateConfiguration(
            NestedOverrideJson,
            useDefectiveInheritedSpecification: true);
        using ServiceProvider provider = CreateServices(configuration);
        JiraProcessingOptions options = provider.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
        ResolvedJiraProcessingFilters filters = provider.GetRequiredService<JiraProcessingFilterResolver>().Resolve(options);

        Assert.NotNull(options.SpecificationsToInclude);
        Assert.Equal(
            "FHIR\u0020R5\u0020Subscriptions\u0020Backport (FHIR)",
            Assert.Single(options.SpecificationsToInclude),
            StringComparer.Ordinal);
        Assert.NotNull(filters.Specifications);
        Assert.Equal(
            "FHIR\u0020R5\u0020Subscriptions\u0020Backport (FHIR)",
            Assert.Single(filters.Specifications),
            StringComparer.Ordinal);
        Assert.NotNull(options.LabelsToExclude);
        Assert.Equal("AwaitingMerge", Assert.Single(options.LabelsToExclude), StringComparer.Ordinal);
        Assert.NotNull(filters.LabelsToExclude);
        Assert.Equal("AwaitingMerge", Assert.Single(filters.LabelsToExclude), StringComparer.Ordinal);
        Assert.True(filters.HasLabelTextFilters);
        Assert.Equal(
            "./synthetic/planner-discovery-configuration.db",
            provider.GetRequiredService<IOptions<ProcessingServiceOptions>>().Value.DatabasePath,
            StringComparer.Ordinal);
    }

    [Fact]
    public void ShallowOverride_LeavesInheritedSpecificationAndExclusionInactive()
    {
        using ConfigurationRoot configuration = CreateConfiguration(
            ShallowOverrideJson,
            useDefectiveInheritedSpecification: true);
        using ServiceProvider provider = CreateServices(configuration);
        JiraProcessingOptions options = provider.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
        ResolvedJiraProcessingFilters filters = provider.GetRequiredService<JiraProcessingFilterResolver>().Resolve(options);

        Assert.NotNull(options.SpecificationsToInclude);
        Assert.Equal(
            "FHIR\u00A0R5\u00A0Subscriptions\u00A0Backport (FHIR)",
            Assert.Single(options.SpecificationsToInclude),
            StringComparer.Ordinal);
        Assert.NotNull(filters.Specifications);
        Assert.Equal(
            "FHIR\u00A0R5\u00A0Subscriptions\u00A0Backport (FHIR)",
            Assert.Single(filters.Specifications),
            StringComparer.Ordinal);
        Assert.Null(options.LabelsToInclude);
        Assert.Null(options.LabelsToExclude);
        Assert.Null(filters.LabelsToInclude);
        Assert.Null(filters.LabelsToExclude);
        Assert.False(filters.HasLabelTextFilters);
        Assert.Equal(
            "./synthetic/planner-discovery-configuration.db",
            provider.GetRequiredService<IOptions<ProcessingServiceOptions>>().Value.DatabasePath,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task RepairedConfiguration_RunBackedDiscoveryPostsCompleteSelection()
    {
        using ConfigurationRoot configuration = CreateConfiguration(NestedOverrideJson);
        using ServiceProvider provider = CreateServices(configuration);
        IOptions<JiraProcessingOptions> options = provider.GetRequiredService<IOptions<JiraProcessingOptions>>();
        ResolvedJiraProcessingFilters filters = provider.GetRequiredService<JiraProcessingFilterResolver>().Resolve(options.Value);
        JiraLocalProcessingRequestFactory requestFactory = provider.GetRequiredService<JiraLocalProcessingRequestFactory>();
        using CapturingHandler handler = new();
        using HttpClient httpClient = new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(options.Value.JiraSourceAddress),
        };
        DirectJiraTicketDiscoveryClient client = new(httpClient, options, requestFactory);

        JiraTicketDiscoveryBatch batch = await client.ListTicketsForModeWithProvenanceAsync(
            filters,
            runBacked: true,
            CancellationToken.None);

        Assert.Empty(batch.Tickets);
        Assert.NotNull(batch.Provenance);
        Assert.True(batch.Provenance.IsStable);
        (HttpMethod method, Uri uri, string json) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/v1/local-processing/selection-tickets?type=fhir", uri.PathAndQuery, StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            "Resolved - change required",
            Assert.Single(root.GetProperty("statuses").EnumerateArray()).GetString(),
            StringComparer.Ordinal);
        Assert.Equal(
            "FHIR",
            Assert.Single(root.GetProperty("projects").EnumerateArray()).GetString(),
            StringComparer.Ordinal);
        Assert.Equal(
            "FHIR\u0020R5\u0020Subscriptions\u0020Backport (FHIR)",
            Assert.Single(root.GetProperty("specifications").EnumerateArray()).GetString(),
            StringComparer.Ordinal);
        Assert.Equal(
            "AwaitingMerge",
            Assert.Single(root.GetProperty("labelText").GetProperty("excludes").EnumerateArray()).GetString(),
            StringComparer.Ordinal);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("processedLocally").ValueKind);
        Assert.Equal(500, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
    }

    private static ConfigurationRoot CreateConfiguration(
        string? localJson = null,
        bool useDefectiveInheritedSpecification = false)
    {
        ConfigurationBuilder builder = new();
        builder.AddJsonFile(FindPlannerSettingsPath(), optional: false, reloadOnChange: false);
        if (useDefectiveInheritedSpecification)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Processing:Jira:SpecificationsToInclude:0"] = "FHIR\u00A0R5\u00A0Subscriptions\u00A0Backport (FHIR)",
            });
        }

        if (localJson is null)
        {
            return (ConfigurationRoot)builder.Build();
        }

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(localJson));
        builder.AddJsonStream(stream);
        return (ConfigurationRoot)builder.Build();
    }

    private static ServiceProvider CreateServices(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddJiraProcessing(
            configuration,
            PlannerJiraProcessingDefaults.Apply,
            new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Resolved - change required"] });
        return services.BuildServiceProvider();
    }

    private static string FindPlannerSettingsPath()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "FhirAugury.Processor.Jira.Fhir.Planner",
                "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find src/FhirAugury.Processor.Jira.Fhir.Planner/appsettings.json from the test working directory.");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Json)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            Assert.NotNull(request.Content);
            string json = await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri, json));

            JiraLocalProcessingListResponse payload = new([], 500, 0, 0)
            {
                Provenance = new SourceReadProvenance
                {
                    Source = "jira",
                    ContentRevision = 7,
                    IsStable = true,
                    ProjectLastSuccessfulRefreshAt = new Dictionary<string, DateTimeOffset?>
                    {
                        ["FHIR"] = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    },
                },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload),
            };
        }
    }
}
