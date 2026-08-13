namespace LocalTypeAssist.Services;

public static class AppLog
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(Models.AppSettings.DataRoot, "logs");
    public static string LogPath => Path.Combine(LogDirectory, "localtypeassist.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var lines = new List<string>
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}"
            };
            if (exception is not null)
            {
                lines.Add(exception.ToString());
            }
            lines.Add(string.Empty);

            lock (Gate)
            {
                File.AppendAllLines(LogPath, lines);
            }
        }
        catch
        {
            // Logging must never become a reason for the app to fail.
        }
    }
}
