using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Configuration;

public class ApplierOptionsTests
{
    [Fact]
    public void Validate_ReturnsErrorsWhenReposEmpty()
    {
        ApplierOptions options = new()
        {
            Repos = [],
        };

        List<string> errors = options.Validate().ToList();

        Assert.Contains(errors, e => e.Contains("Repos must include at least one entry"));
    }

    [Fact]
    public void Validate_ReturnsErrorWhenRepoMissingBuildCommand()
    {
        ApplierOptions options = new()
        {
            Repos =
            [
                new ApplierRepoOptions
                {
                    Owner = "HL7",
                    Name = "fhir",
                    BuildCommand = "",
                },
            ],
        };

        List<string> errors = options.Validate().ToList();

        Assert.Contains(errors, e => e.Contains("BuildCommand must be non-empty"));
    }

    [Fact]
    public void Validate_ReturnsErrorOnDuplicateRepo()
    {
        ApplierOptions options = new()
        {
            Repos =
            [
                new ApplierRepoOptions { Owner = "HL7", Name = "fhir", BuildCommand = "x" },
                new ApplierRepoOptions { Owner = "hl7", Name = "FHIR", BuildCommand = "x" },
            ],
        };

        List<string> errors = options.Validate().ToList();

        Assert.Contains(errors, e => e.Contains("duplicate entry"));
    }

    [Fact]
    public void Validate_ReturnsNoErrorsForMinimalValidConfig()
    {
        ApplierOptions options = new()
        {
            Repos =
            [
                new ApplierRepoOptions
                {
                    Owner = "HL7",
                    Name = "fhir",
                    BuildCommand = "_genonce.sh",
                },
            ],
        };

        List<string> errors = options.Validate().ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsInvalidBaselineSyncSchedule()
    {
        ApplierOptions options = new()
        {
            BaselineSyncSchedule = "not-a-timespan",
            Repos =
            [
                new ApplierRepoOptions { Owner = "HL7", Name = "fhir", BuildCommand = "x" },
            ],
        };

        List<string> errors = options.Validate().ToList();

        Assert.Contains(errors, e => e.Contains("BaselineSyncSchedule"));
    }
}
