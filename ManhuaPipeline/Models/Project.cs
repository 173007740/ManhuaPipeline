namespace ManhuaPipeline.Models;

public class Project
{
    public int ProjectId { get; set; }
    public int DramaId { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? ScriptContent { get; set; }
    /// <summary>分集细化(Stage4)的全片目标总时长（用户显式设置，优先于剧本头部"建议时长"解析）。</summary>
    public string? TargetDurationText { get; set; }
    public int CurrentStage { get; set; } = 0;
    public int EpisodeCount { get; set; } = 12;
    public int CurrentBatch { get; set; } = 1;
    public string? CoverImage { get; set; }
    public int? StyleId { get; set; }
    public string? Tags { get; set; }
    /// <summary>资产图同步进参考图库时写进图库的「类型」大类（动漫 / 写实 / 游戏 / 仙侠）；留空则沿用资产分类（角色/道具/环境/特效）。</summary>
    public string? LibraryCategory { get; set; }
    public string VideoRatio { get; set; } = "16:9";
    public bool VideoWatermark { get; set; }
    public bool VideoAudio { get; set; } = true;
    public string VideoResolution { get; set; } = "720p";
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

