using FhirAugury.Processing.Common.Queue;

namespace FhirAugury.Processing.Common.Tests.Queue;

public class ProcessingStatusValuesTests
{
    [Fact]
    public void Stale_HasExpectedLiteral()
    {
        Assert.Equal("stale", ProcessingStatusValues.Stale);
    }

    [Fact]
    public void Stale_IsDistinctFromOtherStatuses()
    {
        HashSet<string> all =
        [
            ProcessingStatusValues.InProgress,
            ProcessingStatusValues.Complete,
            ProcessingStatusValues.Error,
            ProcessingStatusValues.Stale,
        ];

        Assert.Equal(4, all.Count);
    }
}
