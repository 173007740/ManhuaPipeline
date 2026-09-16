using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// Stage 9 台词硬校验：生成结束后，把每个镜头引号内的台词替换成
/// Stage 5 分镜「对话/台词」原文，禁止 LLM 改写台词正文；
/// 同一句被 LLM 重复到多个时间块时只保留第一条。
/// </summary>
public static class PromptDialogueVerbatimGuard
{
    private static readonly Regex ShotHeaderRegex =
        new(@"(?:- \*\*)?镜头编号(?:\*\*)?\s*[:：]?\s*([\d.]+[a-zA-Z]?\-\d+)", RegexOptions.Compiled);

    private static readonly Regex DialogueLineRegex =
        new(@"(?:- \*\*)?(?:对话/台词|台词|对话)(?:\*\*)?\s*[:：]\s*(.*)$", RegexOptions.Compiled);

    private static readonly Regex QuoteRegex =
        new(@"[“""]([^“”""]*)[”""]", RegexOptions.Compiled);

    public static string Apply(string promptText, string? unitContext)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        var stage5 = ParseDialogueMap(unitContext);
        if (stage5.Count == 0) return promptText;

        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var shotRegex = new Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var shotMatch = Regex.Match(part, @"【镜头([\d.]+[a-zA-Z]?\-\d+)】");
            if (!shotMatch.Success)
            {
                builder.Append(part);
                continue;
            }
            var shot = shotMatch.Groups[1].Value;
            builder.Append(stage5.TryGetValue(shot, out var dialogue) && dialogue.Count > 0
                ? ReplaceQuotedText(part, dialogue)
                : part);
        }
        return builder.ToString();
    }

    private static Dictionary<string, List<string>> ParseDialogueMap(string? unitContext)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(unitContext)) return result;

        var currentShot = (string?)null;
        var currentLines = new List<string>();
        var dialogueOpen = false;
        foreach (var rawLine in unitContext.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var shotMatch = ShotHeaderRegex.Match(line);
            if (shotMatch.Success)
            {
                Flush(result, ref currentShot, ref currentLines);
                currentShot = shotMatch.Groups[1].Value;
                currentLines = new List<string>();
                dialogueOpen = false;
                continue;
            }

            if (currentShot == null) continue;

            // 「对话/台词」冒号后为空时，台词写在下一行缩进子项里，收集到下一个字段标题为止。
            if (dialogueOpen)
            {
                var continuation = StoryboardFrameFieldParser.TryGetDialogueContinuation(line);
                if (continuation == null)
                {
                    dialogueOpen = false;
                }
                else
                {
                    AppendDialogue(currentLines, continuation);
                    continue;
                }
            }

            var dialogueMatch = DialogueLineRegex.Match(line);
            if (!dialogueMatch.Success) continue;
            var value = dialogueMatch.Groups[1].Value.Trim();
            if (value.Length == 0)
            {
                dialogueOpen = true;   // 台词写在后续子项行
                continue;
            }
            if (value.Equals("无", StringComparison.OrdinalIgnoreCase)) continue;
            AppendDialogue(currentLines, value);
        }
        Flush(result, ref currentShot, ref currentLines);
        return result;
    }

    /// <summary>把「对话/台词」字段值切成台词正文（兼容内联引号、多说话人、无引号与子项列表四种写法）。</summary>
    private static void AppendDialogue(List<string> lines, string value)
    {
        foreach (var segment in StoryboardFrameFieldParser.ExtractDialogueSegments(value))
            lines.Add(segment.Text);
    }

    private static void Flush(
        Dictionary<string, List<string>> result,
        ref string? currentShot,
        ref List<string> currentLines)
    {
        if (currentShot != null && currentLines.Count > 0)
            result[currentShot] = currentLines;
        currentShot = null;
        currentLines = new List<string>();
    }

    private static string ReplaceQuotedText(string shotText, List<string> lines)
    {
        var index = 0;
        var replaced = QuoteRegex.Replace(shotText, m =>
        {
            if (index < lines.Count)
            {
                var replacement = lines[index];
                index++;
                return m.Value[0] + replacement + m.Value[^1];
            }
            index++;
            return m.Value;
        });
        if (QuoteRegex.Matches(replaced).Count > lines.Count)
            replaced = RemoveExtraQuoteClauses(replaced, lines.Count);
        return replaced;
    }

    /// <summary>同一句台词被 LLM 重复到多个时间块时，只保留第一条，其余连同所在小句删除。</summary>
    private static string RemoveExtraQuoteClauses(string shotText, int keepCount)
    {
        var matches = QuoteRegex.Matches(shotText).Cast<Match>().ToList();
        if (matches.Count <= keepCount) return shotText;
        foreach (var m in matches.Skip(keepCount).OrderByDescending(x => x.Index))
        {
            var start = FindClauseStart(shotText, m.Index);
            var end = FindClauseEnd(shotText, m.Index + m.Length);
            if (end > start) shotText = shotText.Remove(start, end - start);
        }
        return shotText;
    }

    private static int FindClauseStart(string text, int quoteIndex)
    {
        for (var i = quoteIndex - 1; i >= 0; i--)
        {
            if (text[i] is '，' or '。' or '；' or ';' or '、' or '\n' or '\r')
                return i + 1;
        }
        return 0;
    }

    private static int FindClauseEnd(string text, int quoteEnd)
    {
        for (var i = quoteEnd; i < text.Length; i++)
        {
            if (text[i] is '，' or '。' or '；' or ';' or '、' or '\n' or '\r')
                return i + 1;
        }
        return text.Length;
    }
}
