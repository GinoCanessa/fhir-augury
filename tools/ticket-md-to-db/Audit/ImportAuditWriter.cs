using System.Text.Json;
using FhirAugury.Tools.TicketMdToDb.Overrides;

namespace FhirAugury.Tools.TicketMdToDb.Audit;

public static class ImportAuditWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Task WriteAuditAsync(
        string path,
        ImportAudit audit,
        CancellationToken ct = default) =>
        WriteJsonAtomicallyAsync(path, audit, ct);

    public static Task WriteOverrideTemplateAsync(
        string path,
        ImportOverrideTemplate template,
        CancellationToken ct = default) =>
        WriteJsonAtomicallyAsync(path, template, ct);

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken ct)
    {
        string absolutePath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(absolutePath)
            ?? throw new InvalidOperationException($"Path has no parent directory: {absolutePath}");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(absolutePath)}.{Guid.NewGuid():N}.writing");
        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(temporaryPath, absolutePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
