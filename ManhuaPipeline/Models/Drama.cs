namespace ManhuaPipeline.Models;

public class Drama
{
    public int DramaId { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverImage { get; set; }
    /// <summary>
    /// 整部剧的剧本素材（漫剧级）。创建 / 编辑漫剧时录入——那时一集都还没建，
    /// 只能是整部的。某一集打开流水线立项那格时会预填进来，人删到自己那一集再用。
    /// 见 Database\Upgrade_漫剧剧本素材.sql
    /// </summary>
    public string? ScriptContent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
