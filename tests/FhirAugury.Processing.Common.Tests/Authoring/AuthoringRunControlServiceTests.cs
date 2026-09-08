using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringRunControlServiceTests
{
    [Fact]
    public async Task GetStatusAsync_ReportsRetryAndSupersedeMetadata()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:02:00",
            AuthoringMaxAttempts = 3,
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset failedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(
                run.Id,
                item.Id,
                failedAt.AddMinutes(-1)));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure",
            failedAt);
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);

        Assert.Equal(1, status.Run.FailedItems);
        Assert.Equal(1, status.Run.RetryableErrorItems);
        Assert.Equal(0, status.Run.SupersededItems);
        AuthoringRunItemStatus itemStatus = Assert.Single(status.Items);
        Assert.Equal(2, itemStatus.AttemptsRemaining);
        Assert.Equal(failedAt.AddMinutes(2), itemStatus.NextAutomaticRetryAt);
    }

    [Fact]
    public async Task SupersedeItemAsync_RequiresMatchingProcessorAndReason()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure");
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SupersedeItemAsync(
                "other",
                run.Id,
                item.Id,
                new AuthoringItemSupersedeRequest("not actionable")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SupersedeItemAsync(
                "test",
                run.Id,
                item.Id,
                new AuthoringItemSupersedeRequest(" ")));

        AuthoringItemSupersedeResult result =
            await service.SupersedeItemAsync(
                "test",
                run.Id,
                item.Id,
                new AuthoringItemSupersedeRequest("not actionable"));
        Assert.Equal(AuthoringStatusValues.Items.Superseded, result.Status);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);
        Assert.Equal(1, status.Run.FailedItems);
        Assert.Equal(0, status.Run.RetryableErrorItems);
        Assert.Equal(1, status.Run.SupersededItems);
        Assert.Equal(0, Assert.Single(status.Items).AttemptsRemaining);
        Assert.Null(Assert.Single(status.Items).NextAutomaticRetryAt);
    }

    [Fact]
    public async Task RetryItemAsync_RejectsCoordinatesFromAnotherRun()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure");
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RetryItemAsync("test", "other-run", item.Id));
    }
}
