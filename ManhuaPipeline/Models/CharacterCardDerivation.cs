namespace ManhuaPipeline.Models;

/// <summary>
/// 角色卡派生血缘：记录「这张角色卡是由哪张角色图 + 哪件衣服 + 什么提示词生成出来的」。
///
/// 各剧本的资产各管各的、图片不共享，所以这里只「留痕」不建立强关系：
/// 来源是弱引用（SourceProjectId / SourceAssetId 可空），源卡被删也不影响已派生的卡；
/// 源卡已删时，画布仍能按 SourceImageUrl 的快照图画出箭头起点。
/// 对应脚本：Database\Upgrade_角色卡派生.sql
/// </summary>
public class CharacterCardDerivation
{
    public int DerivationId { get; set; }
    public int UserId { get; set; }

    /// <summary>派生出的新角色卡所在项目。</summary>
    public int ProjectId { get; set; }

    /// <summary>派生出的新角色卡（CharacterAssets.AssetId）。</summary>
    public int AssetId { get; set; }

    /// <summary>来源角色卡所在项目；与 SourceAssetId 同时为空 = 来源是手动上传的图。</summary>
    public int? SourceProjectId { get; set; }

    /// <summary>来源角色卡。</summary>
    public int? SourceAssetId { get; set; }

    /// <summary>来源角色图（落盘路径快照，画布上作为箭头起点）。</summary>
    public string SourceImageUrl { get; set; } = "";

    /// <summary>服装参考图（可空：只改风格/姿态时不给）。</summary>
    public string? GarmentImageUrl { get; set; }

    /// <summary>用户填写的描述性提示词（存档以便复现）。</summary>
    public string? Prompt { get; set; }

    /// <summary>生成结果（= 新卡的 ImageUrl）。</summary>
    public string? ResultImageUrl { get; set; }

    /// <summary>备注，如「冬季版」。</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
