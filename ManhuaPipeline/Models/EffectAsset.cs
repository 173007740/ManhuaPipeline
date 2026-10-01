namespace ManhuaPipeline.Models;

public class EffectAsset
{
    public int AssetId { get; set; }
    public int ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>提取阶段按模版自动生成的「出图提示词」正文（不含统一风格，出图时拼接）。</summary>
    public string? ImagePrompt { get; set; }

    /// <summary>该资产专属的负面提示词（可空，出图时再叠加模版里的统一负面词）。</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>它在漫剧里的身份（跨集复用用）。手工加的资产可以没有。</summary>
    public int? IdentityId { get; set; }

    /// <summary>这个身份的定妆图出自第几集（读表时顺带查出，资产卡上显示）。</summary>
    public int? AnchorEpisode { get; set; }
}
