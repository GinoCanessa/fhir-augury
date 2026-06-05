using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedHydrationSweeperTests
{
    [Fact]
    public async Task RunPerTicketSweep_RehydratesUnresolvedAndMissing_LeavesResolvedAlone()
    {
        using TestDatabase database = TestDatabase.Create();
        await PreparerSweepSeed.SeedPreparedTicketAsync(database.Database, "FHIR-100");
        await PreparerSweepSeed.SeedPreparedTicketAsync(database.Database, "FHIR-101");
        await PreparerSweepSeed.SeedPreparedTicketAsync(database.Database, "FHIR-102");
        // Ticket 100 has a resolved hydration row → ineligible.
        await PreparerSweepSeed.InsertHydrationRowAsync(database.Database.DatabasePath, "FHIR-100", "resolved");
        // Ticket 101 has an unresolved row → eligible.
        await PreparerSweepSeed.InsertHydrationRowAsync(database.Database.DatabasePath, "FHIR-101", "unresolved");
        // Ticket 102 has no hydration row at all → eligible.

        RecordingHydrator hydrator = new(database.Database);
        PreparedHydrationSweeper sweeper = CreateSweeper(database.Database, hydrator);

        PerTicketSweepResult result = await sweeper.RunPerTicketSweepAsync(CancellationToken.None);

        Assert.Equal(2, result.Eligible);
        Assert.Equal(2, hydrator.Invocations.Count);
        Assert.Contains("FHIR-101", hydrator.Invocations);
        Assert.Contains("FHIR-102", hydrator.Invocations);
        Assert.DoesNotContain("FHIR-100", hydrator.Invocations);
    }

    [Fact]
    public async Task RunFull_RunsSpecBackfillBeforePerTicket()
    {
        using TestDatabase database = TestDatabase.Create();
        await PreparerSweepSeed.SeedPreparedTicketAsync(database.Database, "FHIR-200");

        StubBackfill backfill = new(new SpecificationBackfillResult(3, 0, 0, null));
        RecordingHydrator hydrator = new(database.Database);
        List<string> callOrder = [];
        backfill.OnRun = () => callOrder.Add("spec");
        hydrator.OnInvoke = _ => callOrder.Add("hyd");

        PreparedHydrationSweeper sweeper = CreateSweeper(database.Database, hydrator, backfill);

        HydrationSweepResult result = await sweeper.RunFullAsync(HydrationSweepReason.Startup, CancellationToken.None);

        Assert.Null(result.Specification.Failure);
        Assert.Equal(3, result.Specification.Updated);
        Assert.Equal(1, result.PerTicket.Eligible);
        Assert.Equal("spec", callOrder[0]);
        Assert.Equal("hyd", callOrder[1]);
    }

    [Fact]
    public async Task RunFull_StartupReason_ThrowsWhenSpecBackfillFails()
    {
        using TestDatabase database = TestDatabase.Create();
        await PreparerSweepSeed.SeedPreparedTicketAsync(database.Database, "FHIR-300");

        SpecificationBackfillFailure failure = new("upstream down", "ECONNREFUSED", null);
        StubBackfill backfill = new(new SpecificationBackfillResult(0, 0, 0, failure));
        RecordingHydrator hydrator = new(database.Database);
        PreparedHydrationSweeper sweeper = CreateSweeper(database.Database, hydrator, backfill);

        HydrationSweeperUnavailableException ex = await Assert.ThrowsAsync<HydrationSweeperUnavailableException>(
            () => sweeper.RunFullAsync(HydrationSweepReason.Startup, CancellationToken.None));

        Assert.Equal("upstream down", ex.Message);
        Assert.Same(failure, ex.Failure);
        // Per-ticket pass must not run when startup spec backfill fails.
        Assert.Empty(hydrator.Invocations);
    }

    [Fact]
    public async Task RunFull_AdminReason_DoesNotThrowWhenSpecBackfillFails_AndStillRunsPerTicketSweep()
    {
        using TestDatabase database = TestDatabase.Create();
        await PreparerSweepSeed.SeedPreparedTicketAsync(database.Database, "FHIR-301");

        // (This case isn't the controller's actual path — the controller composes the two
        // phases manually — but documents RunFull's AdminRequest semantics per the plan.)
        SpecificationBackfillFailure failure = new("upstream down", "ECONNREFUSED", null);
        StubBackfill backfill = new(new SpecificationBackfillResult(0, 0, 0, failure));
        RecordingHydrator hydrator = new(database.Database);
        PreparedHydrationSweeper sweeper = CreateSweeper(database.Database, hydrator, backfill);

        HydrationSweepResult result = await sweeper.RunFullAsync(HydrationSweepReason.AdminRequest, CancellationToken.None);

        Assert.NotNull(result.Specification.Failure);
        Assert.Single(hydrator.Invocations);
    }

    [Fact]
    public async Task RunPerTicketSweep_SerializesWritesViaGate_AndBoundsHttpConcurrency()
    {
        using TestDatabase database = TestDatabase.Create();
        const int ticketCount = 12;
        for (int i = 0; i < ticketCount; i++)
        {
            await PreparerSweepSeed.SeedPreparedTicketAsync(database.Database, $"FHIR-{400 + i}");
        }

        // Construct a hydrator that delays per call and records overlap.
        ConcurrencyProbeHydrator probe = new(database.Database, perCallDelay: TimeSpan.FromMilliseconds(60));
        const int maxParallelism = 4;
        PreparedHydrationSweeper sweeper = CreateSweeper(database.Database, probe, maxParallelism: maxParallelism);

        await sweeper.RunPerTicketSweepAsync(CancellationToken.None);

        Assert.Equal(ticketCount, probe.Invocations.Count);
        // The gate makes the entire HydrateAsync call serial — only one in flight at a time.
        Assert.Equal(1, probe.MaxConcurrent);
    }

    private static PreparedHydrationSweeper CreateSweeper(
        PreparerDatabase database,
        PreparedTicketHydrator hydrator,
        SpecificationBackfillService? backfill = null,
        int maxParallelism = 4)
    {
        HydrationOptions opts = new() { MaxParallelism = maxParallelism };
        SpecificationBackfillService specSvc = backfill ?? new NoOpBackfill();
        return new PreparedHydrationSweeper(
            hydrator,
            specSvc,
            database,
            Options.Create(opts),
            NullLogger<PreparedHydrationSweeper>.Instance);
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _directory;
        public PreparerDatabase Database { get; }

        private TestDatabase(string directory, PreparerDatabase database)
        {
            _directory = directory;
            Database = database;
        }

        public static TestDatabase Create()
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-sweeper-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "preparer.db");
            PreparerDatabase database = new(path, NullLogger<PreparerDatabase>.Instance);
            database.Initialize();
            return new TestDatabase(directory, database);
        }

        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class RecordingHydrator(PreparerDatabase database)
        : PreparedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, database, NullLogger<PreparedTicketHydrator>.Instance)
    {
        private readonly object _gate = new();

        public List<string> Invocations { get; } = [];

        public Action<string>? OnInvoke { get; set; }

        public override Task HydrateAsync(string ticketKey, CancellationToken ct)
        {
            lock (_gate)
            {
                Invocations.Add(ticketKey);
            }
            OnInvoke?.Invoke(ticketKey);
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrencyProbeHydrator(PreparerDatabase database, TimeSpan perCallDelay)
        : PreparedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, database, NullLogger<PreparedTicketHydrator>.Instance)
    {
        private readonly object _gate = new();
        private int _current;

        public List<string> Invocations { get; } = [];

        public int MaxConcurrent { get; private set; }

        public override async Task HydrateAsync(string ticketKey, CancellationToken ct)
        {
            int snapshot;
            lock (_gate)
            {
                Invocations.Add(ticketKey);
                _current++;
                if (_current > MaxConcurrent)
                {
                    MaxConcurrent = _current;
                }
                snapshot = _current;
            }

            try
            {
                await Task.Delay(perCallDelay, ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _current--;
                }
            }

            _ = snapshot;
        }
    }

    private sealed class StubBackfill(SpecificationBackfillResult result)
        : SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") },
            Options.Create(new HydrationOptions()),
            NullLogger<SpecificationBackfillService>.Instance)
    {
        public Action? OnRun { get; set; }

        public override Task<SpecificationBackfillResult> RunAsync(string preparerDbPath, CancellationToken ct)
        {
            OnRun?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class NoOpBackfill()
        : SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") },
            Options.Create(new HydrationOptions()),
            NullLogger<SpecificationBackfillService>.Instance)
    {
        public override Task<SpecificationBackfillResult> RunAsync(string preparerDbPath, CancellationToken ct)
            => Task.FromResult(new SpecificationBackfillResult(0, 0, 0, null));
    }
}
