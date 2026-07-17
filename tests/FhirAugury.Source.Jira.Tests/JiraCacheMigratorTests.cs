using FhirAugury.Source.Jira.Cache;

namespace FhirAugury.Source.Jira.Tests;

public class JiraCacheMigratorTests
{
    [Fact]
    public void IsLegacyKey_XmlPrefix_True()
    {
        Assert.True(JiraCacheMigrator.IsLegacyKey("xml/DayOf_2026-02-24-000.xml"));
    }

    [Fact]
    public void IsLegacyKey_JsonPrefix_True()
    {
        Assert.True(JiraCacheMigrator.IsLegacyKey("json/DayOf_2026-02-24-000.json"));
    }

    [Fact]
    public void IsLegacyKey_ProjectPrefixed_False()
    {
        Assert.False(JiraCacheMigrator.IsLegacyKey("FHIR/xml/DayOf_2026-02-24-000.xml"));
    }

    [Fact]
    public void IsLegacyKey_MetadataFile_False()
    {
        Assert.False(JiraCacheMigrator.IsLegacyKey("_meta_jira.json"));
    }

    [Fact]
    public void IsLegacyKey_ProjectJsonPrefixed_False()
    {
        Assert.False(JiraCacheMigrator.IsLegacyKey("FHIR-I/json/DayOf_2026-04-01-000.json"));
    }
}
