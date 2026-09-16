namespace ManhuaPipeline.Models;

public class StageData
{
    public int StageId { get; set; }
    public int ProjectId { get; set; }
    public int StageNumber { get; set; }
    public string? Content { get; set; }
    public string? LlmResponse { get; set; }

    /// <summary>结构化产物（L1 故事基线 JSON）。只落在阶段 1 这一行，阶段 2/3 直接复用渲染，不重复调用大模型。</summary>
    public string? StructuredJson { get; set; }

    public string Status { get; set; } = "pending";
    public int CurrentBatch { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
