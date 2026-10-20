using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringRetryPolicyTests
{
    [Fact]
    public void Constructor_ParsesConfiguredDelayAndTotalAttemptLimit()
    {
        AuthoringRetryPolicy policy = CreatePolicy("00:02:30", 4);

        Assert.Equal(TimeSpan.FromMinutes(2.5), policy.RetryDelay);
        Assert.Equal(4, policy.MaxAttempts);
        Assert.True(policy.CanStartAnotherAuthoringAttempt(3));
        Assert.False(policy.CanStartAnotherAuthoringAttempt(4));
    }

    [Fact]
    public void GetNextAutomaticRetryAt_AddsFixedDelay()
    {
        AuthoringRetryPolicy policy = CreatePolicy("00:01:00", 3);
        DateTimeOffset completedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.FromHours(-5));

        Assert.Equal(
            completedAt.AddMinutes(1),
            policy.GetNextAutomaticRetryAt(completedAt));
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("not-a-duration", 3)]
    [InlineData("00:00:00", 3)]
    [InlineData("00:01:00", 0)]
    public void Constructor_RejectsInvalidPolicy(string delay, int maxAttempts)
    {
        Assert.Throws<OptionsValidationException>(
            () => CreatePolicy(delay, maxAttempts));
    }

    private static AuthoringRetryPolicy CreatePolicy(
        string retryDelay,
        int maxAttempts)
        => new(Options.Create(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = retryDelay,
            AuthoringMaxAttempts = maxAttempts,
        }));
}
