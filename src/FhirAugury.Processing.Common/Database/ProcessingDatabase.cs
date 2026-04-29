using FhirAugury.Common.Database;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processing.Common.Database;

/// <summary>
/// Base SQLite database class for concrete Processing services.
/// </summary>
public abstract class ProcessingDatabase(string dbPath, ILogger logger, bool readOnly = false)
    : SourceDatabase(dbPath, logger, readOnly)
{
}
