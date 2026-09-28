namespace ManhuaPipeline.Models;

/// <summary>
/// 图片风格库里的一条风格。无限画布节点的「图片风格」下拉直接读这张表，
/// 不再从所属项目上取画风 —— 不挂项目的独立灵感板也能挑风格。
///
/// StyleImageUrl 只作预览缩略图（挑风格时看着图挑），出图只用 StyleDesc 那段描述。
/// </summary>
public class ImageStyle
{
    public int StyleId { get; set; }
    public string StyleName { get; set; } = "";
    public string StyleDesc { get; set; } = "";
    public string? StyleImageUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
