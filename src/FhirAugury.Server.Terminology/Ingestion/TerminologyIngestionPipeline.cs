using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Database.Records;
using FhirAugury.Server.Terminology.Hosting;
using Hl7.Fhir.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Ingestion;

/// <summary>
/// End-to-end coordinator for the THO ingestion pipeline:
/// for each configured package, resolve via <see cref="FhirPackageSource"/>,
/// compare the resolved version against what's already in SQLite, and
/// either skip, update-in-place, or replace the package's rows.
/// </summary>
/// <remarks>
/// <para>
/// Replacement strategy when a package's resolved version differs from
/// what's already indexed: delete the prior package row, its artifacts,
/// and their concepts; then insert fresh rows. SQLite does not have FK
/// cascades wired here (CsLightDbGen doesn't emit them), so deletes are
/// issued explicitly in the correct order.
/// </para>
/// <para>
/// When the resolved version matches the row already in
/// <c>terminology_packages</c>, no artifact/concept work is performed —
/// we just bump <see cref="TerminologyPackageRecord.IngestedAt"/> so an
/// operator can see the most recent successful check.
/// </para>
/// </remarks>
public sealed class TerminologyIngestionPipeline
{
    private readonly TerminologyDatabase _db;
    private readonly FhirPackageSource _source;
    private readonly TerminologyArtifactNormalizer _normalizer;
    private readonly TerminologyServiceOptions _options;
    private readonly ILogger<TerminologyIngestionPipeline> _logger;

    public TerminologyIngestionPipeline(
        TerminologyDatabase db,
        FhirPackageSource source,
        TerminologyArtifactNormalizer normalizer,
        IOptions<TerminologyServiceOptions> options,
        ILogger<TerminologyIngestionPipeline> logger)
    {
        _db = db;
        _source = source;
        _normalizer = normalizer;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the pipeline across every configured <see cref="PackageOptions"/>.
    /// </summary>
    /// <param name="phaseSink">
    /// Optional progress callback (e.g. publishes to
    /// <see cref="TerminologyIndexStatusTracker"/>).
    /// </param>
    public async Task RunAsync(Action<string>? phaseSink, CancellationToken ct)
    {
        foreach (PackageOptions pkg in _options.Packages)
        {
            ct.ThrowIfCancellationRequested();
            phaseSink?.Invoke($"resolving {pkg.PackageId}#{pkg.VersionTag}");

            PackageIngestSnapshot snapshot;
            try
            {
                snapshot = await _source.AcquireAsync(pkg, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to acquire FHIR package {PackageId}#{Tag}; skipping.",
                    pkg.PackageId, pkg.VersionTag);
                continue;
            }

            await IngestPackageAsync(snapshot, phaseSink, ct).ConfigureAwait(false);
        }
    }

    private async Task IngestPackageAsync(
        PackageIngestSnapshot snapshot,
        Action<string>? phaseSink,
        CancellationToken ct)
    {
        using SqliteConnection connection = _db.OpenConnection();

        TerminologyPackageRecord? existing = FindPackageByPackageId(connection, snapshot.PackageId);

        if (existing is not null && string.Equals(existing.ResolvedVersion, snapshot.ResolvedVersion, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Package {PackageId}@{ResolvedVersion} already indexed; refreshing ingestedAt only.",
                snapshot.PackageId, snapshot.ResolvedVersion);

            TouchIngestedAt(connection, existing.Id);
            return;
        }

        if (existing is not null)
        {
            phaseSink?.Invoke($"replacing {snapshot.PackageId} {existing.ResolvedVersion} → {snapshot.ResolvedVersion}");
            DeletePackageRows(connection, existing.Id, snapshot.PackageId);
        }
        else
        {
            phaseSink?.Invoke($"installing {snapshot.PackageId}@{snapshot.ResolvedVersion}");
        }

        TerminologyPackageRecord pkgRow = new()
        {
            Id = TerminologyPackageRecord.GetIndex(),
            PackageId = snapshot.PackageId,
            RequestedVersionTag = snapshot.RequestedTag,
            ResolvedVersion = snapshot.ResolvedVersion,
            FhirVersion = FhirMajorVersionParser.ToTag(snapshot.FhirVersion),
            IngestedAt = DateTimeOffset.UtcNow,
            ArtifactCount = 0,
            ConceptCount = 0,
        };
        TerminologyPackageRecord.Insert(connection, pkgRow, insertPrimaryKey: true);

        int artifactCount = 0;
        int conceptCount = 0;
        List<TerminologyConceptRecord> conceptBuffer = new(capacity: 4096);

        await foreach (TerminologyResource resource in snapshot.Resources.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            (TerminologyArtifactRecord artifact, List<TerminologyConceptRecord> concepts) =
                resource.Resource switch
                {
                    CodeSystem cs => _normalizer.Normalize(cs, snapshot.PackageId, snapshot.ResolvedVersion,
                        FhirMajorVersionParser.ToTag(snapshot.FhirVersion), resource.Json),
                    ValueSet vs => _normalizer.Normalize(vs, snapshot.PackageId, snapshot.ResolvedVersion,
                        FhirMajorVersionParser.ToTag(snapshot.FhirVersion), resource.Json),
                    _ => (null!, null!),
                };

            if (artifact is null) continue;

            TerminologyArtifactRecord.Insert(connection, artifact, insertPrimaryKey: true);
            artifactCount++;

            foreach (TerminologyConceptRecord c in concepts)
            {
                c.ArtifactId = artifact.Id;
                conceptBuffer.Add(c);
            }

            if (conceptBuffer.Count >= 5000)
            {
                conceptBuffer.Insert(connection, ignoreDuplicates: false, insertPrimaryKey: true);
                conceptCount += conceptBuffer.Count;
                conceptBuffer.Clear();
            }
        }

        if (conceptBuffer.Count > 0)
        {
            conceptBuffer.Insert(connection, ignoreDuplicates: false, insertPrimaryKey: true);
            conceptCount += conceptBuffer.Count;
            conceptBuffer.Clear();
        }

        UpdateCounts(connection, pkgRow.Id, artifactCount, conceptCount);

        _logger.LogInformation(
            "Indexed package {PackageId}@{ResolvedVersion}: {Artifacts} artifacts, {Concepts} concepts.",
            snapshot.PackageId, snapshot.ResolvedVersion, artifactCount, conceptCount);
    }

    // ── Direct SQL: there isn't a generator helper for these. ───────

    private static TerminologyPackageRecord? FindPackageByPackageId(SqliteConnection connection, string packageId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, PackageId, RequestedVersionTag, ResolvedVersion, FhirVersion,
                   IngestedAt, ArtifactCount, ConceptCount
            FROM terminology_packages
            WHERE PackageId = $pid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$pid", packageId);

        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return new TerminologyPackageRecord
        {
            Id = r.GetInt32(0),
            PackageId = r.GetString(1),
            RequestedVersionTag = r.GetString(2),
            ResolvedVersion = r.GetString(3),
            FhirVersion = r.GetString(4),
            IngestedAt = DateTimeOffset.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            ArtifactCount = r.GetInt32(6),
            ConceptCount = r.GetInt32(7),
        };
    }

    private static void TouchIngestedAt(SqliteConnection connection, int packageRowId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE terminology_packages SET IngestedAt = $now WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", packageRowId);
        cmd.ExecuteNonQuery();
    }

    private void DeletePackageRows(SqliteConnection connection, int packageRowId, string packageNpmId)
    {
        using SqliteTransaction tx = connection.BeginTransaction();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM terminology_concepts
                WHERE ArtifactId IN (
                    SELECT Id FROM terminology_artifacts WHERE PackageId = $pid
                );
                DELETE FROM terminology_artifacts WHERE PackageId = $pid;
                DELETE FROM terminology_packages WHERE Id = $row;
                """;
            cmd.Parameters.AddWithValue("$pid", packageNpmId);
            cmd.Parameters.AddWithValue("$row", packageRowId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void UpdateCounts(SqliteConnection connection, int packageRowId, int artifactCount, int conceptCount)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE terminology_packages
            SET ArtifactCount = $a, ConceptCount = $c
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$a", artifactCount);
        cmd.Parameters.AddWithValue("$c", conceptCount);
        cmd.Parameters.AddWithValue("$id", packageRowId);
        cmd.ExecuteNonQuery();
    }
}
