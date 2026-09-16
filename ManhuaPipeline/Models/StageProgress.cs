namespace ManhuaPipeline.Models;

public class StageProgressUpdate
{
    public int Done { get; set; }
    public int Total { get; set; }
    public string? CurrentUnit { get; set; }
    public string? CurrentPhase { get; set; }
    public string? Message { get; set; }
}

public class StageProgressState
{
    public int ProjectId { get; set; }
    public int StageNumber { get; set; }
    public string Status { get; set; } = "processing";
    public int Done { get; set; }
    public int Total { get; set; }
    public string? CurrentUnit { get; set; }
    public string? CurrentPhase { get; set; }
    public string? Message { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<string> ProgressLog { get; set; } = [];
}
