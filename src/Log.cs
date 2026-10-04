namespace EdgeGlide;

/// <summary>Small log in %APPDATA%\EdgeGlide. Written on state changes and errors only, never per mouse move.</summary>
internal static class Log
{
    const long MaxBytes = 512 * 1024;
    static readonly string FilePath = Path.Combine(ConfigStore.Folder, "edgeglide.log");
    static readonly object Sync = new();
    static int s_errors;

    public static void Info(string message) => Write("info ", message);

    public static void Error(string message, Exception? ex = null)
    {
        if (Interlocked.Increment(ref s_errors) <= 100) Write("ERROR", ex is null ? message : $"{message}: {ex}");
    }

    static void Write(string level, string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Folder);
                var existing = new FileInfo(FilePath);
                if (existing.Exists && existing.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
