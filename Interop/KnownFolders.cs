using System.IO;
using System.Runtime.InteropServices;

namespace AppConsistency.Interop;

/// <summary>Resolves the real Downloads folder, including when it has been moved to another drive.</summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
