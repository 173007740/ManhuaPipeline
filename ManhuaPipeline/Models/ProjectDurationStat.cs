namespace ManhuaPipeline.Models;

public class ProjectDurationStat
{
    public int ProjectId { get; set; }
    public string Title { get; set; } = "";
    public int PromptCount { get; set; }
    public int TotalDurationSec { get; set; }
    public int CompletedCount { get; set; }
    public int CompletedDurationSec { get; set; }
    public int HasVideoCount { get; set; }
}
