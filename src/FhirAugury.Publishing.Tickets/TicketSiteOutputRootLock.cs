namespace FhirAugury.Publishing.Tickets;

internal static class TicketSiteOutputRootLock
{
    internal const string LockFileName =
        ".fhir-augury-ticket-site.publish-lock";

    private const int WindowsSharingViolationHResult =
        unchecked((int)0x80070020);
    private const int WindowsLockViolationHResult =
        unchecked((int)0x80070021);
    private const int UnixTryAgain = 11;
    private const int UnixWouldBlock = 35;

    public static Task<FileStream> AcquireAsync(
        string outputRoot,
        CancellationToken ct)
        => AcquireAsync(outputRoot, ct, openLockFile: null);

    internal static async Task<FileStream> AcquireAsync(
        string outputRoot,
        CancellationToken ct,
        Func<string, FileStream>? openLockFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(outputRoot));
            Directory.CreateDirectory(root);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"Could not prepare ticket-site output root '{outputRoot}' " +
                $"for publication locking: {ex.Message}",
                ex);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Could not prepare ticket-site output root '{outputRoot}' " +
                $"for publication locking: {ex.Message}",
                ex);
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException(
                $"Could not resolve ticket-site output root '{outputRoot}' " +
                $"for publication locking: {ex.Message}",
                ex);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"Could not resolve ticket-site output root '{outputRoot}' " +
                $"for publication locking: {ex.Message}",
                nameof(outputRoot),
                ex);
        }

        string lockPath = Path.Combine(root, LockFileName);
        openLockFile ??= OpenLockFile;
        int delayStep = 1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return openLockFile(lockPath);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(delayStep * 10),
                    ct).ConfigureAwait(false);
                delayStep = Math.Min(delayStep + 1, 10);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"Could not acquire ticket-site publication lock " +
                    $"'{lockPath}' inside output root '{root}': {ex.Message}",
                    ex);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Could not acquire ticket-site publication lock " +
                    $"'{lockPath}' inside output root '{root}': {ex.Message}",
                    ex);
            }
            catch (NotSupportedException ex)
            {
                throw new NotSupportedException(
                    $"Could not acquire ticket-site publication lock " +
                    $"'{lockPath}' inside output root '{root}': {ex.Message}",
                    ex);
            }
        }
    }

    private static FileStream OpenLockFile(string lockPath)
        => new(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);

    private static bool IsLockContention(IOException exception)
    {
        if (OperatingSystem.IsWindows())
        {
            return exception.HResult is
                WindowsSharingViolationHResult or
                WindowsLockViolationHResult;
        }

        return exception.HResult is
            UnixTryAgain or
            UnixWouldBlock or
            unchecked((int)0x8007000B) or
            unchecked((int)0x80070023);
    }
}
