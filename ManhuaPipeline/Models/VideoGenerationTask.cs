namespace ManhuaPipeline.Models;

public class VideoGenerationTask
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int PromptId { get; set; }
    public string TaskId { get; set; } = "";
    public string Engine { get; set; } = "volcano";
    public string Status { get; set; } = "pending";
    public string? VideoUrl { get; set; }
    public string? LocalVideoUrl { get; set; }
    public int RequestDuration { get; set; } = 11;
    public string RequestRatio { get; set; } = "16:9";
    public bool RequestWatermark { get; set; } = false;
    public bool RequestGenerateAudio { get; set; } = true;
    public string? ResponseResolution { get; set; }
    public double? ResponseDuration { get; set; }
    public int? EnhanceDuration { get; set; }
    public string? EnhanceResolution { get; set; }
    public int? ResponseUsageTokens { get; set; }
    public int? ResponseSeed { get; set; }
    public string? ApiStatus { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}
