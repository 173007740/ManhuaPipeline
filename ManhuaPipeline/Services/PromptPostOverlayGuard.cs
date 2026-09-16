using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 后期叠加守卫（L5）：镜头里出现手机短信、新闻推送、文件、告示、地图、监控画面、
/// 屏幕界面等「文字类画面元素」时，视频模型必然生成乱码或错字。
/// 本守卫在这些镜头的「约束：」行补上留白约束，要求画面只画干净留白的屏幕/纸面，
/// 文字一律后期叠加（规则出处：short-drama-agent 节 16B / 19）。
///
/// 只补约束、不改剧情、不删正文，幂等（已含规则原文则跳过）。
/// </summary>
public static class PromptPostOverlayGuard
{
    private static readonly Regex ShotHeaderRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");

    private static readonly Regex ConstraintLineRegex = new(@"^\s*约束\s*[:：]", RegexOptions.Compiled);

    private static readonly Regex VideoStyleLineRegex = new(@"^\s*视频风格\s*[:：]", RegexOptions.Compiled);

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
        if (shotText.Contains(PostOverlayAnalyzer.RuleText, StringComparison.Ordinal)) return shotText;

        // 只看画面正文：@图N 绑定行里的资产名（如「文件」类道具卡）不算文字类元素，避免误伤
        var body = Regex.Replace(shotText, @"@图\s*\d+\s*\[[^\]\r\n]*\][^\r\n]*", " ");
        var hits = PostOverlayAnalyzer.Detect(body);
        if (hits.Count == 0) return shotText;

        var suffix = "；屏幕留白约束：本镜头含" + PostOverlayAnalyzer.Describe(hits) +
                     "类文字元素，" + PostOverlayAnalyzer.RuleText +
                     "（屏幕、纸面、面板一律画洁净留白画面，文字内容不做画面生成，避免乱码）";

        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

        // 优先追加到「约束：」行
        for (var i = 0; i < lines.Count; i++)
        {
            if (!ConstraintLineRegex.IsMatch(lines[i])) continue;
            lines[i] = lines[i].TrimEnd() + suffix;
            return string.Join("\n", lines);
        }

        // 没有约束行时：插在「视频风格：」行之前，保证视频风格仍是最后一行
        for (var i = 0; i < lines.Count; i++)
        {
            if (!VideoStyleLineRegex.IsMatch(lines[i])) continue;
            lines.Insert(i, "约束：4K，24fps，浅景深，无字幕无BGM，人物比例自然、肢体完整" + suffix);
            return string.Join("\n", lines);
        }

        // 极端兜底：追加到镜头段落末尾
        return shotText.TrimEnd() + "\n约束：4K，24fps，浅景深，无字幕无BGM，人物比例自然、肢体完整" + suffix;
    }
}
