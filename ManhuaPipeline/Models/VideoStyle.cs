namespace ManhuaPipeline.Models;

public class VideoStyle
{
    public int StyleId { get; set; }
    public string StyleName { get; set; } = "";
    public string StylePrompt { get; set; } = "";
    /// <summary>
    /// 分类（2D动画 / 3D动画 / 真人影视 / 漫画与插画…）。只用来给挑风格时分组，不进提示词。
    /// 老风格没分类，为 null，页面上显示「未分类」。
    /// </summary>
    public string? Category { get; set; }
    /// <summary>
    /// 这条风格自带的反向提示词（这一路影像语言该避开什么：真人摄影 / 塑料人物 / 线条闪烁 /
    /// 五官漂移 / 镜头抖动 / 随机文字水印…）。视频风格 16 种每段正向都配了段反向。
    /// 老风格没有，为 null，出图时负面词照旧。
    /// </summary>
    public string? StyleNegative { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
