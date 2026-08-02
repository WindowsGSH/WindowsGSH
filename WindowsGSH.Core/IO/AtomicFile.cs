using System.IO;
using System.Text;
using System.Threading;

namespace WindowsGSH.Core.IO;

public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, contents, encoding);
            ReplaceOrMove(tempPath, path);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup only; the target write has already completed or failed safely.
            }
        }
    }

    private static void ReplaceOrMove(string tempPath, string destPath)
    {
        if (!File.Exists(destPath))
        {
            File.Move(tempPath, destPath);
            return;
        }

        // File.Replace can fail with IOException if the destination is briefly locked by a
        // dying process (e.g., crash-restart race). One short retry is enough for the lock
        // to clear and avoids a fatal startup crash.
        try
        {
            File.Replace(tempPath, destPath, destinationBackupFileName: null);
        }
        catch (IOException)
        {
            Thread.Sleep(50);
            File.Replace(tempPath, destPath, destinationBackupFileName: null);
        }
    }
}
