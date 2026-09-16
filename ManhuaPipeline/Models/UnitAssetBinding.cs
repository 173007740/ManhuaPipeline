namespace ManhuaPipeline.Models;

/// <summary>分集细化单元（Stage 4 【单元X.Y】）↔ 项目资产 的确定引用绑定行（Stage 4 完成后解析写入，供 Stage 5 分镜继承）。</summary>
public class UnitAssetBinding
{
    public int BindingId { get; set; }
    public int ProjectId { get; set; }
    public int EpisodeNumber { get; set; }
    public string UnitNumber { get; set; } = "";
    /// <summary>Character / Environment / Prop / Effect</summary>
    public string Category { get; set; } = "Character";
    public int AssetId { get; set; }
    public string Name { get; set; } = "";
    public bool HasImage { get; set; }
    /// <summary>该单元内参考图顺序（Stage 9 据此生成 @图片N）</summary>
    public int SortOrder { get; set; }
}
