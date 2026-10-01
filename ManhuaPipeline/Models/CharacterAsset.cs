namespace ManhuaPipeline.Models;

public class CharacterAsset
{
    public int AssetId { get; set; }
    public int ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? Attributes { get; set; }

    /// <summary>提取阶段按模版自动生成的「出图提示词」正文（不含统一风格，出图时拼接）。</summary>
    public string? ImagePrompt { get; set; }

    /// <summary>该资产专属的负面提示词（可空，出图时再叠加模版里的统一负面词）。</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>
    /// 它在漫剧里的身份（见 DramaAssetIdentities）：同一部剧里「杨彦刚」只有这一个身份，
    /// 每集这条只是这个身份在这一集的一份实例。手工加的资产可以没有。
    /// </summary>
    public int? IdentityId { get; set; }

    /// <summary>
    /// 这个身份的定妆图出自第几集（读表时顺带查出来，资产卡上直接显示）。
    /// 有值 = 这一集出图会照着那一集那张出；为空 = 全剧还没给这个身份定过妆。
    /// </summary>
    public int? AnchorEpisode { get; set; }
}
