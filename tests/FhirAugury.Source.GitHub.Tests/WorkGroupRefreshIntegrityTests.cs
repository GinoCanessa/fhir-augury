using FhirAugury.Common.WorkGroups;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Unit coverage for <see cref="WorkGroupRefreshIntegrity"/>, the extracted
/// "configured-but-zero work-group refresh" guard used by
/// <c>GitHubIngestionPipeline.EnsureWorkGroupsRefreshedAsync</c>.
/// </summary>
public class WorkGroupRefreshIntegrityTests
{
    [Fact]
    public void EnsureWorkGroupsRefreshed_Throws_When_Configured_And_Zero_Rows()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            Filename = "CodeSystem-hl7-work-group.xml",
            Url = "https://terminology.hl7.org/en/CodeSystem-hl7-work-group.xml",
        };

        IngestionDataIntegrityException ex = Assert.Throws<IngestionDataIntegrityException>(
            () => WorkGroupRefreshIntegrity.ThrowIfConfiguredButEmpty(cfg, total: 0, xmlPath: null));

        Assert.Contains("Hl7WorkGroupSourceXml", ex.Message);
    }

    [Fact]
    public void EnsureWorkGroupsRefreshed_Throws_When_LocalFile_Configured_And_Zero_Rows()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            LocalFile = @"C:\some\path\CodeSystem-hl7-work-group.xml",
        };

        Assert.Throws<IngestionDataIntegrityException>(
            () => WorkGroupRefreshIntegrity.ThrowIfConfiguredButEmpty(cfg, total: 0, xmlPath: null));
    }

    [Fact]
    public void EnsureWorkGroupsRefreshed_Silent_When_Unconfigured()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            LocalFile = null,
            Url = null,
        };

        WorkGroupRefreshIntegrity.ThrowIfConfiguredButEmpty(cfg, total: 0, xmlPath: null);
        Assert.False(WorkGroupRefreshIntegrity.IsConfigured(cfg));
    }

    [Fact]
    public void EnsureWorkGroupsRefreshed_Silent_When_Configured_And_NonZero_Rows()
    {
        WorkGroupSourceXmlOptions cfg = new()
        {
            Url = "https://terminology.hl7.org/en/CodeSystem-hl7-work-group.xml",
        };

        WorkGroupRefreshIntegrity.ThrowIfConfiguredButEmpty(cfg, total: 62, xmlPath: "/tmp/wg.xml");
        Assert.True(WorkGroupRefreshIntegrity.IsConfigured(cfg));
    }
}
