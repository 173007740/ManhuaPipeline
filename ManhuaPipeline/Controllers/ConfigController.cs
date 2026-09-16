using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "deepseek", "qwen", "gpt", "volcano_video", "comfyui", "mediakit", "image"
    };

    private readonly DbService _db;
    public ConfigController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private static object? ToSafeConfig(LLMConfig? config)
    {
        if (config == null) return null;
        return new
        {
            config.Provider,
            config.ApiUrl,
            config.ModelName,
            config.ThinkingMode,
            config.IsActive,
            config.AutoEnhance,
            HasApiKey = !string.IsNullOrWhiteSpace(config.ApiKey)
        };
    }

    [HttpGet("llm")]
    public IActionResult GetLLMConfigs()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var deepseek = _db.GetActiveConfig(uid, "deepseek");
        var qwen = _db.GetActiveConfig(uid, "qwen");
        var gpt = _db.GetActiveConfig(uid, "gpt");
        var activeProvider = _db.GetActiveLLMProvider(uid);
        return Ok(new { deepseek = ToSafeConfig(deepseek), qwen = ToSafeConfig(qwen), gpt = ToSafeConfig(gpt), activeProvider });
    }
    [HttpGet("llm/{provider}")]
    public IActionResult GetLLMConfig(string provider)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        provider = (provider ?? "").Trim().ToLowerInvariant();
        if (!SupportedProviders.Contains(provider)) return BadRequest(new { message = "不支持的配置类型" });
        return Ok(ToSafeConfig(_db.GetActiveConfig(uid, provider)));
    }

    [HttpPost("llm")]
    public IActionResult SaveLLMConfig([FromBody] LLMConfig config)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        config.Provider = (config.Provider ?? "").Trim().ToLowerInvariant();
        if (!SupportedProviders.Contains(config.Provider)) return BadRequest(new { message = "不支持的配置类型" });
        config.ApiKey = (config.ApiKey ?? "").Trim();
        config.UserId = uid;
        _db.SaveLLMConfig(config);
        return Ok(new { message = "配置保存成功", config = ToSafeConfig(_db.GetActiveConfig(uid, config.Provider)) });
    }
    [HttpGet("video")]
    public IActionResult GetVideoConfigs()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var engine = _db.GetActiveVideoEngine(uid);
        var volcano = _db.GetActiveConfig(uid, "volcano_video");
        var comfyui = _db.GetActiveConfig(uid, "comfyui");
        string? workflow = null;
        try { var wfPath = ComfyService.WorkflowPath(uid); if (System.IO.File.Exists(wfPath)) workflow = System.IO.File.ReadAllText(wfPath); } catch { }
        return Ok(new { engine, volcano = ToSafeConfig(volcano), comfyui = ToSafeConfig(comfyui), workflow });
    }

    [HttpPost("video-engine")]
    public IActionResult SetVideoEngine([FromBody] VideoEngineRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var engine = (req?.Engine ?? "").Trim();
        if (engine != "volcano" && engine != "comfyui")
            return BadRequest(new { message = "视频生成引擎只支持 volcano 或 comfyui" });
        _db.SetActiveVideoEngine(uid, engine);
        return Ok(new { message = "已切换视频生成引擎: " + engine });
    }

    [HttpPost("comfyui/workflow")]
    public IActionResult SaveComfyWorkflow([FromBody] ComfyWorkflowRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var workflow = (req?.Workflow ?? "").Trim();
        if (workflow.Length == 0)
            return BadRequest(new { message = "工作流内容不能为空" });
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(workflow);
        }
        catch
        {
            return BadRequest(new { message = "工作流不是合法的 JSON，请粘贴 ComfyUI 导出的 API 格式" });
        }
        var dir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "comfyui");
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, uid + ".workflow.json"), workflow);
        return Ok(new { message = "工作流已保存" });
    }
    [HttpPost("llm/active")]
    public IActionResult SetActiveLLMProvider([FromBody] ActiveLLMProviderRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var provider = (req?.Provider ?? "").Trim();
        if (provider != "deepseek" && provider != "qwen" && provider != "gpt")
            return BadRequest(new { message = "大模型只支持 deepseek、qwen 或 gpt" });
        _db.SetActiveLLMProvider(uid, provider);
        return Ok(new { message = "已切换大模型: " + provider });
    }
    [HttpGet("token-usage")]
    public IActionResult GetTokenUsage()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var (quota, used, remaining) = _db.GetTokenUsageStats();
        var canEditQuota = string.Equals(_db.GetUserById(uid)?.Role, "admin", StringComparison.OrdinalIgnoreCase);
        return Ok(new { quotaTokens = quota, usedTokens = used, remainingTokens = remaining, canEditQuota });
    }

    [HttpGet("token-usage/projects")]
    public IActionResult GetProjectTokenUsages()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetProjectTokenUsages(uid));
    }

    [HttpPut("token-usage")]
    public IActionResult SetTokenUsage([FromBody] TokenUsageRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!string.Equals(_db.GetUserById(uid)?.Role, "admin", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "只有管理员可以修改全局 Token 配额" });
        if (req.QuotaTokens <= 0) return BadRequest(new { message = "总量必须大于 0" });
        _db.SetTokenQuota(req.QuotaTokens);
        return Ok(new { message = "保存成功" });
    }

    // ========== 资产提示词模版（Stage 6/7/8/11 提取资产时按它自动生成出图提示词） ==========

    /// <summary>
    /// 读取四类资产的提示词模版。
    /// projectId &gt; 0：返回该剧「生效」的模版（本剧覆盖 → 账号级默认 → 出厂默认），并带上账号级默认值，
    /// 方便界面提示「当前是剧级覆盖 / 账号默认 / 出厂默认」以及「恢复账号默认」。
    /// projectId = 0：读账号级默认模版。
    /// </summary>
    [HttpGet("asset-prompt-templates")]
    public IActionResult GetAssetPromptTemplates([FromQuery] int projectId = 0)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var list = AssetPromptTemplate.AllCategories.Select(c =>
        {
            var tpl = _db.GetAssetPromptTemplate(uid, projectId, c);
            var account = projectId > 0 ? _db.GetAssetPromptTemplate(uid, 0, c) : tpl;
            var source = tpl.TemplateId == 0 ? "default" : (tpl.ProjectId > 0 ? "project" : "account");
            return new
            {
                category = c,
                categoryName = AssetPromptTemplate.CategoryName(c),
                projectId,
                styleLock = tpl.StyleLock,
                negativePrompt = tpl.NegativePrompt,
                ruleText = tpl.RuleText,
                enabled = tpl.Enabled,
                source,
                isProjectOverride = tpl.ProjectId > 0,
                isDefault = tpl.TemplateId == 0,
                accountStyleLock = account.StyleLock,
                accountNegativePrompt = account.NegativePrompt,
                accountRuleText = account.RuleText,
                accountEnabled = account.Enabled,
                updatedAt = tpl.TemplateId == 0 ? (DateTime?)null : tpl.UpdatedAt
            };
        }).ToList();
        return Ok(list);
    }

    /// <summary>保存某类资产的提示词模版。projectId=0 存账号级默认（所有剧共用），&gt;0 存该剧专属覆盖。</summary>
    [HttpPost("asset-prompt-templates")]
    public IActionResult SaveAssetPromptTemplate([FromBody] AssetPromptTemplateRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var category = (req?.Category ?? "").Trim().ToLowerInvariant();
        if (!AssetPromptTemplate.IsValidCategory(category))
            return BadRequest(new { message = "资产类型只能是 characters / props / environments / effects" });
        var projectId = req!.ProjectId.GetValueOrDefault();
        if (projectId < 0) return BadRequest(new { message = "项目 ID 不合法" });
        _db.SaveAssetPromptTemplate(new AssetPromptTemplate
        {
            UserId = uid,
            ProjectId = projectId,
            Category = category,
            StyleLock = req.StyleLock,
            NegativePrompt = req.NegativePrompt,
            RuleText = req.RuleText,
            Enabled = req.Enabled
        });
        var scope = projectId > 0 ? "本剧模版" : "账号默认模版";
        return Ok(new { message = AssetPromptTemplate.CategoryName(category) + scope + "已保存" });
    }

    /// <summary>
    /// 恢复默认。
    /// projectId &gt; 0：删除该剧的模版覆盖，回落到账号级默认（账号级没存过则回出厂默认）。
    /// projectId = 0：把账号级默认模版写成出厂默认值。
    /// </summary>
    [HttpPost("asset-prompt-templates/{category}/reset")]
    public IActionResult ResetAssetPromptTemplate(string category, [FromQuery] int projectId = 0)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        category = (category ?? "").Trim().ToLowerInvariant();
        if (!AssetPromptTemplate.IsValidCategory(category))
            return BadRequest(new { message = "资产类型不合法" });
        var name = AssetPromptTemplate.CategoryName(category);
        if (projectId > 0)
        {
            _db.DeleteProjectAssetPromptTemplate(uid, projectId, category);
            return Ok(new { message = name + "已恢复为账号默认模版" });
        }
        var def = AssetPromptTemplateDefaults.Create(category);
        def.UserId = uid;
        _db.SaveAssetPromptTemplate(def);
        return Ok(new { message = name + "账号默认模版已恢复出厂默认" });
    }
}

public class AssetPromptTemplateRequest
{
    public string? Category { get; set; }

    /// <summary>0 或缺省 = 账号级默认模版；&gt;0 = 该剧专属模版。</summary>
    public int? ProjectId { get; set; }

    public string? StyleLock { get; set; }
    public string? NegativePrompt { get; set; }
    public string? RuleText { get; set; }
    public bool Enabled { get; set; } = true;
}

public class TokenUsageRequest
{
    public long QuotaTokens { get; set; }
}
public class ActiveLLMProviderRequest
{
    public string? Provider { get; set; }
}
public class VideoEngineRequest
{
    public string? Engine { get; set; }
}

public class ComfyWorkflowRequest
{
    public string? Workflow { get; set; }
}
