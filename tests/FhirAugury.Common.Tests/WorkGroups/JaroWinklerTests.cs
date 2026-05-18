using FhirAugury.Common.WorkGroups;

namespace FhirAugury.Common.Tests.WorkGroups;

public class JaroWinklerTests
{
    [Fact]
    public void IdenticalStrings_ReturnsOne()
    {
        Assert.Equal(1.0, JaroWinkler.Compute("OrdersAndObservations", "OrdersAndObservations"));
    }

    [Fact]
    public void BothEmpty_ReturnsOne()
    {
        Assert.Equal(1.0, JaroWinkler.Compute(string.Empty, string.Empty));
    }

    [Theory]
    [InlineData("", "anything")]
    [InlineData("anything", "")]
    public void OneEmpty_ReturnsZero(string a, string b)
    {
        Assert.Equal(0.0, JaroWinkler.Compute(a, b));
    }

    [Fact]
    public void DisjointStrings_ReturnsZero()
    {
        Assert.Equal(0.0, JaroWinkler.Compute("abc", "xyz"));
    }

    [Fact]
    public void MarthaMarhta_MatchesClassicWinklerExample()
    {
        double score = JaroWinkler.Compute("MARTHA", "MARHTA");
        Assert.InRange(score, 0.960, 0.962);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        Assert.Equal(
            JaroWinkler.Compute("MARTHA", "MARHTA"),
            JaroWinkler.Compute("martha", "marhta"),
            5);
    }

    [Fact]
    public void DwayneDuane_MatchesClassicWinklerExample()
    {
        double score = JaroWinkler.Compute("DWAYNE", "DUANE");
        Assert.InRange(score, 0.83, 0.85);
    }
}
