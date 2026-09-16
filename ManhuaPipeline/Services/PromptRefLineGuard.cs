using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 @图N 行合法性守卫：只保留资产名单内的角色/战斗态/群像/道具/特效/技能/场景/风格卡，
/// 移除 LLM 自创且资产库中不存在的绑定（如 [旧灯]人物），并统一补全「参考」二字。
/// </summary>
public static class PromptRefLineGuard
{
    private static readonly Regex ShotHeaderRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");

    private static readonly HashSet<string> GenericEffectNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "拳风", "火墙", "冲击", "罡气", "气劲", "剑气", "护体"
        };

    public static string Apply(string promptText, IEnumerable<string>? validNames)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;

        var valid = BuildValidSet(validNames);
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var parts = ShotHeaderRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(ProcessShot(part, valid));
        }
        return builder.ToString();
    }

    private static HashSet<string> BuildValidSet(IEnumerable<string>? validNames)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "光影质感" };
        foreach (var raw in validNames ?? Array.Empty<string>())
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            valid.Add(name);
            if (name.EndsWith("战斗态", StringComparison.Ordinal))
                valid.Add(name.Substring(0, name.Length - "战斗态".Length));
            if (name.EndsWith("群像", StringComparison.Ordinal))
                valid.Add(name.Substring(0, name.Length - "群像".Length));
        }
        return valid;
    }

    private static string ProcessShot(string shotText, HashSet<string> valid)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var atImageIndex = lines.FindIndex(SeedanceAtImageLine.IsAtImageLine);
        if (atImageIndex < 0) return shotText;

        var segments = SeedanceAtImageLine.Parse(lines[atImageIndex]);
        if (segments.Count == 0) return shotText;

        var kept = segments.Where(s => IsAllowed(s, valid)).ToList();
        if (kept.Count == 0) return shotText;
        lines[atImageIndex] = SeedanceAtImageLine.Rebuild(kept);
        return string.Join("\n", lines);
    }

    private static bool IsAllowed(SeedanceAtImageLine.Segment segment, HashSet<string> valid)
    {
        var name = (segment.Name ?? "").Trim();
        if (name.Length == 0) return false;
        if (segment.Category == "光影质感") return true;
        if (GenericEffectNames.Contains(name)) return true;

        if (segment.Category == "群像" && name.EndsWith("群像", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "群像".Length);
        if (segment.Category == "战斗态" && name.EndsWith("战斗态", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "战斗态".Length);

        return valid.Contains(name);
    }
}
