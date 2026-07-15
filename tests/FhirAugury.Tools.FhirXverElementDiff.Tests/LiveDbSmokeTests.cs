using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Readers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

/// <summary>
/// Skippable smoke tests over the real cache DBs. When the DBs are absent (CI without
/// the cache), each test returns early so the suite still passes.
/// </summary>
[Trait("Category", "LiveDb")]
public sealed class LiveDbSmokeTests
{
    [Fact]
    public void R4_To_R5_Structure_Buckets_And_Renames()
    {
        if (!LiveDb.TryPaths(out string specDb, out _, out _))
        {
            return; // cache DBs unavailable — skip
        }

        ReleaseReader reader = new(NullLogger.Instance);
        ReleaseModel r4 = reader.LoadRelease(reader.ResolveRelease(ReleaseId.R4, specDb));
        ReleaseModel r5 = reader.LoadRelease(reader.ResolveRelease(ReleaseId.R5, specDb));

        StructureBuckets buckets = StructureDiffer.Diff(r4, r5);
        new StructureRenameDetector().Apply(buckets);

        Assert.Contains(buckets.Removed, s => s.Name == "Media");
        Assert.Contains(buckets.Added, s => s.Name == "Citation");
        Assert.Contains(buckets.Mapped, p => p.Later.Name == "Patient" && !p.IsRename);

        StructurePair? deviceUsage = buckets.Mapped.FirstOrDefault(p => p.Later.Name == "DeviceUsage");
        Assert.NotNull(deviceUsage);
        Assert.Equal(RenameKind.Confirmed, deviceUsage!.RenameKind);
        Assert.Equal("DeviceUseStatement", deviceUsage.OldName);

        // DeviceUseStatement / DeviceUsage must not leak into Removed / Added.
        Assert.DoesNotContain(buckets.Removed, s => s.Name == "DeviceUseStatement");
        Assert.DoesNotContain(buckets.Added, s => s.Name == "DeviceUsage");
    }

    [Fact]
    public void R5_To_R6_Xhtml_Id_Surfaces_As_Cardinality_Change()
    {
        if (!LiveDb.TryPaths(out string specDb, out string r6Db, out _))
        {
            return; // cache DBs unavailable — skip
        }

        ReleaseReader reader = new(NullLogger.Instance);
        ReleaseModel r5 = reader.LoadRelease(reader.ResolveRelease(ReleaseId.R5, specDb));
        ReleaseModel r6 = reader.LoadRelease(reader.ResolveRelease(ReleaseId.R6, r6Db));

        StructureBuckets buckets = StructureDiffer.Diff(r5, r6);
        new StructureRenameDetector().Apply(buckets);

        StructurePair? xhtml = buckets.Mapped.FirstOrDefault(p => p.Later.Name == "xhtml");
        Assert.NotNull(xhtml);

        IReadOnlyList<ElementRow> rows = ElementDiffer.Diff(xhtml!, r5, r6);

        // xhtml.id is base-identical in R5 (0..1) but locally constrained in R6 (0..0):
        // the union-of-interestingness filter must keep it and flag a cardinality change,
        // never dropping it as purely inherited.
        ElementRow? idRow = rows.FirstOrDefault(r => r.TargetPath == "xhtml.id" || r.SourcePath == "xhtml.id");
        Assert.NotNull(idRow);
        Assert.True(idRow!.Flags.Cardinality);
        Assert.Contains("0..1 → 0..0", idRow.Summary);
    }
}

/// <summary>Locates the repo-root cache DBs by walking up from the test binaries.</summary>
internal static class LiveDb
{
    public static bool TryPaths(out string specDb, out string r6Db, out string clone)
    {
        specDb = string.Empty;
        r6Db = string.Empty;
        clone = string.Empty;

        string? root = FindRepoRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            return false;
        }

        specDb = System.IO.Path.Combine(root, "cache", "fhir-spec.db");
        r6Db = System.IO.Path.Combine(root, "cache", "fhir-r6.db");
        clone = System.IO.Path.Combine(root, "cache", "github", "repos", "HL7_fhir", "clone");
        return File.Exists(specDb);
    }

    private static string? FindRepoRoot(string start)
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "fhir-augury.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
