using FhirAugury.Common.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Fhir.Database;

/// <summary>
/// Read-only access to the upstream-built FHIR spec database (e.g.
/// <c>cache/fhir-spec.db</c>). The file is produced by an external CI build; this
/// service never writes to, creates, or migrates it. <see cref="InitializeSchema"/>
/// is intentionally a no-op and must never be invoked for this database.
/// </summary>
public sealed class FhirSpecDatabase : SourceDatabase
{
    private readonly string _dbPath;

    public FhirSpecDatabase(string dbPath, ILogger<FhirSpecDatabase> logger)
        : base(dbPath, logger, readOnly: true)
    {
        _dbPath = dbPath;
    }

    /// <summary>Absolute path to the spec database file.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>True when the spec database file is present on disk (degraded-mode guard).</summary>
    public bool Exists => File.Exists(_dbPath);

    // The spec DB is read-only and externally produced; never create or alter schema.
    protected override void InitializeSchema(SqliteConnection connection)
    {
    }
}
