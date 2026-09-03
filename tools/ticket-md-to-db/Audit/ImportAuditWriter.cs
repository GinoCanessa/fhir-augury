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
        WriteJsonAtomicallyAsync(path, audit, overwrite: true, ct);

    public static Task WriteNewAuditAsync(
        string path,
        ImportAudit audit,
        CancellationToken ct = default) =>
        WriteJsonAtomicallyAsync(path, audit, overwrite: false, ct);

    public static Task WriteOverrideTemplateAsync(
        string path,
        ImportOverrideTemplate template,
        CancellationToken ct = default) =>
        WriteJsonAtomicallyAsync(path, template, overwrite: true, ct);

    public static bool TryReadAudit(string path, out ImportAudit? audit)
    {
        try
        {
            audit = JsonSerializer.Deserialize<ImportAudit>(
                File.ReadAllText(path),
                JsonOptions);
            return audit is not null;
        }
        catch (Exception ex) when (ex is
            IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            audit = null;
            return false;
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        bool overwrite,
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

            File.Move(temporaryPath, absolutePath, overwrite);
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
