using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// 从剧本头部解析"建议时长：3分05秒—3分20秒"这类目标时长区间。
/// 未声明目标时长时返回 null（不启用时长硬预算，保持旧行为）。
/// </summary>
public static class DurationBudgetParser
{
    public readonly record struct DurationBudget(int MinSeconds, int MaxSeconds, string Display);

    public static DurationBudget? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 1) X分Y秒 — X分Y秒（允许"X分钟"、秒部分可缺省，允许 全角—/半角- 等分隔）
        var m = Regex.Match(
            text,
            @"(\d+)\s*分(?:钟)?\s*(?:(\d+)\s*秒)?\s*[—\-–~至到]\s*(\d+)\s*分(?:钟)?\s*(?:(\d+)\s*秒)?");
        if (m.Success)
        {
            var lo = ToSeconds(m.Groups[1].Value, m.Groups[2].Value);
            var hi = ToSeconds(m.Groups[3].Value, m.Groups[4].Value);
            if (lo > 0 && hi > 0) return Make(lo, hi);
        }

        // 2) 纯秒区间，如 185—200秒
        m = Regex.Match(text, @"(\d+)\s*秒\s*[—\-–~至到]\s*(\d+)\s*秒");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var s1) && int.TryParse(m.Groups[2].Value, out var s2))
            return Make(s1, s2);

        // 3) 单一目标，如"时长：3分钟" / "3分05秒"
        m = Regex.Match(text, @"(\d+)\s*分(?:钟)?\s*(?:(\d+)\s*秒)?");
        if (m.Success)
        {
            var single = ToSeconds(m.Groups[1].Value, m.Groups[2].Value);
            if (single > 0) return Make(single, single);
        }

        return null;
    }

    private static int ToSeconds(string minutes, string seconds)
    {
        if (!int.TryParse(minutes, out var mins)) return 0;
        var secs = int.TryParse(seconds, out var s) ? s : 0;
        return mins * 60 + secs;
    }

    private static DurationBudget Make(int lo, int hi)
    {
        if (lo < 0) lo = 0;
        if (hi < lo) (lo, hi) = (hi, lo);
        return new DurationBudget(lo, hi, Format(lo) + "—" + Format(hi));
    }

    private static string Format(int seconds)
    {
        var mins = seconds / 60;
        var secs = seconds % 60;
        return secs == 0 ? $"{mins}分钟" : $"{mins}分{secs:00}秒";
    }
}
