namespace ManhuaPipeline.Models;

/// <summary>
/// 资产卡出图任务（后台队列）。
/// 出图不再占用 HTTP 请求：入队后由 AssetImageQueueService 依次执行，
/// 页面关闭/刷新/换设备都会继续跑完，前端回来查状态即可。
/// Status: queued（排队中）→ running（出图中）→ completed / failed。
/// </summary>
public class AssetImageTask
{
    public int TaskId { get; set; }
    public int ProjectId { get; set; }
    public int UserId { get; set; }

    /// <summary>characters / props / environments / effects</summary>
    public string Category { get; set; } = "";

    public int AssetId { get; set; }
    public string? AssetName { get; set; }

    public string Status { get; set; } = "queued";

    /// <summary>临时覆盖的提示词正文（临时换装等，只作用于本次出图）。</summary>
    public string? PromptOverride { get; set; }

    /// <summary>临时覆盖的负面提示词。</summary>
    public string? NegativeOverride { get; set; }

    /// <summary>追加在提示词末尾的额外要求。</summary>
    public string? ExtraPrompt { get; set; }

    /// <summary>出图尺寸，空 = 按 ImageService.AssetImageSize（16:9）。</summary>
    public string? Size { get; set; }

    // ---- 参考图派生（角色换装/换形态）：SourceImageUrl 非空即走「图生图」路径 ----
    // 五个字段全为空 = 原来的纯文生图路径，行为完全不变。

    /// <summary>来源角色卡所在项目（弱引用；与 SourceAssetId 同时为空 = 来源是手动上传的图）。</summary>
    public int? SourceProjectId { get; set; }

    /// <summary>来源角色卡 Id（弱引用，源卡被删也照常出图）。</summary>
    public int? SourceAssetId { get; set; }

    /// <summary>来源角色图（站内路径）。非空时作为第 1 张参考图提交。</summary>
    public string? SourceImageUrl { get; set; }

    /// <summary>服装参考图（站内路径，可空）。非空时作为第 2 张参考图提交。</summary>
    public string? GarmentImageUrl { get; set; }

    /// <summary>派生备注（如「冬季版」），会写进血缘记录。</summary>
    public string? SourceNote { get; set; }

    public string? ImageUrl { get; set; }
    public int? LibraryAssetId { get; set; }
    public string? UsedPrompt { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
