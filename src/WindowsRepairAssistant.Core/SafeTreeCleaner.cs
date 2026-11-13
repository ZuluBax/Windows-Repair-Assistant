using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsRepairAssistant.Core;

public sealed record CleanupStats(int FilesDeleted, int FoldersDeleted, long BytesFreed, int InUse, int NotDeleted)
{
    public static readonly CleanupStats Zero = new(0, 0, 0, 0, 0);

    public static CleanupStats operator +(CleanupStats a, CleanupStats b) => new(
        a.FilesDeleted + b.FilesDeleted, a.FoldersDeleted + b.FoldersDeleted, a.BytesFreed + b.BytesFreed,
        a.InUse + b.InUse, a.NotDeleted + b.NotDeleted);
}

/// <summary>Thrown when a folder is deliberately not cleaned. The message completes "… was not cleaned because …".</summary>
public sealed class CleanupRefusedException(string reason) : IOException(reason);

/// <summary>
/// Deletes everything inside a folder that can be deleted, leaving the folder itself and anything in use.
/// </summary>
/// <remarks>
/// Security: this runs as administrator inside folders that standard users can write to (C:\Windows\Temp
/// accepts files from every user). A user could plant a junction or link there, or swap a folder for one
/// while we work, to make us delete files they could not delete themselves (a well-known
/// privilege-escalation pattern). So every item is opened without following links, its real location is
/// read back from that open handle and checked to be inside the folder being cleaned, and it is then
/// deleted through the same handle, leaving no gap for a swap between check and delete. A link is only
/// ever removed itself; its target is never entered or touched.
/// </remarks>
public static class SafeTreeCleaner
{
    // Deep enough for any real temp folder; deliberately nested trees beyond this are left alone.
    private const int MaxDepth = 64;
    private const int ProgressInterval = 500;

    /// <param name="protectedPaths">Full paths never to delete, counted as in use: files Windows will move or delete at the next restart.</param>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="CleanupRefusedException">The folder is a link or not a folder.</exception>
    /// <exception cref="IOException">The folder could not be opened.</exception>
    public static CleanupStats DeleteContents(string root, IEnumerable<string>? protectedPaths = null, Action<CleanupStats>? onProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"\"{root}\" does not exist.");
        }

        string rootFinal;
        using (SafeFileHandle handle = Native.Open(root, Native.FileReadAttributes, Native.ShareAll, out int error))
        {
            if (handle.IsInvalid)
            {
                throw new IOException($"\"{root}\" could not be opened: {new Win32Exception(error).Message}");
            }

            FileAttributes attributes = Native.GetAttributes(handle);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CleanupRefusedException("it is a link to another location, so it was left alone for safety reasons");
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw new CleanupRefusedException("it is not a folder");
            }

            rootFinal = Native.GetFinalPath(handle);
        }

        var isProtected = new HashSet<string>(protectedPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var counter = new Counter(rootFinal, isProtected, onProgress);
        counter.CleanDirectory(rootFinal, depth: 0);
        return counter.Snapshot();
    }

    private sealed class Counter(string rootFinal, HashSet<string> protectedPaths, Action<CleanupStats>? onProgress)
    {
        private int _files;
        private int _folders;
        private long _bytes;
        private int _inUse;
        private int _notDeleted;

        public CleanupStats Snapshot() => new(_files, _folders, _bytes, _inUse, _notDeleted);

        public void CleanDirectory(string directory, int depth)
        {
            if (depth > MaxDepth)
            {
                _notDeleted++;
                return;
            }

            List<(string Path, bool LooksLikeFolder)> entries;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
                // Materialised first so no enumeration handle is open while we delete.
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options)
                    .Select(i => (i.FullName, i.Attributes.HasFlag(FileAttributes.Directory) && !i.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _notDeleted++;
                return;
            }

            foreach ((string path, bool looksLikeFolder) in entries)
            {
                // The enumeration's attributes are only a hint for which way to open the item; what is
                // actually deleted is decided from the opened handle.
                if (looksLikeFolder)
                {
                    CleanSubfolder(path, depth);
                }
                else
                {
                    DeleteItem(path, expectFolder: false);
                }
            }
        }

        private void CleanSubfolder(string path, int depth)
        {
            string final;
            using (SafeFileHandle handle = Native.Open(path, Native.FileReadAttributes, Native.ShareAll, out int error))
            {
                if (handle.IsInvalid)
                {
                    RecordOpenFailure(error, isFolder: true);
                    return;
                }

                FileAttributes attributes;
                try
                {
                    attributes = Native.GetAttributes(handle);
                    final = Native.GetFinalPath(handle);
                }
                catch (Win32Exception)
                {
                    _notDeleted++;
                    return;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
                {
                    // Swapped for a link or file since it was listed: treat it as a single item, never enter it.
                    handle.Dispose();
                    DeleteItem(path, expectFolder: false);
                    return;
                }

                if (!IsInsideRoot(final))
                {
                    _notDeleted++;
                    return;
                }

                if (IsProtected(final))
                {
                    _inUse++;
                    return;
                }
            }

            // Children are each re-checked against the root when deleted, so a swap after this point
            // can only make them be skipped, never deleted somewhere else.
            CleanDirectory(final, depth + 1);
            DeleteItem(final, expectFolder: true);
        }

        private void DeleteItem(string path, bool expectFolder)
        {
            // Files are opened without sharing, so a file that any program has open counts as in use and is
            // left alone. Folders allow sharing because Explorer or a console may simply be looking at them.
            uint share = expectFolder ? Native.ShareAll : 0;
            using SafeFileHandle handle = Native.Open(path, Native.Delete | Native.FileReadAttributes, share, out int error);
            if (handle.IsInvalid)
            {
                RecordOpenFailure(error, expectFolder);
                return;
            }

            FileAttributes attributes;
            string final;
            Native.FileStandardInfo info;
            try
            {
                attributes = Native.GetAttributes(handle);
                final = Native.GetFinalPath(handle);
                info = Native.GetStandardInfo(handle);
            }
            catch (Win32Exception)
            {
                _notDeleted++;
                return;
            }

            bool isLink = attributes.HasFlag(FileAttributes.ReparsePoint);
            bool isRealFolder = attributes.HasFlag(FileAttributes.Directory) && !isLink;
            if (isRealFolder != expectFolder)
            {
                // Changed between listing and opening: never delete a real folder whose contents were not checked.
                _notDeleted++;
                return;
            }

            if (!IsInsideRoot(final))
            {
                _notDeleted++;
                return;
            }

            if (IsProtected(final))
            {
                _inUse++;
                return;
            }

            int result = Native.MarkForDeletion(handle);
            if (result == Native.ErrorDirNotEmpty)
            {
                return; // Something inside is in use; that is already counted.
            }

            if (result != 0)
            {
                _notDeleted++;
                return;
            }

            if (isRealFolder || (isLink && attributes.HasFlag(FileAttributes.Directory)))
            {
                _folders++;
            }
            else
            {
                _files++;
                // Removing one name of a file with several hard links frees nothing, and links take no space.
                if (!isLink && info.NumberOfLinks == 1)
                {
                    _bytes += info.EndOfFile;
                }
            }

            if ((_files + _folders) % ProgressInterval == 0)
            {
                onProgress?.Invoke(Snapshot());
            }
        }

        private void RecordOpenFailure(int error, bool isFolder)
        {
            switch (error)
            {
                case Native.ErrorFileNotFound or Native.ErrorPathNotFound:
                    return; // Already gone, e.g. deleted by the program that made it.
                case Native.ErrorSharingViolation or Native.ErrorLockViolation when !isFolder:
                    _inUse++;
                    return;
                case Native.ErrorSharingViolation or Native.ErrorLockViolation:
                    return; // A folder in use; any files in it are counted separately.
                default:
                    _notDeleted++;
                    return;
            }
        }

        private bool IsInsideRoot(string finalPath) => IsStrictlyInside(finalPath, rootFinal);

        // Final paths look like \\?\C:\...; the protected list uses plain C:\... paths.
        private bool IsProtected(string finalPath) =>
            protectedPaths.Count > 0
            && protectedPaths.Contains(finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..] : finalPath);
    }

    /// <summary>True only for something below <paramref name="root"/>: never the root itself or a sibling such as Temp2.</summary>
    internal static bool IsStrictlyInside(string finalPath, string root) =>
        finalPath.Length > root.Length + 1
        && finalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && finalPath[root.Length] == '\\';

    private static class Native
    {
        public const uint Delete = 0x00010000;
        public const uint FileReadAttributes = 0x0080;
        public const uint ShareAll = 0x1 | 0x2 | 0x4;

        public const int ErrorFileNotFound = 2;
        public const int ErrorPathNotFound = 3;
        public const int ErrorSharingViolation = 32;
        public const int ErrorLockViolation = 33;
        public const int ErrorDirNotEmpty = 145;
        private const int ErrorInvalidFunction = 1;
        private const int ErrorNotSupported = 50;
        private const int ErrorInvalidParameter = 87;

        private const uint OpenExisting = 3;
        private const uint FlagBackupSemantics = 0x02000000; // needed to open folders
        private const uint FlagOpenReparsePoint = 0x00200000; // open a link itself, never its target

        private const int FileStandardInfoClass = 1;
        private const int FileDispositionInfoClass = 4;
        private const int FileAttributeTagInfoClass = 9;
        private const int FileDispositionInfoExClass = 21;

        private const uint DispositionDelete = 0x1;
        private const uint DispositionPosixSemantics = 0x2;
        private const uint DispositionIgnoreReadOnly = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct FileStandardInfo
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint NumberOfLinks;
            public byte DeletePending;
            public byte Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfoEx
        {
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            public byte DeleteFile;
        }

        public static SafeFileHandle Open(string path, uint access, uint share, out int error)
        {
            SafeFileHandle handle = CreateFileW(path, access, share, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
            return handle;
        }

        public static FileAttributes GetAttributes(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out FileAttributeTagInfo info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return (FileAttributes)info.FileAttributes;
        }

        public static FileStandardInfo GetStandardInfo(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo info, (uint)Marshal.SizeOf<FileStandardInfo>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return info;
        }

        /// <summary>The item's real, fully resolved location, e.g. <c>\\?\C:\Windows\Temp\x.tmp</c>.</summary>
        public static string GetFinalPath(SafeFileHandle handle)
        {
            var buffer = new char[512];
            while (true)
            {
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                if (length < buffer.Length)
                {
                    return new string(buffer, 0, (int)length);
                }

                buffer = new char[length + 1];
            }
        }

        /// <summary>Returns 0, or the Windows error code. The item is removed when the handle closes.</summary>
        public static int MarkForDeletion(SafeFileHandle handle)
        {
            // POSIX semantics remove the name at once; IgnoreReadOnly saves a separate attribute change.
            var extended = new FileDispositionInfoEx { Flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly };
            if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref extended, (uint)Marshal.SizeOf<FileDispositionInfoEx>()))
            {
                return 0;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error is not (ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction))
            {
                return error;
            }

            // Older Windows 10 builds and FAT-formatted drives don't support the extended form.
            var classic = new FileDispositionInfo { DeleteFile = 1 };
            return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref classic, (uint)Marshal.SizeOf<FileDispositionInfo>())
                ? 0
                : Marshal.GetLastPInvokeError();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FileAttributeTagInfo info, uint size);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FileStandardInfo info, uint size);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref FileDispositionInfoEx info, uint size);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref FileDispositionInfo info, uint size);
    }
}
