using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;

internal sealed record PlannerRetainedMember(
    string Path, string RootId, string RelativePath, PlannerRetainedFileIdentity Identity, long Length);

internal sealed record PlannerRetainedMembership(
    IReadOnlyList<PlannerRetainedMember> Files, IReadOnlyList<PlannerRetainedDirectory> Directories);

internal sealed class PlannerRetainedStatePaths : IDisposable
{
    internal const int MaximumPathLength = 240;
    internal static readonly string[] SqliteSuffixes = ["-wal", "-shm", "-journal"];
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.OrdinalIgnoreCase);

    internal string Bundle { get; }
    internal string OwnerLockPath { get; }
    internal IReadOnlyList<PlannerRetainedRoot> Roots { get; }
    internal PlannerRetainedSettingsPaths SettingsPaths { get; }
    internal PlannerRetainedFileIdentity DatabaseIdentity { get; }
    internal bool BundleCreated { get; private set; }

    private PlannerRetainedStatePaths(PlannerRetainedStateSettings settings)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw Refuse("unsupported-platform-file-identity");
            }
            string database = Absolute(settings.Database);
            string snapshots = Absolute(settings.Snapshots);
            string? backup = settings.PreCutoverBackup.Length == 0 ? null : Absolute(settings.PreCutoverBackup);
            Bundle = Absolute(settings.Bundle);
            OwnerLockPath = database + ".owner.lock";
            string databaseDirectory = Path.GetDirectoryName(database)!;
            string evidenceRoot = Path.GetDirectoryName(Bundle)!;
            if (evidenceRoot.Length > 60)
            {
                throw Refuse("path-budget-exceeded");
            }
            Budget(OwnerLockPath);

            // A typo must not reach PlannerDatabase's OpenOrCreate ownership protocol.
            if (!TryAttributes(databaseDirectory, out FileAttributes parent) ||
                !parent.HasFlag(FileAttributes.Directory) || !TryAttributes(database, out FileAttributes file) ||
                file.HasFlag(FileAttributes.Directory))
            {
                throw Refuse("database-missing-or-not-regular");
            }
            PinAncestors(database);
            DatabaseIdentity = InspectFile(database);

            List<string> directories = [databaseDirectory, snapshots];
            directories.AddRange(settings.RetainedRoots.Select(Absolute));
            foreach (string directory in directories)
            {
                RefuseBroadRoot(directory);
                PinAncestors(Path.Combine(directory, "_"));
                if (TryAttributes(directory, out FileAttributes attributes) &&
                    !attributes.HasFlag(FileAttributes.Directory))
                {
                    throw Refuse("retained-root-not-directory");
                }
                if (settings.RetainedRoots.Any(root => SamePath(Absolute(root), directory)) &&
                    !TryAttributes(directory, out _))
                {
                    throw Refuse("retained-root-missing");
                }
            }
            List<PlannerRetainedRoot> roots = [];
            foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal))
            {
                if (!roots.Any(root => Contains(root.OriginalDirectory, directory)))
                {
                    roots.Add(new($"r{roots.Count:D4}", directory, "directory", null, TryAttributes(directory, out _)));
                }
            }
            if (backup is not null)
            {
                PinAncestors(backup);
                if (TryAttributes(backup, out FileAttributes attributes))
                {
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        throw Refuse("backup-not-regular");
                    }
                    if (SamePath(backup, database) || InspectFile(backup) == DatabaseIdentity)
                    {
                        throw Refuse("physical-file-alias");
                    }
                }
                if (!roots.Any(root => Contains(root.OriginalDirectory, backup)))
                {
                    string directory = Path.GetDirectoryName(backup)!;
                    RefuseBroadRoot(directory);
                    roots.Add(new($"r{roots.Count:D4}", directory, "file-family",
                        Path.GetFileName(backup), TryAttributes(backup, out _)));
                }
            }
            Roots = roots;
            string rehearsal = Path.Combine(evidenceRoot, "rehearsal-1");
            foreach (PlannerRetainedRoot root in roots)
            {
                if (Overlaps(root.OriginalDirectory, Bundle) || Overlaps(root.OriginalDirectory, rehearsal))
                {
                    throw Refuse("overlapping-roots");
                }
            }
            if (Overlaps(Bundle, rehearsal))
            {
                throw Refuse("overlapping-roots");
            }
            PinAncestors(Bundle);
            PinAncestors(rehearsal);
            if (TryAttributes(Bundle, out _))
            {
                throw Refuse("destination-exists");
            }
            SettingsPaths = new(Map(database, true), backup is null ? null : Map(backup, TryAttributes(backup, out _)),
                Map(snapshots, TryAttributes(snapshots, out _)));
            ValidateExpandedBudgets(Enumerate(), rehearsal);
            if (TryAttributes(OwnerLockPath, out FileAttributes ownerAttributes))
            {
                if (ownerAttributes.HasFlag(FileAttributes.Directory))
                {
                    throw Refuse("owner-lock-not-regular");
                }
                // The coordinating file is admitted too; a hard link here would allow
                // the owner's OpenOrCreate handle to alias unrelated retained bytes.
                _ = InspectFile(OwnerLockPath);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static PlannerRetainedStatePaths Admit(PlannerRetainedStateSettings settings) => new(settings);

    internal PlannerRetainedPath Map(string original, bool existed)
    {
        original = Absolute(original);
        PlannerRetainedRoot? root = Roots.FirstOrDefault(root =>
            Contains(root.OriginalDirectory, original) &&
            (root.Scope == "directory" || SamePath(Path.Combine(root.OriginalDirectory, root.SelectedFile!), original)));
        return root is null
            ? throw Refuse("unmapped-retained-path")
            : new(root.Id, Relative(root.OriginalDirectory, original), existed);
    }

    internal PlannerRetainedMembership Enumerate()
    {
        List<PlannerRetainedMember> files = [];
        List<PlannerRetainedDirectory> directories = [];
        HashSet<PlannerRetainedFileIdentity> identities = [];
        foreach (PlannerRetainedRoot root in Roots)
        {
            if (root.Scope == "directory")
            {
                if (TryAttributes(root.OriginalDirectory, out _))
                {
                    Walk(root, root.OriginalDirectory);
                }
            }
            else if (TryAttributes(root.OriginalDirectory, out _))
            {
                PinDirectory(root.OriginalDirectory);
                foreach (string path in Directory.EnumerateFileSystemEntries(
                             root.OriginalDirectory, root.SelectedFile + "*", SearchOption.TopDirectoryOnly)
                             .Order(StringComparer.Ordinal))
                {
                    AddFile(root, path);
                }
            }
        }
        return new(files.OrderBy(member => member.Path, StringComparer.Ordinal).ToArray(),
            directories.OrderBy(directory => directory.RootId, StringComparer.Ordinal)
                .ThenBy(directory => directory.RelativePath, StringComparer.Ordinal).ToArray());

        void Walk(PlannerRetainedRoot root, string directory)
        {
            PlannerRetainedFileIdentity identity = PinDirectory(directory);
            directories.Add(new(root.Id, Relative(root.OriginalDirectory, directory), identity));
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                FileAttributes attributes = Attributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Refuse("reparse-point");
                }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Walk(root, path);
                }
                else
                {
                    AddFile(root, path);
                }
            }
        }

        void AddFile(PlannerRetainedRoot root, string path)
        {
            if (SamePath(path, OwnerLockPath))
            {
                return;
            }
            PlannerRetainedFileIdentity identity = InspectFile(path);
            if (!identities.Add(identity))
            {
                throw Refuse("physical-file-alias");
            }
            files.Add(new(path, root.Id, Relative(root.OriginalDirectory, path),
                identity, new FileInfo(path).Length));
        }
    }

    internal void ValidateExpandedBudgets(PlannerRetainedMembership membership, string? rehearsalRoot = null)
    {
        string rehearsal = rehearsalRoot ?? Path.Combine(Path.GetDirectoryName(Bundle)!, "rehearsal-1");
        foreach (PlannerRetainedMember member in membership.Files)
        {
            Budget(member.Path);
            foreach (string area in new[] { "raw", "normalization" })
            {
                Budget(InBundle(area + "/" + member.RootId + "/" + member.RelativePath));
            }
            string normalized = InBundle("normalization/" + member.RootId + "/" + member.RelativePath);
            string work = Under(rehearsal, "work/" + member.RootId + "/" + member.RelativePath);
            foreach (string suffix in SqliteSuffixes
                         .Concat(SqliteSuffixes.Select(suffix => ".tmp" + suffix)).Append(".owner.lock"))
            {
                Budget(normalized + suffix);
                Budget(work + suffix);
            }
        }
        foreach (PlannerRetainedDirectory directory in membership.Directories)
        {
            Budget(InBundle("raw/" + directory.RootId + "/" + directory.RelativePath));
            Budget(Under(rehearsal, "work/" + directory.RootId + "/" + directory.RelativePath));
        }
        if (SettingsPaths.PreCutoverBackup is { } backup)
        {
            string work = Under(rehearsal, "work/" + backup.RootId + "/" + backup.RelativePath);
            string temporary = work + "." + new string('0', 32) + ".tmp";
            Budget(work);
            foreach (string suffix in SqliteSuffixes.Prepend(""))
            {
                Budget(temporary + suffix);
            }
        }
        PlannerRetainedPath snapshots = SettingsPaths.Snapshots;
        string futureSnapshot = Under(rehearsal, ("work/" + snapshots.RootId + "/" +
            snapshots.RelativePath).TrimEnd('/') + "/jira-fhir-" + new string('0', 32) + ".db.tmp");
        foreach (string suffix in SqliteSuffixes.Prepend(""))
        {
            Budget(futureSnapshot + suffix);
            Budget(InBundle("safety/d000000.db") + suffix);
            Budget(InBundle("safety/planner.db") + suffix);
            Budget(Under(rehearsal, "diagnostics/cutover-candidate.db") + suffix);
        }
        foreach (string name in new[]
        {
            "manifest.json", "capture.complete.json", "capture.complete.pending.json",
            "capture.started.json", "capture.incomplete.json", "normalization/temp",
            "normalization/validation/f000000.db-journal",
            "inventory/d000000/t000000-index000000.rows", "inventory/d000000/inventory.json",
            "inventory/d000000/snapshot-references.rows",
        })
        {
            Budget(InBundle(name));
        }
        foreach (string name in new[]
        {
            "temp", "rehearsal.result.json", "checkpoints/pass2-post-initialization/d000000/t000000-index000000.rows",
        })
        {
            Budget(Under(rehearsal, name));
        }
    }

    internal void CreateBundle()
    {
        string parent = Path.GetDirectoryName(Bundle)!;
        if (!TryAttributes(parent, out _))
        {
            throw Refuse("evidence-parent-missing");
        }
        PinDirectory(parent);
        CreateNewDirectory(Bundle);
        BundleCreated = true;
        PinDirectory(Bundle);
    }

    internal string InBundle(string relative) => Under(Bundle, relative);

    internal string Output(string relative)
    {
        string path = InBundle(relative);
        Budget(path);
        EnsureOutputDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    internal void EnsureOutputDirectory(string directory)
    {
        if (!Contains(Bundle, directory) || !BundleCreated)
        {
            throw Refuse("output-map-escape");
        }
        if (SamePath(Bundle, directory))
        {
            PinDirectory(Bundle);
            return;
        }
        EnsureOutputDirectory(Path.GetDirectoryName(directory)!);
        if (!TryAttributes(directory, out _))
        {
            CreateNewDirectory(directory);
        }
        PinDirectory(directory);
    }

    internal void PinAncestors(string path)
    {
        string? directory = Path.GetDirectoryName(Absolute(path));
        if (directory is null)
        {
            return;
        }
        PinAncestors(directory);
        if (TryAttributes(directory, out FileAttributes attributes))
        {
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw Refuse("ancestor-not-directory");
            }
            PinDirectory(directory);
        }
    }

    private PlannerRetainedFileIdentity PinDirectory(string directory)
    {
        if (!_directories.TryGetValue(directory, out SafeFileHandle? handle))
        {
            handle = OpenHandle(directory, directory: true, 0x80, FileShare.Read);
            try
            {
                PlannerRetainedFileIdentity identity = Identify(handle, directory, directoryExpected: true);
                _directories.Add(directory, handle);
                return identity;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        return Identify(handle, directory, directoryExpected: true);
    }

    internal static PlannerRetainedFileIdentity InspectFile(string path)
    {
        using SafeFileHandle handle = OpenHandle(path, directory: false, 0x80,
            FileShare.ReadWrite | FileShare.Delete);
        return Identify(handle, path, directoryExpected: false);
    }

    internal static SafeFileHandle PinFileIdentity(string path)
    {
        // Attribute-only access can coexist with the existing owner's exclusive
        // data handle. Denying delete pins the admitted lock file across acquisition.
        SafeFileHandle handle = OpenHandle(path, directory: false, 0x80, FileShare.ReadWrite);
        try
        {
            _ = Identify(handle, path, directoryExpected: false);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileStream OpenFrozenFile(string path)
    {
        CheckAncestors(path);
        SafeFileHandle handle = OpenHandle(path, directory: false, 0x80000000, FileShare.Read);
        try
        {
            _ = Identify(handle, path, directoryExpected: false);
            return new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle FreezeDirectory(string path)
    {
        CheckAncestors(path);
        SafeFileHandle handle = OpenHandle(path, directory: true, 0x80, FileShare.Read);
        try
        {
            _ = Identify(handle, path, directoryExpected: true);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileStream CreateFile(string path)
    {
        CheckAncestors(path);
        FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920);
        try
        {
            _ = Identify(file.SafeFileHandle, path, directoryExpected: false);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal static PlannerRetainedFileIdentity Identify(
        SafeFileHandle handle, string expectedPath, bool directoryExpected = false)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information) ||
            !GetFileInformationByHandleEx(handle, 18, out FileIdInformation fileId, 24) ||
            GetFileType(handle) != 1)
        {
            throw Refuse("unsupported-file-identity");
        }
        FileAttributes attributes = (FileAttributes)information.FileAttributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
            attributes.HasFlag(FileAttributes.Directory) != directoryExpected)
        {
            throw Refuse("reparse-or-nonregular-member");
        }
        if (!directoryExpected && information.NumberOfLinks != 1)
        {
            throw Refuse("physical-file-alias");
        }
        StringBuilder path = new(32768);
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity ||
            !SamePath(path.ToString().Replace(@"\\?\", "", StringComparison.Ordinal), expectedPath))
        {
            throw Refuse("physical-path-alias");
        }
        return new(fileId.VolumeSerialNumber, $"{fileId.High:X16}{fileId.Low:X16}", information.NumberOfLinks);
    }

    internal static void CheckAncestors(string path)
    {
        string? directory = Path.GetDirectoryName(Absolute(path));
        while (directory is not null)
        {
            if (TryAttributes(directory, out FileAttributes attributes))
            {
                if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Refuse("reparse-or-nondirectory-ancestor");
                }
                using SafeFileHandle handle = OpenHandle(directory, directory: true, 0x80,
                    FileShare.ReadWrite | FileShare.Delete);
                _ = Identify(handle, directory, directoryExpected: true);
            }
            directory = Path.GetDirectoryName(directory);
        }
    }

    internal static string Absolute(string path)
    {
        path = AbsoluteSyntax(path);
        if (GetDriveType(path[..3]) != 3)
        {
            throw Refuse("nonlocal-drive");
        }
        return path;
    }

    // Consumers may validate opaque original provenance syntactically, but must
    // not probe its drive, existence, ancestors or members.
    internal static string AbsoluteSyntax(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            throw Refuse("unsupported-or-empty-path");
        }
        path = path.Replace('/', '\\');
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.AsSpan(2).Contains(':') || path.Any(character => character < ' ' || "<>\"|?*".Contains(character)))
        {
            throw Refuse("nonlocal-or-nonabsolute-path");
        }
        string root = path[..3];
        string tail = path[3..].TrimEnd('\\');
        if (tail.Length != 0)
        {
            foreach (string part in tail.Split('\\'))
            {
                string token = part.Split('.')[0];
                if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                    token.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                    (token.Length == 4 && (token.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                        (char.IsDigit(token[3]) || "¹²³".Contains(token[3]))))
                {
                    throw Refuse("ambiguous-or-traversing-path");
                }
            }
        }
        return root + tail;
    }

    internal static string Under(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
        {
            throw Refuse("output-map-escape");
        }
        string result = Absolute(Path.Combine(root, relative.Replace('/', '\\')));
        if (!Contains(root, result))
        {
            throw Refuse("output-map-escape");
        }
        return result;
    }

    internal static string Relative(string root, string path)
        => SamePath(root, path) ? "" : Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static bool SamePath(string left, string right)
        => string.Equals(left.Replace('/', '\\').TrimEnd('\\'), right.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    internal static bool Contains(string root, string path)
        => SamePath(root, path) || path.Replace('/', '\\').StartsWith(
            root.Replace('/', '\\').TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(string left, string right) => Contains(left, right) || Contains(right, left);

    internal static void Budget(string path)
    {
        if (path.Length > MaximumPathLength)
        {
            throw Refuse("path-budget-exceeded");
        }
    }

    private static void RefuseBroadRoot(string directory)
    {
        if (SamePath(directory, Path.GetPathRoot(directory)!) ||
            Contains(directory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ||
            Contains(directory, AppContext.BaseDirectory.TrimEnd('\\')) ||
            Contains(directory, Environment.CurrentDirectory) ||
            TryAttributes(Path.Combine(directory, ".git"), out _))
        {
            throw Refuse("unbounded-retained-root");
        }
    }

    internal static bool TryAttributes(string path, out FileAttributes attributes)
    {
        uint value = GetFileAttributes(path);
        if (value != uint.MaxValue)
        {
            attributes = (FileAttributes)value;
            return true;
        }
        int error = Marshal.GetLastWin32Error();
        if (error is not (2 or 3))
        {
            throw new PlannerRetainedStateException("paths", "inaccessible-member", inner: new Win32Exception(error));
        }
        attributes = default;
        return false;
    }

    private static FileAttributes Attributes(string path)
        => TryAttributes(path, out FileAttributes attributes) ? attributes : throw Refuse("membership-changed");

    internal static void CreateNewDirectory(string path)
    {
        CheckAncestors(path);
        if (!CreateDirectory(path, IntPtr.Zero))
        {
            throw new PlannerRetainedStateException("paths",
                Marshal.GetLastWin32Error() == 183 ? "destination-exists" : "directory-create-failed");
        }
    }

    private static SafeFileHandle OpenHandle(string path, bool directory, uint access, FileShare share)
    {
        SafeFileHandle handle = CreateFileNative(Absolute(path), access, share, IntPtr.Zero, 3,
            0x00200000u | (directory ? 0x02000000u : 0u), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new PlannerRetainedStateException("paths",
                error is 32 or 33 ? "owner-or-writer-busy" : "inaccessible-member", inner: new Win32Exception(error));
        }
        return handle;
    }

    private static PlannerRetainedStateException Refuse(string category) => new("paths", category);

    public void Dispose()
    {
        foreach (SafeFileHandle handle in _directories.Values.Reverse())
        {
            handle.Dispose();
        }
        _directories.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(
        string name, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, out FileIdInformation information, uint size);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string root);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);
}
