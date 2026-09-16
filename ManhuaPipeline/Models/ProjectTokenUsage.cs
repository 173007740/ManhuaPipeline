namespace ManhuaPipeline.Models;

public class ProjectTokenUsage
{
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public long UsedTokens { get; set; }
    public long TotalSeconds { get; set; }
}
