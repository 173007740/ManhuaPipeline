using System.Text;
using System.Text.Json;
using ManhuaPipeline.Models;
using Microsoft.Extensions.Configuration;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// Director V2 SemanticValidator：LLM 检查程序规则查不到的语义项——
/// 戏剧目的是否落实、核心爽点是否成立、情绪曲线、摄影是否服务主体、
/// 特效不纳入检查（允许自由新增）；失败时返回空结果，不阻塞流水线。
/// </summary>
public class DirectorSemanticValidator
{
    private readonly LLMService _llm;
    private readonly bool _enabled;

    public DirectorSemanticValidator(LLMService llm, IConfiguration config)
    {
        _llm = llm;
        _enabled = config.GetValue("Director:SemanticValidation:Enabled", true);
    }

    public async Task<DirectorValidationResult> ValidateAsync(
        DirectorPlan? plan,
        StageUnit unit,
        string storyboard,
        string apiUrl,
        string apiKey,
        string model,
        string? thinkingMode = null)
    {
        if (!_enabled || plan == null || string.IsNullOrWhiteSpace(storyboard))
            return DirectorValidationResult.Pass();

        try
        {
            var userMessage = new StringBuilder();
            userMessage.AppendLine("【DirectorPlan 导演决策】");
            userMessage.AppendLine(DirectorPromptBuilder.BuildDecisionSection(plan));
            userMessage.AppendLine();
            userMessage.AppendLine("【当前单元】");
            userMessage.AppendLine(unit.RawText);
            userMessage.AppendLine();
            userMessage.AppendLine("【分镜脚本】");
            userMessage.AppendLine(storyboard);

            var raw = await _llm.CallAsync(
                apiUrl, apiKey, model, BuildSystemPrompt(), userMessage.ToString(),
                jsonMode: true, temperature: 0.2, thinkingMode: thinkingMode);
            return Parse(raw);
        }
        catch
        {
            return DirectorValidationResult.Pass();
        }
    }

    private static string BuildSystemPrompt()
    {
        return """
你是一个导演语义验收员。你的任务不是检查节拍/镜头数/回合数等程序规则，而是判断分镜是否真正落实 DirectorPlan 的语义意图。

【检查范围】
1. DRAMATIC_PURPOSE：戏剧目的是否被镜头真正表现。
2. EMOTION_CURVE：情绪曲线（如 冷静 → 压迫 → 期待）是否在镜头推进中体现。
3. CORE_PAYOFF：核心爽点是否成立并落镜，观众能否在画面里看到这个爽点。
4. CAMERA_STRATEGY：摄影策略是否服务核心主体、是否把镜头给到该给的人。
5. 特效不纳入检查：不限制特效量、峰值、何时给/何时收，允许分镜自由新增特效；技能归属与技能改动由程序规则校验。

【输出要求】
只输出 JSON，不要 Markdown 代码块，不要解释：
{
  "violations": [
    {
      "code": "DRAMATIC_PURPOSE | EMOTION_CURVE | CORE_PAYOFF | CAMERA_STRATEGY | COMBAT_LOGIC",
      "severity": "Error | Warning",
      "message": "一句话说明问题",
      "expected": "导演要求",
      "actual": "分镜实际表现",
      "combatBeatId": "如 Beat03，不适用则空字符串",
      "shotId": "如 1.1-3，不适用则空字符串",
      "repairInstruction": "给分镜师的定向修复要求"
    }
  ]
}

【规则】
- 没有问题时 violations 为空数组。
- 保守判断：只有明显违背导演语义才报违规；措辞不同但意思一致不报。
- 每条违规必须能定位、能修复；repairInstruction 不得为空。
- 单元标题、控制行（控制模式/打斗模板/技能/运镜原子/单元类型）不是镜头，禁止因这些行没有「起始画面/结束画面」报违规；只检查「镜头编号」开头的实际镜头。
- Error 表示明显违背且必须返工；Warning 表示力度不够但不必整段重写。
""";
    }

    private static DirectorValidationResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DirectorValidationResult.Pass();
        var json = ExtractJsonObject(raw);
        if (json == null) return DirectorValidationResult.Pass();

        SemanticResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<SemanticResponse>(json, JsonOptions);
        }
        catch
        {
            return DirectorValidationResult.Pass();
        }

        var violations = (response?.Violations ?? new List<SemanticViolation>())
            .Where(v => !string.IsNullOrWhiteSpace(v.Message) && !IsIgnoredSemanticCode(v.Code))
            .Select(Map)
            .ToList();
        if (violations.Count == 0) return DirectorValidationResult.Pass();

        var score = 100;
        foreach (var violation in violations)
            score = Math.Max(0, score - DirectorRuleValidator.DeductForSemantic(violation));
        var hasError = violations.Any(v => v.Severity == "Error");
        return new DirectorValidationResult
        {
            Passed = !hasError && score >= 80,
            Score = score,
            Verdict = DirectorRuleValidator.BuildVerdict(hasError, score),
            Violations = violations
        };
    }

    private static bool IsIgnoredSemanticCode(string? code)
    {
        return string.Equals(code, "VFX_STRATEGY", StringComparison.OrdinalIgnoreCase);
    }

    private static DirectorViolation Map(SemanticViolation v)
    {
        var code = (v.Code ?? "").Trim().ToUpperInvariant();
        if (code is not ("DRAMATIC_PURPOSE" or "EMOTION_CURVE" or "CORE_PAYOFF" or "CAMERA_STRATEGY"))
            code = "COMBAT_LOGIC";
        var severity = string.Equals(v.Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning";
        return new DirectorViolation
        {
            Code = code,
            Severity = severity,
            Message = v.Message ?? "",
            Expected = v.Expected ?? "",
            Actual = v.Actual ?? "",
            CombatBeatId = v.CombatBeatId ?? "",
            ShotId = v.ShotId ?? "",
            RepairInstruction = v.RepairInstruction ?? ""
        };
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw.Substring(start, end - start + 1);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class SemanticResponse
    {
        public List<SemanticViolation> Violations { get; set; } = [];
    }

    private sealed class SemanticViolation
    {
        public string Code { get; set; } = "";
        public string Severity { get; set; } = "Warning";
        public string Message { get; set; } = "";
        public string Expected { get; set; } = "";
        public string Actual { get; set; } = "";
        public string CombatBeatId { get; set; } = "";
        public string ShotId { get; set; } = "";
        public string RepairInstruction { get; set; } = "";
    }
}
