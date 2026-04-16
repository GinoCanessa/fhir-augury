using FhirAugury.Source.Jira.Cache;

namespace FhirAugury.Source.Jira.Tests;

public class JiraCacheLayoutTests
{
    [Fact]
    public void ProjectXmlKey_BuildsCorrectPath()
    {
        string result = JiraCacheLayout.ProjectXmlKey("FHIR", "DayOf_2026-02-24-000.xml");
        Assert.Equal("FHIR/xml/DayOf_2026-02-24-000.xml", result);
    }

    [Fact]
    public void ProjectJsonKey_BuildsCorrectPath()
    {
        string result = JiraCacheLayout.ProjectJsonKey("FHIR-I", "DayOf_2026-02-24-000.json");
        Assert.Equal("FHIR-I/json/DayOf_2026-02-24-000.json", result);
    }

    [Fact]
    public void ProjectSubPath_ReturnsProjectKey()
    {
        string result = JiraCacheLayout.ProjectSubPath("CDA");
        Assert.Equal("CDA", result);
    }

    [Fact]
    public void ProjectXmlSubPath_BuildsCorrectPrefix()
    {
        string result = JiraCacheLayout.ProjectXmlSubPath("V2");
        Assert.Equal("V2/xml", result);
    }

    [Fact]
    public void ProjectJsonSubPath_BuildsCorrectPrefix()
    {
        string result = JiraCacheLayout.ProjectJsonSubPath("UP");
        Assert.Equal("UP/json", result);
    }

    [Fact]
    public void LegacyXmlKey_StillWorks()
    {
        string result = JiraCacheLayout.XmlKey("DayOf_2026-02-24-000.xml");
        Assert.Equal("xml/DayOf_2026-02-24-000.xml", result);
    }

    [Fact]
    public void LegacyJsonKey_StillWorks()
    {
        string result = JiraCacheLayout.JsonKey("DayOf_2026-02-24-000.json");
        Assert.Equal("json/DayOf_2026-02-24-000.json", result);
    }
}
