using FhirAugury.Source.Jira.Configuration;

namespace FhirAugury.Source.Jira.Tests;

public class JiraServiceOptionsTests
{
    [Fact]
    public void GetEffectiveProjects_EmptyList_ReturnsSingleDefaultProject()
    {
        JiraServiceOptions opts = new() { DefaultProject = "FHIR" };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Single(result);
        Assert.Equal("FHIR", result[0].Key);
    }

    [Fact]
    public void GetEffectiveProjects_PopulatedList_ReturnsEnabledOnly()
    {
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "FHIR" },
                new JiraProjectConfig { Key = "FHIR-I", Enabled = false },
                new JiraProjectConfig { Key = "CDA" },
            ]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Equal(2, result.Count);
        Assert.Equal("FHIR", result[0].Key);
        Assert.Equal("CDA", result[1].Key);
    }

    [Fact]
    public void GetEffectiveProjects_AllDisabled_ReturnsEmptyList()
    {
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "FHIR", Enabled = false },
                new JiraProjectConfig { Key = "CDA", Enabled = false },
            ]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Empty(result);
    }

    [Fact]
    public void GetEffectiveProjects_CustomDefaultProject_UsedAsFallback()
    {
        JiraServiceOptions opts = new() { DefaultProject = "CDA" };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Single(result);
        Assert.Equal("CDA", result[0].Key);
    }

    [Fact]
    public void GetEffectiveProjects_PreservesJqlOverride()
    {
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "FHIR", Jql = "project = FHIR AND status = Open" },
            ]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Single(result);
        Assert.Equal("project = FHIR AND status = Open", result[0].Jql);
    }

    [Fact]
    public void GetEffectiveProjects_DefaultJqlIsNull()
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FHIR" }]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Null(result[0].Jql);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_ForDefaults()
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FHIR" }]
        };

        Assert.Empty(opts.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(401)]
    [InlineData(int.MaxValue)]
    public void Validate_FlagsDownloadWindowDaysOutOfRange(int days)
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "BAD", DownloadWindowDays = days }]
        };

        List<string> errors = opts.Validate().ToList();

        Assert.Single(errors);
        Assert.Contains("BAD", errors[0]);
        Assert.Contains("DownloadWindowDays", errors[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(400)]
    public void Validate_AcceptsBoundaryValues(int days)
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FHIR", DownloadWindowDays = days }]
        };

        Assert.Empty(opts.Validate());
    }

    [Fact]
    public void Validate_FlagsFutureStartDate()
    {
        DateOnly tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FUT", StartDate = tomorrow }]
        };

        List<string> errors = opts.Validate().ToList();
        Assert.Single(errors);
        Assert.Contains("StartDate", errors[0]);
        Assert.Contains("FUT", errors[0]);
    }

    [Fact]
    public void Validate_AcceptsTodayAndPastStartDate()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "T", StartDate = today },
                new JiraProjectConfig { Key = "P", StartDate = today.AddYears(-2) },
            ]
        };

        Assert.Empty(opts.Validate());
    }

    [Fact]
    public void Validate_ReportsEveryInvalidProject()
    {
        DateOnly tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "A", DownloadWindowDays = 0 },
                new JiraProjectConfig { Key = "B", StartDate = tomorrow },
                new JiraProjectConfig { Key = "C", DownloadWindowDays = 500, StartDate = tomorrow },
                new JiraProjectConfig { Key = "OK" },
            ]
        };

        List<string> errors = opts.Validate().ToList();
        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("'A'") && e.Contains("DownloadWindowDays"));
        Assert.Contains(errors, e => e.Contains("'B'") && e.Contains("StartDate"));
        Assert.Contains(errors, e => e.Contains("'C'") && e.Contains("DownloadWindowDays"));
        Assert.Contains(errors, e => e.Contains("'C'") && e.Contains("StartDate"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MinValue)]
    public void Validate_FlagsBaselineValueOutOfRange(int baseline)
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "RANK", BaselineValue = baseline }]
        };

        List<string> errors = opts.Validate().ToList();
        Assert.Single(errors);
        Assert.Contains("RANK", errors[0]);
        Assert.Contains("BaselineValue", errors[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void Validate_AcceptsBaselineValueBoundaries(int baseline)
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "OK", BaselineValue = baseline }]
        };

        Assert.Empty(opts.Validate());
    }

    [Fact]
    public void JiraProjectConfig_DefaultBaselineIsFive()
    {
        Assert.Equal(5, new JiraProjectConfig { Key = "X" }.BaselineValue);
    }

    [Fact]
    public void Validate_AcceptsDefaultHl7WorkGroupSourceXmlFilename()
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FHIR" }]
        };

        Assert.Empty(opts.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_FlagsEmptyHl7WorkGroupSourceXmlFilename(string filename)
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FHIR" }],
            Hl7WorkGroupSourceXml = new WorkGroupSourceXmlOptions { Filename = filename }
        };

        List<string> errors = opts.Validate().ToList();
        Assert.Single(errors);
        Assert.Contains("Filename", errors[0]);
    }

    [Theory]
    [InlineData("..\\evil.xml")]
    [InlineData("../evil.xml")]
    [InlineData("nested/file.xml")]
    [InlineData("nested\\file.xml")]
    public void Validate_FlagsHl7WorkGroupSourceXmlFilenameWithSeparators(string filename)
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FHIR" }],
            Hl7WorkGroupSourceXml = new WorkGroupSourceXmlOptions { Filename = filename }
        };

        List<string> errors = opts.Validate().ToList();
        Assert.Single(errors);
        Assert.Contains("Filename", errors[0]);
    }
}
