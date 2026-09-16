namespace ManhuaPipeline.Models;

/// <summary>
/// Director V2 校验结果：100 分制 + 硬失败 + 结构化违规列表。
/// Passed 表示可以进入后续阶段；NeedsReview 表示返工耗尽后仍失败，
/// 已保存最高分版本供人工复查，但不再阻塞流水线。
/// </summary>
public class DirectorValidationResult
{
    public bool Passed { get; set; }

    public bool HasHardFailure { get; set; }

    public bool NeedsReview { get; set; }

    public int Score { get; set; }

    /// <summary>PASS / PASS WITH WARNING / FAIL。</summary>
    public string Verdict { get; set; } = "PASS";

    public List<DirectorViolation> Violations { get; set; } = [];

    public static DirectorValidationResult Pass() => new()
    {
        Passed = true,
        Score = 100,
        Verdict = "PASS"
    };
}
