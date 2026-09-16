﻿using System.Linq;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// Seedance 提示词解析（Stage 9 共享解析逻辑，供全量保存与逐单元增量入库复用）
/// </summary>
public static class SeedancePromptParser
{
    public static List<SeedancePrompt> Parse(string text, int projectId, List<string> charNames, List<string> propNames, List<string> envNames, List<string> effectNames)
    {
        var prompts = new List<SeedancePrompt>();
        if (string.IsNullOrWhiteSpace(text)) return prompts;
        text = NormalizeEpisodeMarkers(text);
        var pattern = @"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)";
        var blocks = Regex.Split(text, pattern)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToArray();
        var shotBlocks = new List<string>();
        foreach (var block in blocks)
        {
            var trimmed = block.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            // 主块内可能因 LLM 省略「第X集/单元X.Y」前缀而并入多个镜头，按【镜头】二次切分
            var shotMatches = Regex.Matches(trimmed, @"【镜头[\d.]+[a-zA-Z]?\-\d+】").Cast<Match>().ToList();
            if (shotMatches.Count <= 1)
            {
                shotBlocks.Add(trimmed);
                continue;
            }
            for (int i = 0; i < shotMatches.Count; i++)
            {
                var start = i == 0 ? 0 : shotMatches[i].Index;
                var end = i + 1 < shotMatches.Count ? shotMatches[i + 1].Index : trimmed.Length;
                var seg = trimmed.Substring(start, end - start).Trim();
                if (i > 0)
                {
                    var shotNo = Regex.Match(seg, @"【镜头([\d.]+[a-zA-Z]?\-\d+)】").Groups[1].Value;
                    var dashIdx = shotNo.LastIndexOf('-');
                    if (dashIdx > 0)
                    {
                        var unitNo = shotNo.Substring(0, dashIdx);
                        var epNo = unitNo.Split('.')[0];
                        seg = Regex.Replace(seg, @"^【镜头[\d.]+[a-zA-Z]?\-\d+】", "【第" + epNo + "集】【单元" + unitNo + "】【镜头" + shotNo + "】");
                    }
                }
                shotBlocks.Add(seg);
            }
        }
        foreach (var block in shotBlocks)
        {
            var trimmed = block.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Extract episode, unit, shot from the header
            var epMatch = Regex.Match(trimmed, @"【第(\d+)集】");
            var unitMatch = Regex.Match(trimmed, @"【单元([\d.]+[a-zA-Z]?)】");
            var shotMatch = Regex.Match(trimmed, @"【镜头([\d.]+[a-zA-Z]?\-\d+)】");

            // Extract shot type (场景类型)
            string? shotType = null;
            var typeMatch = Regex.Match(trimmed, @"类型[:：]\s*([^\r\n]+)");
            if (typeMatch.Success)
            {
                shotType = typeMatch.Groups[1].Value.Trim()
                    .TrimEnd('。', '.', '，', ',', '、', '；', ';', ' ', '\t');
                shotType = Regex.Match(shotType, @"^[^（(【]*").Value.Trim();
            }
            else
            {
                var unitTypeMatch = Regex.Match(trimmed, @"单元类型[:：]\s*([^\r\n]+)");
                if (unitTypeMatch.Success)
                {
                    shotType = unitTypeMatch.Groups[1].Value.Trim()
                        .TrimEnd('。', '.', '，', ',', '、', '；', ';', ' ', '\t');
                    shotType = Regex.Match(shotType, @"^[^（(【]*").Value.Trim();
                }
            }
            if (string.IsNullOrEmpty(shotType)) shotType = null;

            // Strip 类型/单元类型 line(s) so they don't enter the video prompt
            var cleanText = Regex.Replace(trimmed, @"^\s*类型[:：][^\r\n]*(\r?\n|$)", "", RegexOptions.Multiline);
            // 标题行只用于镜头切分与元数据提取，不进入最终视频提示词
            cleanText = Regex.Replace(cleanText, @"^\s*【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】\s*(\r?\n|$)", "", RegexOptions.Multiline);
            cleanText = Regex.Replace(cleanText, @"^\s*单元类型[:：][^\r\n]*(\r?\n|$)", "", RegexOptions.Multiline);
            cleanText = Regex.Replace(cleanText, @"类型[:：][^\r\n]*", "").Trim();

            // LLM 偶尔只回模板头（如“# Seedance 2.0 视频生成提示词”）或空块，不能当成真实镜头入库。
            if (!shotMatch.Success || string.IsNullOrWhiteSpace(cleanText)) continue;

            var negativeMatch = Regex.Match(trimmed, @"负向提示词\s*[:：]\s*([^\r\n]+)");
            var negativePrompt = negativeMatch.Success
                ? negativeMatch.Groups[1].Value.Trim().TrimEnd('。', '.', '；', ';', ' ', '\t')
                : null;

            prompts.Add(new SeedancePrompt
            {
                ProjectId = projectId,
                PromptText = NormalizePromptTags(cleanText, charNames, propNames, envNames, effectNames),
                NegativePrompt = negativePrompt,
                Status = "completed",
                BatchNumber = 1,
                EpisodeNumber = epMatch.Success ? int.Parse(epMatch.Groups[1].Value) : 0,
                UnitName = unitMatch.Success ? unitMatch.Groups[1].Value : null,
                ShotLabel = shotMatch.Success ? shotMatch.Groups[1].Value : null,
                ShotType = shotType,
                Duration = ParsePromptDuration(cleanText, shotType)
            });
        }
        return prompts;
    }


    // ========== 解析提示词实际时长（优先 LLM 输出，兜底 shotType 规则）==========
    private static int ParsePromptDuration(string promptText, string? shotType)
    {
        var fallback = (shotType is not null && (shotType.Contains("打斗") || shotType.Contains("对决"))) ? 15 : 11;
        if (string.IsNullOrWhiteSpace(promptText)) return fallback;
        var segDurs = Regex.Matches(promptText, @"时长[:：]\s*(\d+)\s*秒")
            .Cast<Match>()
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        if (segDurs.Count > 0)
        {
            var sum = segDurs.Sum();
            if (sum is 5 or 11 or 15) return sum;
        }
        var comboDur = Regex.Match(promptText, @"\[组合:[^\]]*-(\d+)\s*s\]");
        if (comboDur.Success && int.TryParse(comboDur.Groups[1].Value, out var cd) && (cd is 5 or 11 or 15))
            return cd;
        // 支持小数时间戳（如 [2-3.5s]、[3.5-5s]），避免因 \d+ 匹配不到小数导致时长被兜底成 15/11
        var blockEnds = Regex.Matches(promptText, @"\[([\d.]+)-([\d.]+)s\]")
            .Cast<Match>()
            .Select(m => decimal.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var end) ? end : 0m)
            .ToList();
        if (blockEnds.Count > 0)
        {
            var maxEnd = blockEnds.Max();
            if (maxEnd == 5m) return 5;
            if (maxEnd == 11m) return 11;
            if (maxEnd == 15m) return 15;
            // 非标准结束秒（LLM 违规写法）：就近取档，避免 5 秒镜头被兜底成 15/11
            if (maxEnd > 0)
            {
                var nearest = new[] { 5m, 11m, 15m }.OrderBy(x => Math.Abs(x - maxEnd)).First();
                return (int)nearest;
            }
        }
        return fallback;
    }

    // ========== 提示词标签规范化（角色引用 / 场景锚定）==========
    private static string NormalizePromptTags(string text, List<string> charNames, List<string> propNames, List<string> envNames, List<string> effectNames)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string comboScene = "";
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var comboMatch = Regex.Match(line, @"\[组合:([^\]]+)\]");
            if (comboMatch.Success && string.IsNullOrEmpty(comboScene))
                comboScene = comboMatch.Groups[1].Value.Split('-')[0].Trim();
            if (line.StartsWith("@角色引用") || line.StartsWith("@角色引用："))
            {
                var idx = line.IndexOfAny(new[] { ':', '：' });
                if (idx >= 0 && idx < line.Length - 1)
                {
                    var raw = line.Substring(idx + 1);
                    var tokens = Regex.Split(raw, @"[\[\]@、,，\s]+")
                        .Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    var mapped = new List<string>();
                    foreach (var t in tokens)
                    {
                        var t2 = t.Trim();
                        var name = MapName(t2, charNames);
                        if (name == t2 && t2.Length >= 3)
                        {
                            var split = SplitByNames(t2, charNames);
                            if (split.Count >= 2)
                            {
                                mapped.AddRange(split);
                                continue;
                            }
                        }
                        mapped.Add(name);
                    }
                    lines[i] = "@角色引用:" + string.Join("", mapped.Select(x => "[" + x + "]"));
                }
            }
            else if (line.StartsWith("@道具引用") || line.StartsWith("@道具引用："))
            {
                var idx = line.IndexOfAny(new[] { ':', '：' });
                if (idx >= 0 && idx < line.Length - 1)
                {
                    var raw = line.Substring(idx + 1);
                    var tokens = Regex.Split(raw, @"[\[\]@、,，\s]+")
                        .Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    if (tokens.Count > 0)
                    {
                        var mapped = tokens.Select(t => MapName(t.Trim(), propNames)).ToList();
                        lines[i] = "@道具引用:" + string.Join("", mapped.Select(x => "[" + x + "]"));
                    }
                }
            }
            else if (line.StartsWith("场景锚定引用") || line.StartsWith("场景锚定引用："))
            {
                var idx = line.IndexOfAny(new[] { ':', '：' });
                if (idx >= 0 && idx < line.Length - 1)
                    lines[i] = "场景锚定引用:" + MapAnchor(line.Substring(idx + 1).Trim(), comboScene, envNames);
            }
                        else if (line.StartsWith("参考图") || line.StartsWith("参考图："))
            {
                // 拆分同括号内多个同类型资产（如 为[破旧毛笔、黄符纸、残墨]道具参考 → 为[破旧毛笔][黄符纸][残墨]道具参考）
                lines[i] = Regex.Replace(line,
                    @"为(\[?)([^\]【】]*?)(\]?)(形象参考|道具参考|场景参考|特效参考)",
                    m =>
                    {
                        var items = Regex.Split(m.Groups[2].Value, @"[、,，\s]+")
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => "[" + s.Trim() + "]");
                        return "为" + string.Concat(items) + m.Groups[4].Value;
                    });
            }
        }
        return string.Join("\n", lines);
    }

        private static List<string> MainNames(List<string> names)
    {
        var result = new List<string>();
        foreach (var n in names)
        {
            var t = n.Trim();
            var idx = t.IndexOfAny(new[] { '（', '(' });
            if (idx > 0) t = t.Substring(0, idx).Trim();
            if (t.Length > 0 && !result.Contains(t)) result.Add(t);
        }
        return result;
    }

    private static string MapName(string name, List<string> names)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var mainNames = MainNames(names);
        var exact = mainNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // 名称是某角色卡括号内的身份称呼（如“丹青阁首席弟子”）时保持原样
        foreach (var n in names)
        {
            var t = n.Trim();
            var open = t.IndexOfAny(new[] { '（', '(' });
            var close = t.IndexOfAny(new[] { '）', ')' });
            if (open > 0 && close > open && t.Substring(open + 1, close - open - 1) == name)
                return name;
        }
        foreach (var t in mainNames)
        {
            if (t.Length < 2 || name.Length < 2) continue;
            var common = LongestCommonSubstring(name, t);
            if (common.Length >= 2 && common.Length * 10 >= Math.Min(name.Length, t.Length) * 6)
                return t;
        }
        return name;
    }

    private static string MapAnchor(string anchor, string comboScene, List<string> envNames)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return anchor;
        var exact = envNames.FirstOrDefault(n => string.Equals(n.Trim(), anchor, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Trim();
        if (!string.IsNullOrWhiteSpace(comboScene))
        {
            var byCombo = envNames.FirstOrDefault(n => string.Equals(n.Trim(), comboScene, StringComparison.OrdinalIgnoreCase));
            if (byCombo != null) return byCombo.Trim();
        }
        return anchor;
    }

    private static List<string> SplitByNames(string s, List<string> names)
    {
        var result = new List<string>();
        var sorted = MainNames(names).OrderByDescending(n => n.Length).ToList();
        int i = 0;
        while (i < s.Length)
        {
            var matched = sorted.FirstOrDefault(n => s.Substring(i).StartsWith(n));
            if (matched != null) { result.Add(matched); i += matched.Length; }
            else i++;
        }
        return result;
    }

    private static string LongestCommonSubstring(string a, string b)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        int best = 0, end = 0;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                if (a[i - 1] == b[j - 1])
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                    if (dp[i, j] > best) { best = dp[i, j]; end = i; }
                }
        return best > 0 ? a.Substring(end - best, best) : "";
    }



    // ========== Normalize Chinese numerals in episode markers ==========
    public static string NormalizeEpisodeMarkers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        text = System.Text.RegularExpressions.Regex.Replace(text, @"【第([一二三四五六七八九十百零]+)集】", match =>
        {
            var cn = match.Groups[1].Value;
            var num = ChineseToArabic(cn);
            return $"【第{num}集】";
        });
        return NormalizeUnitMarkers(text);
    }

    public static string NormalizeEpisodeNumbersOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        return System.Text.RegularExpressions.Regex.Replace(text, @"【第([一二三四五六七八九十百零]+)集】", match =>
        {
            var cn = match.Groups[1].Value;
            var num = ChineseToArabic(cn);
            return $"【第{num}集】";
        });
    }

    private static string NormalizeUnitMarkers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var episode = 0;
        var unitIndex = 0;
        return Regex.Replace(text, @"【第(\d+)集】|【单元([\d.]+)([-_]\d+)?[a-zA-Z]?】", match =>
        {
            if (match.Groups[1].Success)
            {
                episode = int.Parse(match.Groups[1].Value);
                unitIndex = 0;
                return match.Value;
            }
            var unitNumber = match.Groups[2].Value;
            if (episode <= 0)
            {
                var dot = unitNumber.IndexOf('.');
                int.TryParse(dot >= 0 ? unitNumber.Substring(0, dot) : unitNumber, out episode);
                unitIndex = 0;
            }
            if (match.Groups[3].Success)
            {
                // 模型把“集.场景-子块”写成 1.1-1、1.1-2 时，按出现顺序重编为 1.1、1.2…，避免解析器不认导致页面和分镜都拿不到内容
                if (episode <= 0)
                {
                    var dot = unitNumber.IndexOf('.');
                    int.TryParse(dot >= 0 ? unitNumber.Substring(0, dot) : unitNumber, out episode);
                    unitIndex = 0;
                }
                unitIndex++;
                return $"【单元{episode}.{unitIndex}】";
            }
            if (unitNumber.Contains('.'))
            {
                var dot = unitNumber.IndexOf('.');
                int.TryParse(unitNumber.Substring(dot + 1), out unitIndex);
                // 已是“集.单元”编号时直接保留，避免每个镜头标题重复的【第X集】重置计数导致全部变成 X.1
                return $"【单元{unitNumber}】";
            }
            if (episode <= 0)
            {
                int.TryParse(unitNumber, out episode);
                unitIndex = 0;
            }
            unitIndex++;
            return $"【单元{episode}.{unitIndex}】";
        });
    }

    private static int ChineseToArabic(string chinese)
    {
        if (string.IsNullOrEmpty(chinese)) return 0;
        var digitMap = new System.Collections.Generic.Dictionary<char, int>
        {
            {'零', 0}, {'一', 1}, {'二', 2}, {'三', 3}, {'四', 4},
            {'五', 5}, {'六', 6}, {'七', 7}, {'八', 8}, {'九', 9}
        };
        int total = 0, current = 0;
        foreach (var c in chinese)
        {
            if (c == '十')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
            }
            else if (c == '百')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
            }
            else if (digitMap.ContainsKey(c))
            {
                current = digitMap[c];
            }
        }
        total += current;
        return total > 0 ? total : 1;
    }
}
