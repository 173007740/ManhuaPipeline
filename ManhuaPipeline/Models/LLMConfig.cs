namespace ManhuaPipeline.Models;

public class LLMConfig
{
    public int ConfigId { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? ApiUrl { get; set; }
    public string? ModelName { get; set; }
    public string? ThinkingMode { get; set; }
    public bool IsActive { get; set; }
    public bool AutoEnhance { get; set; }

    /// <summary>
    /// 配置页上的显示别名（如「中转 gpt-image」「方舟 seedream 2K」）。
    /// 只有出图（Provider='image'）用得上：同一个用户可以配多个出图渠道并存，
    /// 没名字的话列表里就是一堆一样的「gpt-image-1」，分不清哪个是哪个。
    /// 为空时页面回退显示模型名。
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>出图渠道在配置页列表里的排序（小的在前）。同样是出图专用。</summary>
    public int SortOrder { get; set; }
}
