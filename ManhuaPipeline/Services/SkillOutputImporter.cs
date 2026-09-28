using System.Text.Json;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 把 LLM 产出写进业务表。
///
/// 为什么这层单独存在：引擎（DirectorAgentService）刻意不懂业务，它只认 Stages.OutputTarget
/// 这个字符串。真正认识「分镜长什么样、资产台账长什么样」的是这里。
/// 于是「哪个阶段的产出往哪张表落」是数据，「怎么解析」是代码，两者分开：
/// 加一种产出形态 = 这里加一个 case + 数据里填个名字，引擎不动。
///
/// 解析策略：JSON 优先，md 兜底。
///   自己跑流水线时，引擎会在 prompt 里追加结构化要求（见 OutputSchemaFor），LLM 附 ```json 块，可靠；
///   拿外部 agent 跑完的文本回灌时没有 JSON，就得靠 md 兜底——这也是昨天那个导入器验证过的路子。
/// </summary>
public class SkillOutputImporter
{
    private readonly DbService _db;
    private readonly ILogger<SkillOutputImporter> _log;

    public SkillOutputImporter(DbService db, ILogger<SkillOutputImporter> log)
    {
        _db = db; _log = log;
    }

    public sealed record ImportResult(int Count, string? Error);

    /// <summary>
    /// 引擎会在 prompt 末尾追加这段，要求 LLM 除了正文外再附一个 ```json 块。
    /// 有了它解析就不必猜格式，这是「可靠」和「能跑」的分界线。
    /// </summary>
    public static string? OutputSchemaFor(string? target) => target switch
    {
        "assets" =>
            "产出末尾必须附一个 ```json 块，格式：\n[{\"code\":\"CHR-赵日天\",\"category\":\"CHR\",\"name\":\"赵日天\",\"description\":\"一句话资产描述\"}]\n" +
            "category 只能是 CHR/SCN/PRP/VFX/AUD 之一；code 前缀与 category 一致。",
        "asset_prompts" =>
            "产出末尾必须附一个 ```json 块，格式：\n[{\"code\":\"CHR-赵日天\",\"name\":\"赵日天\",\"imagePrompt\":\"中文正式提示词全文\",\"negativePrompt\":\"负面约束全文\"}]\n" +
            "imagePrompt 填中文正式提示词全文，不要把英文提示词填进来。",
        "frames" =>
            "产出末尾必须附一个 ```json 块，格式：\n[{\"unitNumber\":\"1.1\",\"shotNumber\":\"1.1-1\",\"shotSize\":\"中景\",\"camera\":\"机位\",\"characters\":\"出场角色\"," +
            "\"duration\":\"3s\",\"description\":\"画面内容\",\"startState\":\"起始状态\",\"singleAction\":\"本镜唯一动作\"," +
            "\"endState\":\"结束状态\",\"nextConnection\":\"为何进入下一镜\",\"forbiddenChanges\":\"不得变化的项\",\"newInformation\":\"信息增量\"}]\n" +
            "shotNumber 必须是「单元号-序号」，单元号形如 集.单元。",
        _ => null
    };

    public ImportResult Import(string target, string? outputJson, string? outputText,
                               int projectId, int episodeId, string? inputText = null)
    {
        if (projectId <= 0)
            return new ImportResult(0, "本次运行没绑定项目，只留文本不入库");

        try
        {
            return target switch
            {
                "assets" => ImportAssets(outputJson, outputText, projectId),
                "asset_prompts" => ImportAssetPrompts(outputJson, outputText, projectId),
                "frames" => ImportFrames(outputJson, outputText, projectId, episodeId),
                "prompts" => ImportPrompts(outputText ?? "", projectId, inputText),
                _ => new ImportResult(0, null)
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "产出入库失败 target={Target} project={ProjectId}", target, projectId);
            return new ImportResult(0, ex.Message);
        }
    }

    // ========== 资产台账（P2a）==========

    private ImportResult ImportAssets(string? outputJson, string? outputText, int projectId)
    {
        var items = new List<(string Category, string Name, string? Desc)>();

        foreach (var e in ReadJsonArray(outputJson))
        {
            var code = Str(e, "code") ?? "";
            var category = Str(e, "category");
            if (string.IsNullOrWhiteSpace(category))
            {
                var m = Regex.Match(code, @"^(CHR|SCN|PRP|VFX|AUD)-", RegexOptions.IgnoreCase);
                category = m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
            }
            var name = Str(e, "name") ?? Regex.Replace(code, @"^(CHR|SCN|PRP|VFX|AUD)-", "", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(name)) continue;
            items.Add((category ?? "", name.Trim(), Str(e, "description")));
        }

        // md 兜底：台账表格行，形如 | CHR-赵日天 | @CHR-赵日天 | 镜1、镜2 | 是 | 否 | 必须出图：… |
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(outputText))
        {
            foreach (var line in outputText.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("|")) continue;
                var cells = t.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
                if (cells.Length < 2) continue;
                var m = Regex.Match(cells[0], @"^(CHR|SCN|PRP|VFX|AUD)-(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                items.Add((m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Trim(),
                           cells.Length > 2 ? cells[^1] : null));
            }
        }

        int n = 0, skipped = 0;
        foreach (var it in items)
        {
            var table = DbService.AssetTableOf(it.Category);
            if (string.IsNullOrEmpty(table)) { skipped++; continue; }   // AUD 声音资产不出图也没有表
            if (_db.AssetNameExists(projectId, table, it.Name)) { skipped++; continue; }
            _db.AddAsset(projectId, table, it.Name, it.Desc);
            n++;
        }

        return new ImportResult(n, n == 0 && skipped > 0 ? "资产都已存在或未识别到条目" : null);
    }

    // ========== 资产出图提示词（P2c 三批）==========

    private ImportResult ImportAssetPrompts(string? outputJson, string? outputText, int projectId)
    {
        var items = new List<(string Category, string Name, string? Image, string? Negative)>();

        foreach (var e in ReadJsonArray(outputJson))
        {
            var code = Str(e, "code") ?? "";
            var category = Str(e, "category");
            if (string.IsNullOrWhiteSpace(category))
            {
                var m = Regex.Match(code, @"^(CHR|SCN|PRP|VFX|AUD)-", RegexOptions.IgnoreCase);
                category = m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
            }
            var name = Str(e, "name") ?? Regex.Replace(code, @"^(CHR|SCN|PRP|VFX|AUD)-", "", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(name)) continue;
            items.Add((category ?? "", name.Trim(), Str(e, "imagePrompt"), Str(e, "negativePrompt")));
        }

        // md 兜底：按「## N. CODE-名字 · …」切块，块内取【中文正式提示词】与【负面约束】
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(outputText))
        {
            foreach (var block in SplitByHeading(outputText))
            {
                var m = Regex.Match(block, @"^(CHR|SCN|PRP|VFX|AUD)-(.+?)[\s·|]", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                items.Add((m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Trim(),
                           Section(block, "中文正式提示词", "中文提示词"),
                           Section(block, "负面约束", "负面提示词", "禁止项")));
            }
        }

        int n = 0;
        foreach (var it in items)
        {
            var table = DbService.AssetTableOf(it.Category);
            if (string.IsNullOrEmpty(table)) continue;
            if (it.Image == null && it.Negative == null) continue;
            // 资产来自 P2a；万一这批跑在了 P2a 之前，先补建条目再回填，避免提示词无处可写
            if (!_db.AssetNameExists(projectId, table, it.Name))
                _db.AddAsset(projectId, table, it.Name, null);
            if (_db.UpdateAssetPrompt(projectId, table, it.Name, it.Image, it.Negative)) n++;
        }

        return new ImportResult(n, n == 0 ? "没匹配到可回填的资产提示词" : null);
    }

    // ========== 分镜（P3）==========

    private ImportResult ImportFrames(string? outputJson, string? outputText, int projectId, int episodeId)
    {
        if (episodeId <= 0)
            return new ImportResult(0, "本次运行没绑定剧集，分镜必须挂在具体某一集下，未入库");

        var frames = new List<StoryboardFrame>();
        var epNo = _db.GetEpisodeNumber(episodeId);

        foreach (var e in ReadJsonArray(outputJson))
        {
            var unit = Str(e, "unitNumber") ?? "";
            var shot = Str(e, "shotNumber") ?? "";
            if (string.IsNullOrWhiteSpace(shot)) continue;
            if (string.IsNullOrWhiteSpace(unit))
            {
                var i = shot.LastIndexOf('-');
                if (i > 0) unit = shot[..i];
            }
            frames.Add(new StoryboardFrame
            {
                EpisodeNumber = epNo,
                UnitNumber = unit,
                ShotNumber = shot.Trim(),
                ShotSize = Str(e, "shotSize"),
                Camera = Str(e, "camera"),
                Characters = Str(e, "characters"),
                Duration = Str(e, "duration"),
                Description = Str(e, "description"),
                Composition = Str(e, "composition"),
                Dialogue = Str(e, "dialogue"),
                StartScene = Str(e, "startScene"),
                EndScene = Str(e, "endScene"),
                // L4 镜头状态机六字段：分镜从「描述文本」变成「可校验的状态机」靠的就是这六个
                StartState = Str(e, "startState"),
                SingleAction = Str(e, "singleAction"),
                EndState = Str(e, "endState"),
                NextConnection = Str(e, "nextConnection"),
                ForbiddenChanges = Str(e, "forbiddenChanges"),
                NewInformation = Str(e, "newInformation")
            });
        }

        // md 兜底：只认首列是「单元号-序号」的表格行，认不出就宁可不写，避免落一库脏数据
        if (frames.Count == 0 && !string.IsNullOrWhiteSpace(outputText))
        {
            foreach (var line in outputText.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("|")) continue;
                var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length < 2) continue;
                if (!Regex.IsMatch(cells[0], @"^\d+(\.\d+)?-\d+$")) continue;
                var shot = cells[0];
                var dash = shot.LastIndexOf('-');
                frames.Add(new StoryboardFrame
                {
                    EpisodeNumber = epNo,
                    UnitNumber = shot[..dash],
                    ShotNumber = shot,
                    ShotSize = cells.Length > 1 ? cells[1] : null,
                    Camera = cells.Length > 2 ? cells[2] : null,
                    Description = cells.Length > 3 ? cells[3] : null
                });
            }
        }

        if (frames.Count == 0)
            return new ImportResult(0, "分镜没解析出镜头（产出里没找到 ```json 块或可识别的镜号表格），未入库");

        // 编号与排序：帧号全局递增，单元内序号单独排，保证「同单元替换」式增量写入能对上
        int frameNo = 0;
        var unitSeq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in frames)
        {
            f.FrameNumber = ++frameNo;
            f.SortOrder = frameNo;
            var key = f.UnitNumber ?? "";
            unitSeq[key] = unitSeq.TryGetValue(key, out var c) ? c + 1 : 1;
            f.UnitOrder = unitSeq[key];
        }

        _db.SaveFramesIncremental(episodeId, projectId, frames);
        return new ImportResult(frames.Count, null);
    }

    // ========== 投喂提示词（P4）==========

    private ImportResult ImportPrompts(string outputText, int projectId, string? inputText)
    {
        if (string.IsNullOrWhiteSpace(outputText)) return new ImportResult(0, "产出为空");

        // 复用 Stage9 的生产级解析器：它处理了【第X集】【单元X】【镜头X-Y】切分、时长推算，
        // 以及 @角色引用 / 场景锚定的名称规范化——这部分自己再写一遍只会更差。
        var names = _db.GetAssetNames(projectId);
        var prompts = SeedancePromptParser.Parse(outputText, projectId, names.Chars, names.Props, names.Envs, names.Effects);
        if (prompts.Count == 0)
            return new ImportResult(0, "没解析出镜头提示词（需要【第X集】【单元X.Y】【镜头X.Y-N】标题）");

        foreach (var p in prompts) p.Status = "completed";

        // 按镜头原地覆盖：保留 PromptId、参考图绑定与成片，只换内容列，重跑不会越积越多
        foreach (var g in prompts.GroupBy(p => (p.UnitName ?? "", p.ShotLabel ?? "")))
        {
            var rows = g.ToList();
            if (IsH3(inputText))
            {
                // H3 与 SD 共用一条记录、分列存放：正文搬到 PromptTextH3，SD 那列留着不动
                foreach (var p in rows) { p.PromptTextH3 = ToH3Refs(p.PromptText); p.PromptText = ""; }
                _db.UpsertH3PromptsForShot(projectId, g.Key.Item1, g.Key.Item2, rows);
            }
            else
            {
                _db.UpsertSeedancePromptsForShot(projectId, g.Key.Item1, g.Key.Item2, rows);
            }
        }

        return new ImportResult(prompts.Count, null);
    }

    /// <summary>
    /// 提示词类型写在这一步的输入里（P4 表单选的，或页面顶部选好带过来的），不靠猜产出文本。
    /// SD 与 H3 的差别是两套写法：@图N vs @图片N、有没有 model= 行 —— 猜产出不可靠。
    /// </summary>
    private static bool IsH3(string? inputText)
    {
        if (string.IsNullOrWhiteSpace(inputText)) return false;
        var m = Regex.Match(inputText, @"提示词类型\s*[:：]\s*(\S+)");
        return m.Success && m.Groups[1].Value.Contains("H3", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SD 写 @图N，H3 写 @图片N。解析器不关心这个，差别只在模型认哪种写法。</summary>
    private static string ToH3Refs(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : Regex.Replace(text, @"@图(\d+)", "@图片$1");

    // ========== 小工具 ==========

    private static IEnumerable<JsonElement> ReadJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e;
        }
    }

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    /// <summary>按「## 」标题切块，每块带上自己的标题行，供 md 兜底解析按资产归类。</summary>
    private static IEnumerable<string> SplitByHeading(string text)
    {
        var lines = text.Split('\n');
        var cur = new List<string>();
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("## "))
            {
                if (cur.Count > 0) yield return string.Join('\n', cur);
                cur = new List<string> { line };
            }
            else cur.Add(line);
        }
        if (cur.Count > 0) yield return string.Join('\n', cur);
    }

    /// <summary>取块内某个小节（如「### 中文正式提示词」）的正文，到下一个同级标题为止。</summary>
    private static string? Section(string block, params string[] titles)
    {
        var lines = block.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith("#")) continue;
            var head = t.TrimStart('#').Trim();
            if (!titles.Any(x => head.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            var body = new List<string>();
            for (int j = i + 1; j < lines.Length; j++)
            {
                var n = lines[j].Trim();
                if (n.StartsWith("#") || n.StartsWith("---") || n.StartsWith("##")) break;
                body.Add(lines[j]);
            }
            var s = string.Join('\n', body).Trim();
            return s.Length == 0 ? null : s;
        }
        return null;
    }
}
