using System.Text;
using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;

namespace ManhuaPipeline.Services.Combat;

/// <summary>
/// 把“单元/镜头时长 + 节拍数”翻译成给分镜 LLM 的时间预算。
/// 只给建议窗口与“时间轴必须铺满镜头时长”的硬约束，不限制回合密度上限。
/// </summary>
public static class CombatTimeBudgetBuilder
{
    public static string Build(StageUnit unit, IReadOnlyList<PackedShot>? packedShots, int beatCount = 0)
    {
        var duration = unit.Duration is 5 or 11 or 15 ? unit.Duration : 11;
        var sb = new StringBuilder();
        sb.AppendLine("【时间预算】");
        sb.AppendLine($"- 本单元时长: {duration} 秒；节拍数: {Math.Max(1, beatCount)}");
        if (packedShots != null && packedShots.Count > 0)
        {
            sb.AppendLine("- ShotPacker 已按镜头给出建议时间轴；每个镜头必须把时间轴连续铺满该镜头的「镜头时长」，禁止只写到半程。");
            foreach (var shot in packedShots)
            {
                sb.AppendLine($"  - {shot.ShotId}: 建议 {shot.DurationSeconds} 秒，覆盖 {string.Join(",", shot.CombatBeatIds)}");
            }
        }
        else
        {
            sb.AppendLine("- 未提供打包镜头时，按模板自由编排，但仍必须遵守下面的时长铁律。");
        }

        sb.AppendLine();
        sb.AppendLine("【节奏密度参考（非硬上限）】");
        sb.AppendLine("- 5 秒: 2-3 个可读攻防交换，或 5-8 个快闪节拍；11 秒: 3-5 个交换，或 6-10 个快闪；15 秒: 5-8 个交换，或 10-14 个快闪。");
        sb.AppendLine("- 高手过招允许 1 秒内多个动作/回合，密度由动作链与内容决定；禁止因档位小就把多回合压成“一次挥击+特效”。");

        sb.AppendLine();
        sb.AppendLine("【时长铁律】");
        sb.AppendLine("- 每个镜头的「镜头时长」只能选 5/11/15 秒；「镜头时间轴」必须从 0 秒连续覆盖到该镜头时长结束，时间轴总和既不能小于镜头时长，也不能超出。");
        sb.AppendLine("- 慢动作/顿帧/定格（如 0.2-0.4 秒命中瞬间）计入总时长，禁止在镜头时长之外另算时间。");
        return sb.ToString();
    }
}
