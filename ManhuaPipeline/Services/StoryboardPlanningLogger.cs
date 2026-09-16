using System.Text;

namespace ManhuaPipeline.Services;

public static class StoryboardPlanningLogger
{
    private static readonly object Sync = new();
    private static string _logPath = ResolveLogPath();

    public static string LogFilePath
    {
        get
        {
            lock (Sync) return _logPath;
        }
    }

    public static void StartRun(int projectId, int stageNumber)
    {
        lock (Sync)
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _logPath = Path.Combine(
                dir ?? ".",
                $"storyboard_planning_p{projectId}_s{stageNumber}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
        }
    }

    public static void Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (Sync)
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_logPath, text, new UTF8Encoding(false));
        }
    }

    private static string ResolveLogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ManhuaPipeline.sln")))
                return Path.Combine(dir.FullName, "logs", "storyboard_planning.log");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "logs", "storyboard_planning.log");
    }
}
