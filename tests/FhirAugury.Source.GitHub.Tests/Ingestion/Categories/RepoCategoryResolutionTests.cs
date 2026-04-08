using FhirAugury.Source.GitHub.Configuration;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Categories;

public class RepoCategoryResolutionTests
{
    [Fact]
    public void GetAllRepositories_PairsReposWithCategories()
    {
        GitHubServiceOptions options = new()
        {
            FhirCoreRepositories = ["HL7/fhir"],
            UtgRepositories = ["HL7/UTG"],
            FhirExtensionsPackRepositories = ["HL7/fhir-extensions"],
            IncubatorRepositories = ["HL7/admin-incubator"],
            IgRepositories = ["test/ig"],
        };

        IReadOnlyList<(string Name, RepoCategory Category)> repos = options.GetAllRepositories();

        Assert.Equal(5, repos.Count);
        Assert.Contains(repos, r => r.Name == "HL7/fhir" && r.Category == RepoCategory.FhirCore);
        Assert.Contains(repos, r => r.Name == "HL7/UTG" && r.Category == RepoCategory.Utg);
        Assert.Contains(repos, r => r.Name == "HL7/fhir-extensions" && r.Category == RepoCategory.FhirExtensionsPack);
        Assert.Contains(repos, r => r.Name == "HL7/admin-incubator" && r.Category == RepoCategory.Incubator);
        Assert.Contains(repos, r => r.Name == "test/ig" && r.Category == RepoCategory.Ig);
    }

    [Fact]
    public void GetAllRepositories_EmptyCategories()
    {
        GitHubServiceOptions options = new()
        {
            FhirCoreRepositories = [],
            UtgRepositories = [],
            FhirExtensionsPackRepositories = [],
            IncubatorRepositories = [],
            IgRepositories = [],
        };

        IReadOnlyList<(string Name, RepoCategory Category)> repos = options.GetAllRepositories();

        Assert.Empty(repos);
    }

    [Fact]
    public void GetAllRepositories_MultipleReposPerCategory()
    {
        GitHubServiceOptions options = new()
        {
            FhirCoreRepositories = ["HL7/fhir"],
            UtgRepositories = [],
            FhirExtensionsPackRepositories = [],
            IncubatorRepositories = ["HL7/admin-incubator", "HL7/oo-incubator", "HL7/cg-incubator"],
            IgRepositories = [],
        };

        IReadOnlyList<(string Name, RepoCategory Category)> repos = options.GetAllRepositories();

        Assert.Equal(4, repos.Count);
        Assert.Equal(3, repos.Count(r => r.Category == RepoCategory.Incubator));
    }

    [Fact]
    public void GetAllRepositoryNames_ReturnsAllNames()
    {
        GitHubServiceOptions options = new()
        {
            FhirCoreRepositories = ["HL7/fhir"],
            UtgRepositories = ["HL7/UTG"],
            FhirExtensionsPackRepositories = [],
            IncubatorRepositories = ["HL7/admin-incubator"],
            IgRepositories = [],
        };

        List<string> names = options.GetAllRepositoryNames();

        Assert.Equal(3, names.Count);
        Assert.Contains("HL7/fhir", names);
        Assert.Contains("HL7/UTG", names);
        Assert.Contains("HL7/admin-incubator", names);
    }
}
