using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// V1.5 兼容壳：V2 的完整校验在 DirectorRuleValidator，
/// 这里保留原 Check / BuildReworkFeedback 签名，避免下游与测试大改。
/// </summary>
public static class DirectorComplianceGuard
{
    public static ComplianceResult Check(DirectorPlan? plan, StageUnit unit, string result)
    {
        if (plan == null || string.IsNullOrWhiteSpace(result)) return ComplianceResult.Pass();

        var validation = DirectorRuleValidator.Validate(plan, unit, result);
        if (validation.Passed) return ComplianceResult.Pass();

        var reasons = validation.Violations
            .Where(v => v.Severity == "Error" || v.Severity == "Warning")
            .Select(v => v.Message)
            .Distinct()
            .ToList();
        if (reasons.Count == 0)
            reasons.Add($"导演合规校验未通过（{validation.Verdict}，得分 {validation.Score}/100）");
        return ComplianceResult.Fail(string.Join("；", reasons));
    }

    public static List<string> BuildReworkFeedback(DirectorPlan? plan, StageUnit unit, string result)
    {
        if (plan == null || string.IsNullOrWhiteSpace(result)) return [];
        var validation = DirectorRuleValidator.Validate(plan, unit, result);
        return DirectorRepairPlanner.BuildRepairFeedback(validation, result);
    }
}

public sealed record ComplianceResult(bool Passed, string? Reason)
{
    public static ComplianceResult Pass() => new(true, null);
    public static ComplianceResult Fail(string reason) => new(false, reason);
}
