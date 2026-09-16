using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 技能呐喊守卫：技能释放镜头（@图N 行含 [技能名]特效参考 且技能 Tier≥3）若正文缺少
/// "施法角色怒吼技能名" 的台词，自动在技能爆发时间块末尾补一句呐喊，
/// 让 Seedance 视频里角色开口喊出技能名，提升大招冲击力与情绪张力。
/// 在 PromptDialogueVerbatimGuard 之后运行（此时台词已逐字对齐分镜），
/// 因此补入的呐喊不会因"分镜无此台词"而被删除。
/// </summary>
public static class PromptSkillShoutGuard
{
    private static readonly Regex ShotHeaderRegex = new(
        @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");

    private static readonly Regex TimeBlockRegex = new(
        @"^\s*\[(\d+(?:\.\d+)?)\s*[-–]\s*(\d+(?:\.\d+)?)\s*s\]", RegexOptions.Compiled);

    /// <summary>T1/T2 常规技、状态技不喊技能名，T3 及以上才喊。</summary>
    private const int MinShoutTier = 3;

    private static readonly Regex NoDialogueConstraintRegex = new(
        @"无台词[，,、]?\s*无口型表演[，,、]?\s*无对白", RegexOptions.Compiled);

    private static readonly Regex ShoutQuoteRegex = new(
        @"[“""][^“""]{0,30}?[！!][”""]", RegexOptions.Compiled);

    public static string Apply(
        string promptText,
        IEnumerable<SkillLibraryItem>? lockedSkills,
        IEnumerable<string>? charNames)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        var skills = (lockedSkills ?? Array.Empty<SkillLibraryItem>())
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
            .ToList();
        if (skills.Count == 0) return promptText;

        var names = (charNames ?? Array.Empty<string>())
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var parts = ShotHeaderRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(ProcessShot(part, skills, names));
        }
        return builder.ToString();
    }

    private static string ProcessShot(
        string shotText,
        List<SkillLibraryItem> skills,
        List<string> charNames)
    {
        // 1. 收集本镜头 @图N 行"特效参考"段中属于锁定技能（Tier≥3）的技能
        var atImageSkills = new List<SkillLibraryItem>();
        foreach (var line in shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (!SeedanceAtImageLine.IsAtImageLine(line)) continue;
            foreach (var seg in SeedanceAtImageLine.Parse(line))
            {
                if (seg.Category != "特效") continue;
                var skill = skills.FirstOrDefault(s => string.Equals(s.Name, seg.Name, StringComparison.Ordinal));
                if (skill != null && skill.Tier >= MinShoutTier && !atImageSkills.Contains(skill))
                    atImageSkills.Add(skill);
            }
        }
        if (atImageSkills.Count == 0) return shotText;

        // 2. 选 Tier 最高的技能作为本次呐喊（同 Tier 取 @图N 靠前者）
        var shoutSkill = atImageSkills
            .OrderByDescending(s => s.Tier)
            .ThenBy(s => atImageSkills.IndexOf(s))
            .First();
        var skillName = shoutSkill.Name.Trim();

        // 3. 若正文已存在喊出该技能名的台词（引号内含技能名 + 感叹号）则跳过
        if (ShoutQuoteRegex.IsMatch(shotText) &&
            Regex.IsMatch(shotText, Regex.Escape(skillName)) &&
            Regex.IsMatch(shotText, "[“\"]" + Regex.Escape(skillName) + "[！!]"))
            return shotText;

        // 4. 施法者：优先技能归属角色，并在出镜角色名单中匹配镜头实际叫法（如 岳沉天 → 前世岳沉天）
        var owner = (shoutSkill.OwnerCharacter ?? "").Trim();
        string caster;
        if (owner.Length > 0)
        {
            var matched = charNames
                .Where(n => n.EndsWith(owner, StringComparison.Ordinal)
                            || n.Contains(owner, StringComparison.Ordinal)
                            || owner.Contains(n, StringComparison.Ordinal))
                .OrderByDescending(n => n.Length)
                .FirstOrDefault();
            caster = matched ?? owner;
        }
        else
        {
            // 无归属角色时，取正文中出现最多的出镜角色作为施法者
            var body = Regex.Replace(shotText, @"@图\s*\d+\s*\[[^\]\r\n]*\][^\r\n]*", " ");
            caster = charNames
                .Select(n => new { Name = n, Count = Regex.Matches(body, Regex.Escape(n)).Count })
                .Where(x => x.Count > 0)
                .OrderByDescending(x => x.Count)
                .Select(x => x.Name)
                .FirstOrDefault() ?? "";
            if (caster.Length == 0) return shotText;
        }

        // 5. 拆分时间块，找正文含技能名的块（爆发点），没有则用最后一块
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var blockLines = new List<(int Index, string Body)>();
        int? pending = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (TimeBlockRegex.IsMatch(trimmed))
            {
                if (pending.HasValue)
                    blockLines.Add((pending.Value, BuildBlockBody(lines, pending.Value, i)));
                pending = i;
            }
            else if (pending.HasValue
                     && (trimmed.StartsWith("灯光", StringComparison.Ordinal)
                         || trimmed.StartsWith("约束", StringComparison.Ordinal)))
            {
                blockLines.Add((pending.Value, BuildBlockBody(lines, pending.Value, i)));
                pending = null;
            }
        }
        if (pending.HasValue)
            blockLines.Add((pending.Value, BuildBlockBody(lines, pending.Value, lines.Count)));
        if (blockLines.Count == 0) return shotText;

        var matchedBlocks = blockLines
            .Where(b => b.Body.Contains(skillName, StringComparison.Ordinal))
            .ToList();
        var target = matchedBlocks.Count > 0
            ? matchedBlocks[matchedBlocks.Count - 1]
            : blockLines[blockLines.Count - 1];

        // 6. 在目标时间块正文末尾补一句技能呐喊
        var shout = caster + "怒吼\"" + skillName + "！\"";
        var targetLine = lines[target.Index].TrimEnd();
        if (!targetLine.EndsWith("。", StringComparison.Ordinal)
            && !targetLine.EndsWith("！", StringComparison.Ordinal)
            && !targetLine.EndsWith("；", StringComparison.Ordinal))
            targetLine += "。";
        targetLine += "；" + shout;
        lines[target.Index] = targetLine;

        var result = string.Join("\n", lines);

        // 7. 约束行：原"无台词，无口型表演，无对白"改为允许技能呐喊
        result = NoDialogueConstraintRegex.Replace(
            result,
            "台词仅一句技能呐喊「" + skillName + "」，铿锵有力、口型同步，禁止赶拍");

        return result;
    }

    private static string BuildBlockBody(List<string> lines, int start, int end)
    {
        return string.Join("", lines.Skip(start + 1).Take(Math.Max(0, end - start - 1)));
    }
}
