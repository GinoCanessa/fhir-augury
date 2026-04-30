using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

public class RepoWorkspaceLayoutTests
{
    [Fact]
    public void SafeName_ReplacesSlash()
    {
        Assert.Equal("HL7_fhir", RepoWorkspaceLayout.SafeName("HL7", "fhir"));
        Assert.Equal("HL7_fhir", RepoWorkspaceLayout.SafeName("HL7/fhir"));
    }

    [Fact]
    public void SafeName_FullName_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => RepoWorkspaceLayout.SafeName(""));
    }

    [Fact]
    public void PrimaryClonePath_RoundTripsViaPathCombine()
    {
        string root = Path.Combine(Path.GetTempPath(), "applier-workspace");
        string actual = RepoWorkspaceLayout.PrimaryClonePath(root, "HL7", "fhir");
        Assert.Equal(Path.Combine(root, "clones", "HL7_fhir"), actual);
    }

    [Fact]
    public void BaselinePath_NestsUnderBaselinesSubDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "applier-workspace");
        string actual = RepoWorkspaceLayout.BaselinePath(root, "HL7", "fhir");
        Assert.Equal(Path.Combine(root, "baselines", "HL7_fhir"), actual);
    }

    [Fact]
    public void WorktreePath_IncludesTicketKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "applier-workspace");
        string actual = RepoWorkspaceLayout.WorktreePath(root, "HL7", "fhir", "FHIR-123");
        Assert.Equal(Path.Combine(root, "worktrees", "HL7_fhir", "FHIR-123"), actual);
    }

    [Fact]
    public void OutputPath_TicketKeyBeforeRepo()
    {
        string root = Path.Combine(Path.GetTempPath(), "applier-out");
        string actual = RepoWorkspaceLayout.OutputPath(root, "FHIR-123", "HL7", "fhir");
        Assert.Equal(Path.Combine(root, "FHIR-123", "HL7_fhir"), actual);
    }
}
