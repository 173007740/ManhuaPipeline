namespace ManhuaPipeline.Models;

/// <summary>
/// Director V2 结构化违规：告诉系统到底哪里没听导演、期望是什么、
/// 实际是什么，以及定向返工时应该怎么改。
/// </summary>
public class DirectorViolation
{
    /// <summary>违规代码，如 COMBAT_BEAT_MISSING / ROUND_COUNT_MISMATCH。</summary>
    public string Code { get; set; } = "";

    /// <summary>Error / Warning。Error 会让校验直接失败，Warning 只扣分。</summary>
    public string Severity { get; set; } = "Error";

    public string Message { get; set; } = "";

    public string Expected { get; set; } = "";

    public string Actual { get; set; } = "";

    /// <summary>定位到战斗节拍，如 Beat03。</summary>
    public string CombatBeatId { get; set; } = "";

    /// <summary>定位到镜头编号，如 1.1-3。</summary>
    public string ShotId { get; set; } = "";

    /// <summary>给分镜 LLM 的定向修复要求。</summary>
    public string RepairInstruction { get; set; } = "";
}
