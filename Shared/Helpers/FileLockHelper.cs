using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TurboSuite.Shared.Helpers
{
    /// <summary>
    /// Detects whether a file (typically an Excel workbook) is currently locked by
    /// another process, and makes a best-effort attempt to name who has it open so
    /// the user can ask that person to close it. Used by Counts to fail fast with a
    /// helpful message instead of grinding through a long export only to hit an
    /// IOException on save.
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

        /// <summary>
        /// Best-effort lookup of the user who has an Excel workbook open, read from
        /// the hidden "~$" owner file Excel writes alongside the workbook. Returns
        /// null if it cannot be determined. Never throws.
        /// </summary>
        public static string TryGetLockOwner(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
                    return null;

                string ownerFile = Path.Combine(dir, "~$" + name);
                if (!File.Exists(ownerFile))
                    return null;

                byte[] bytes;
                using (var fs = File.Open(ownerFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                return ExtractOwnerName(bytes);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The Excel owner file stores a one-byte character count at offset 0
        /// followed by the username. Older Office writes it as single-byte (ANSI)
        /// characters, newer Office as UTF-16. Decode both and pick whichever yields
        /// a plausible name.
        /// </summary>
        private static string ExtractOwnerName(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2)
                return null;

            int count = bytes[0];
            if (count <= 0 || count > 54)
                return null;

            // ANSI: name immediately follows the length byte, one byte per char.
            string ansi = SafeString(bytes, 1, count, Encoding.Default);
            if (IsPlausibleName(ansi))
                return ansi.Trim();

            // Unicode: two bytes per char.
            string unicode = SafeString(bytes, 1, count * 2, Encoding.Unicode);
            if (IsPlausibleName(unicode))
                return unicode.Trim();

            return null;
        }

        private static string SafeString(byte[] bytes, int offset, int length, Encoding enc)
        {
            if (offset + length > bytes.Length)
                length = bytes.Length - offset;
            if (length <= 0)
                return null;
            return enc.GetString(bytes, offset, length);
        }

        private static bool IsPlausibleName(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;
            s = s.Trim();
            if (s.Length < 2)
                return false;
            // A real name is mostly letters/digits/common name punctuation.
            int good = s.Count(c => char.IsLetterOrDigit(c) || " .,'-_@".IndexOf(c) >= 0);
            return good >= s.Length - 1;
        }
    }
}
