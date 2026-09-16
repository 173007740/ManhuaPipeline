namespace ManhuaPipeline.Models;

/// <summary>
/// 资产出图提示词模版：按「用户 + 项目 + 资产类型（characters/props/environments/effects）」存。
/// ProjectId = 0 表示账号级默认模版（所有剧共用），>0 表示该剧专属覆盖。
/// 生效顺序：本剧模版 → 账号级默认 → 出厂默认（<see cref="Services.AssetPromptTemplateDefaults"/>）。
/// 用途：Stage 6/7/8/11 提取资产时，把生效模版注入提示词，
/// 让 LLM 直接按模版产出每个资产的「出图提示词」；出图时再拼上统一风格与负面词。
/// </summary>
public class AssetPromptTemplate
{
    /// <summary>分类键：characters / props / environments / effects。</summary>
    public const string CategoryCharacters = "characters";
    public const string CategoryProps = "props";
    public const string CategoryEnvironments = "environments";
    public const string CategoryEffects = "effects";

    public static readonly string[] AllCategories =
    {
        CategoryCharacters, CategoryProps, CategoryEnvironments, CategoryEffects
    };

    /// <summary>分类中文名（用于提示词与界面展示）。</summary>
    public static string CategoryName(string category) => category switch
    {
        CategoryCharacters => "角色",
        CategoryProps => "道具",
        CategoryEnvironments => "环境",
        CategoryEffects => "特效",
        _ => category
    };

    public static bool IsValidCategory(string? category) =>
        !string.IsNullOrWhiteSpace(category) && AllCategories.Contains(category.Trim().ToLowerInvariant());

    public int TemplateId { get; set; }
    public int UserId { get; set; }

    /// <summary>0 = 账号级默认模版（所有剧共用）；>0 = 该剧专属覆盖。</summary>
    public int ProjectId { get; set; }

    public string Category { get; set; } = "";

    /// <summary>该模版是否来自剧级覆盖（还是账号级默认 / 出厂默认）。</summary>
    public bool IsProjectOverride => ProjectId > 0;

    /// <summary>统一视觉风格（硬锁定）：出图时自动追加到提示词末尾。</summary>
    public string? StyleLock { get; set; }

    /// <summary>统一负面提示词：出图时自动追加。</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>该类资产的提示词规则：提取时指导 LLM 怎么写「出图提示词」正文。</summary>
    public string? RuleText { get; set; }

    /// <summary>该分类是否启用模版化提取；关闭后该分类沿用旧的「描述拼提示词」方式。</summary>
    public bool Enabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; }
}
