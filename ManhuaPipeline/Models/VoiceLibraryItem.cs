namespace ManhuaPipeline.Models;

/// <summary>音色库条目：一段可跨项目复用的参考音频（H3 音色参考用）。</summary>
public class VoiceLibraryItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>大类：动漫 / 游戏 / 写实 / 仙侠（与资产库类型同一套，可为空=通用）。</summary>
    public string? Category { get; set; }
    public string? Tag { get; set; }
    public string? Note { get; set; }
    public string AudioUrl { get; set; } = "";
    /// <summary>封面图（可空）：显示在音色卡片背景上，便于一眼区分音色。</summary>
    public string? ImageUrl { get; set; }
    public string? OriginalFileName { get; set; }
    public int? DurationSec { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
