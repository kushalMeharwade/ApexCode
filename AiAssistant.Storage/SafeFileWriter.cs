using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;

namespace AiAssistant.Storage
{
    public static class SafeFileWriter
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        public static async Task WriteAllTextAsync(string filePath, string contents, CancellationToken ct = default)
        {
            var fileLock = _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
            await fileLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Detect encoding of the existing file to preserve BOM
                Encoding encoding = new UTF8Encoding(false); // Default to UTF-8 without BOM

            if (File.Exists(filePath))
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length >= 2)
                    {
                        var bom = new byte[4];
                        fs.Read(bom, 0, 4);

                        if (bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf)
                            encoding = new UTF8Encoding(true);
                        else if (bom[0] == 0xff && bom[1] == 0xfe)
                            encoding = Encoding.Unicode;
                        else if (bom[0] == 0xfe && bom[1] == 0xff)
                            encoding = Encoding.BigEndianUnicode;
                        else if (bom[0] == 0 && bom[1] == 0 && bom[2] == 0xfe && bom[3] == 0xff)
                            encoding = Encoding.UTF32;
                    }
                }
            }

            var tempFilePath = filePath + "." + Guid.NewGuid() + ".tmp";
            
            try
            {
                // Write to a temporary file
                using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, encoding))
                {
                    await sw.WriteAsync(contents).ConfigureAwait(false);
                }

                // Atomically move the temporary file over the existing file
                if (File.Exists(filePath))
                {
                    File.Replace(tempFilePath, filePath, null);
                }
                else
                {
                    File.Move(tempFilePath, filePath);
                }
            }
            finally
            {
                // Clean up the temp file if an exception occurred and it wasn't moved
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { /* ignore */ }
                }
            }
        }
        finally
        {
            fileLock.Release();
            }
        }
        
        public static void WriteAllText(string filePath, string contents)
        {
            WriteAllTextAsync(filePath, contents).GetAwaiter().GetResult();
        }
    }
}
