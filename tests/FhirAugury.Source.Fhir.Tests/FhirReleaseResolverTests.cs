using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class FhirReleaseResolverTests : IClassFixture<FhirSpecFixture>
{
    private readonly FhirSpecFixture _fixture;

    public FhirReleaseResolverTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirReleaseResolver Resolver(string? defaultRelease = null)
        => new(_fixture.CreateDatabase(), defaultRelease);

    [Theory]
    [InlineData("R5", 5)]
    [InlineData("5.0", 5)]
    [InlineData("hl7.fhir.r5.core", 5)]
    [InlineData("5.0.0", 5)]
    [InlineData("R4", 4)]
    [InlineData("4.0", 4)]
    [InlineData("DSTU2", 1)]
    [InlineData("R2", 1)]
    [InlineData("R6", 6)]
    [InlineData("6.0", 6)]
    [InlineData("6.0.0-ballot4", 6)]
    [InlineData("hl7.fhir.r6.core", 6)]
    public void ResolvePackageKey_KnownTokens_ResolveToExpectedKey(string token, int expectedKey)
    {
        int? key = Resolver().ResolvePackageKey(token, out string? error);

        Assert.Null(error);
        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void ResolvePackageKey_IsCaseInsensitiveForAliases()
    {
        int? key = Resolver().ResolvePackageKey("r5", out string? error);

        Assert.Null(error);
        Assert.Equal(5, key);
    }

    [Fact]
    public void ResolvePackageKey_UnknownToken_ReturnsNullWithError()
    {
        int? key = Resolver().ResolvePackageKey("R99", out string? error);

        Assert.Null(key);
        Assert.NotNull(error);
        Assert.Contains("R99", error);
        Assert.Contains("Available releases", error);
    }

    [Fact]
    public void ResolveDefault_NoConfiguredDefault_PicksLatestStable()
    {
        // R6 is a prerelease (6.0.0-ballot4); R5 is the newest stable release.
        int? key = Resolver().ResolveDefaultPackageKey(out string? error);

        Assert.Null(error);
        Assert.Equal(5, key);
    }

    [Fact]
    public void ResolveDefault_ConfiguredDefault_UsesIt()
    {
        int? key = Resolver("R4").ResolveDefaultPackageKey(out string? error);

        Assert.Null(error);
        Assert.Equal(4, key);
    }

    [Fact]
    public void TryResolve_NullToken_ResolvesDefaultAndEchoesRelease()
    {
        bool ok = Resolver().TryResolve(null, out int key, out ReleaseInfo? info, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(5, key);
        Assert.NotNull(info);
        Assert.Equal("R5", info!.ShortName);
        Assert.Equal("hl7.fhir.r5.core", info.PackageId);
    }

    [Fact]
    public void TryResolve_UnknownToken_Fails()
    {
        bool ok = Resolver().TryResolve("nope", out _, out ReleaseInfo? info, out string? error);

        Assert.False(ok);
        Assert.Null(info);
        Assert.NotNull(error);
    }

    [Fact]
    public void GetReleaseInfo_MapsAllColumns()
    {
        ReleaseInfo? info = Resolver().GetReleaseInfo(6);

        Assert.NotNull(info);
        Assert.Equal("R6", info!.ShortName);
        Assert.Equal("6.0", info.FhirVersion);
        Assert.Equal("6.0.0-ballot4", info.PackageVersion);
        Assert.Equal("hl7.fhir.r6.core", info.PackageId);
    }

    [Fact]
    public void ResolvePackageKey_MissingDatabase_ReturnsError()
    {
        FhirSpecDatabase missing = new(
            Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.db"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FhirSpecDatabase>.Instance);
        FhirReleaseResolver resolver = new(missing, defaultRelease: null);

        int? key = resolver.ResolvePackageKey("R5", out string? error);

        Assert.Null(key);
        Assert.NotNull(error);
    }
}
