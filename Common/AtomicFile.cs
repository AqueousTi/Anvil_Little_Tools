using System;
using System.IO;
using System.Text;

namespace LittleTools.Common
{
    internal static class AtomicFile
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

        public static void WriteUtf8(string path, string content)
        {
            WriteUtf8(path, content, null);
        }

        public static void WriteUtf8(string path, string content, string backupPath)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target path is required.", "path");
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, content ?? "", Utf8WithoutBom);
                if (File.Exists(path)) File.Replace(temporary, path, backupPath, true);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
