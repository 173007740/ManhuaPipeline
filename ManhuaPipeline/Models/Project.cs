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
    public int EpisodeCount { get; set; } = 1;
    public int CurrentBatch { get; set; } = 1;
    public string? CoverImage { get; set; }
    /// <summary>视频画风（→ VideoStyles.StyleId）：出视频时作为【项目风格】整段写进提示词。</summary>
    public int? StyleId { get; set; }
    /// <summary>资产画风（→ ImageStyles.StyleId）：只管四类资产的出图，跟视频画风分开维护。
    /// 留空时出图退回视频画风，老项目行为不变。</summary>
    public int? ImageStyleId { get; set; }
    public string? Tags { get; set; }
    /// <summary>资产图同步进参考图库时写进图库的「类型」大类（动漫 / 写实 / 游戏 / 仙侠）；留空则沿用资产分类（角色/道具/环境/特效）。</summary>
    public string? LibraryCategory { get; set; }
    /// <summary>项目内容类型：drama=短剧（默认）/ ad=广告 / mv=歌曲MV。
    /// 只决定结构性规则（时间轴来源、是否按线性叙事、产品如何出现、镜头时长如何取档）；
    /// 画风、题材、去水与动作过程化等画面质量规则对三种类型一律通用，不按此字段分叉。</summary>
    public string ProjectType { get; set; } = "drama";
    public string VideoRatio { get; set; } = "16:9";
    public bool VideoWatermark { get; set; }
    public bool VideoAudio { get; set; } = true;
    public string VideoResolution { get; set; } = "720p";
    /// <summary>出图像素档（百万像素）：ComfyUI 引擎的画幅按它算宽高，可选 0.5/1/1.5/2，默认 1。</summary>
    public double VideoMegapixels { get; set; } = 1;
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

