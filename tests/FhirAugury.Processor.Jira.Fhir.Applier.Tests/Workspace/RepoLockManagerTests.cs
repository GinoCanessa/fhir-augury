using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

public class RepoLockManagerTests
{
    [Fact]
    public async Task SameRepo_AcquiresSerially()
    {
        RepoLockManager mgr = new();
        IDisposable first = await mgr.AcquireAsync("HL7/fhir", default);
        Task<IDisposable> second = mgr.AcquireAsync("HL7/fhir", default);
        await Task.Delay(50);
        Assert.False(second.IsCompleted, "second acquire should block while first is held");
        first.Dispose();
        IDisposable secondReleased = await second.WaitAsync(TimeSpan.FromSeconds(2));
        secondReleased.Dispose();
    }

    [Fact]
    public async Task DifferentRepos_RunInParallel()
    {
        RepoLockManager mgr = new();
        IDisposable a = await mgr.AcquireAsync("HL7/fhir", default);
        IDisposable b = await mgr.AcquireAsync("HL7/fhir-tools", default);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task DoubleDispose_DoesNotOverRelease()
    {
        RepoLockManager mgr = new();
        IDisposable lk = await mgr.AcquireAsync("HL7/fhir", default);
        lk.Dispose();
        lk.Dispose();
        IDisposable next = await mgr.AcquireAsync("HL7/fhir", default).WaitAsync(TimeSpan.FromSeconds(1));
        next.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_RejectsEmpty()
    {
        RepoLockManager mgr = new();
        await Assert.ThrowsAsync<ArgumentException>(async () => await mgr.AcquireAsync("", default));
    }
}
