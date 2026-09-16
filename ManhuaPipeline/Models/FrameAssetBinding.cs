namespace ManhuaPipeline.Models;

/// <summary>分镜帧 ↔ 项目资产 的确定引用绑定行（Stage 5 落库后解析写入，Stage 9 直接读取）。</summary>
public class FrameAssetBinding
{
    public int BindingId { get; set; }
    public int ProjectId { get; set; }
    public int FrameId { get; set; }
    /// <summary>Character / Environment / Prop / Effect</summary>
    public string Category { get; set; } = "Character";
    public int AssetId { get; set; }
    public string Name { get; set; } = "";
    public bool HasImage { get; set; }
    /// <summary>该镜头内参考图顺序（Stage9 按此生成 @图片N）</summary>
    public int SortOrder { get; set; }
}
