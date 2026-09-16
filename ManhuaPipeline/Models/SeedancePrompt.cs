namespace ManhuaPipeline.Models;

public class SeedancePrompt
{
    public int PromptId { get; set; }
    public int ProjectId { get; set; }
    public int? FrameId { get; set; }
    public string PromptText { get; set; } = "";
    public string? PromptTextH3 { get; set; }
    public string? NegativePrompt { get; set; }
    public string? VideoUrl { get; set; }
    public string? LocalVideoUrl { get; set; }
    public string Status { get; set; } = "pending";

    /// <summary>
    /// L5 逐镜状态机状态（注意与 <see cref="Status"/> 区分：Status 是视频生成状态 pending/processing/completed/failed）。
    /// 取值：ready（提示词就绪、引用的资产都有图）/ blocked_by_missing_asset（被缺失资产阻塞）/ accepted（人工验收通过）/ rejected（人工打回）。
    /// </summary>
    public string ShotStatus { get; set; } = "ready";

        public int BatchNumber { get; set; } = 1;
    public int EpisodeNumber { get; set; }
    public string? UnitName { get; set; }
    public string? ShotLabel { get; set; }
    public string? ShotType { get; set; }
    public int Duration { get; set; } = 11;
    public string? ReferenceImages { get; set; }
    public string? ReferenceVideos { get; set; }
    public string? ReferenceAudio { get; set; }
}
