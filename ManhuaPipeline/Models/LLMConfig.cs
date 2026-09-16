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
}
