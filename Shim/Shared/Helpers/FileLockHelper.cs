using System;
using System.IO;

namespace TurboSuite.Shared.Helpers
{
    /// <summary>
    /// Detects whether a file (typically an Excel workbook) is currently locked by
    /// another process. Used by Counts to fail fast with a helpful message instead
    /// of grinding through a long export only to hit an IOException on save.
    /// </summary>
    public static class FileLockHelper
    {
        /// <summary>
        /// Returns true if the file exists and cannot be opened for exclusive
        /// read/write access — i.e. another process (usually Excel) holds it open.
        /// Returns false if the file is free, missing, or the path is empty.
        /// </summary>
        public static bool IsFileLocked(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only / permission issue rather than a live lock — let the
                // normal save path surface the real error rather than mislabeling it.
                return false;
            }
        }
    }
}
