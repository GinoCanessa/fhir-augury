using System.Globalization;
using FhirAugury.Processing.Common.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Authoring;

public sealed class AuthoringRetryPolicy
{
    public AuthoringRetryPolicy(IOptions<ProcessingServiceOptions> optionsAccessor)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ProcessingServiceOptions options = optionsAccessor.Value;
        if (!TimeSpan.TryParse(
                options.AuthoringRetryDelay,
                CultureInfo.InvariantCulture,
                out TimeSpan retryDelay) ||
            retryDelay <= TimeSpan.Zero)
        {
            throw new OptionsValidationException(
                ProcessingServiceOptions.SectionName,
                typeof(ProcessingServiceOptions),
                ["AuthoringRetryDelay must be a positive TimeSpan string."]);
        }
        if (options.AuthoringMaxAttempts < 1)
        {
            throw new OptionsValidationException(
                ProcessingServiceOptions.SectionName,
                typeof(ProcessingServiceOptions),
                ["AuthoringMaxAttempts must be greater than or equal to 1."]);
        }

        RetryDelay = retryDelay;
        MaxAttempts = options.AuthoringMaxAttempts;
    }

    public TimeSpan RetryDelay { get; }
    public int MaxAttempts { get; }

    public bool CanStartAnotherAuthoringAttempt(int attemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        return attemptCount < MaxAttempts;
    }

    public DateTimeOffset GetNextAutomaticRetryAt(DateTimeOffset completedAt)
        => completedAt.Add(RetryDelay);

    internal static AuthoringRetryPolicy CreateDefault()
        => new(Options.Create(new ProcessingServiceOptions()));
}
