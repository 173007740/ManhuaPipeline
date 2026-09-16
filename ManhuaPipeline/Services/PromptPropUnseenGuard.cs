using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 道具出镜守卫：移除 @图N 行中正文时间块未出现/未使用的道具参考段。
/// 与 PromptPropRefGuard（补全正文出镜道具）互补，防止 LLM 把在场角色的标志性武器
/// 误绑成参考图（如镜头正文未拔剑，却出现 [玄冰细剑]道具参考）。
/// 规则依据：@图N 参考图行的道具必须是本镜头时间块内实际出现或使用的道具；
/// 正文未出镜的道具禁止写入参考图行。
/// </summary>
public static class PromptPropUnseenGuard
{
    private static readonly Regex ShotHeaderRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");

    public static string Apply(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;

        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var parts = ShotHeaderRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(ProcessShot(part));
        }
        return builder.ToString();
    }

    private static string ProcessShot(string shotText)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var atImageIndex = lines.FindIndex(SeedanceAtImageLine.IsAtImageLine);
        if (atImageIndex < 0) return shotText;

        var segments = SeedanceAtImageLine.Parse(lines[atImageIndex]);
        if (segments.Count == 0) return shotText;

        // 正文 = 除 @图N 行以外的所有行（时间块、动作、台词、场景描述）
        var body = string.Join("\n", lines.Where((l, i) => i != atImageIndex));
        var kept = segments
            .Where(s => s.Category != "道具" || body.Contains(s.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 无道具段被移除则保持原样（避免无谓重写）
        if (kept.Count == segments.Count) return shotText;

        lines[atImageIndex] = SeedanceAtImageLine.Rebuild(kept);
        return string.Join("\n", lines);
    }
}
