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
///
/// 加一种产出形态怎么做：
///   1. 产出就是一段文本（立项 / 剧本 / 清单 / 图册 / 质检这类）→ 数据里把 OutputTarget 填成
///      deliverable，落 DirectorSkillDeliverables。零代码，这是通用兜底：阶段是数据驱动、可以在
///      Skill 包页自己编排的，用户新加的阶段也得有地方落，通用表不能没有。
///   2. 产出要进业务表（分镜 / 资产 / 提示词这类）→ 建表 + 这里加一个 case + 数据里填个名字。
///
/// 拆表的依据换过一次，两条都留着，后面别再反复：
///   旧原则——按「结构差异」拆，不按模块数量。五个文本模块结构一样时拆五张表，
///           只会得到五份表定义和五套 CRUD，加个公共能力（导出、搜索、按版本回看）要改五处。
///   现原则——「模块独立优先」。立项已拆出 ProjectBriefs，剧本拆出 ScriptDrafts。
///           理由：模块要能自己加字段、删字段、加索引，改动不波及别人，
///           也能整个签出去独立维护（立项就是：它属于项目，不属于某一次运行）。
///   代价认了：公共能力以后用视图或基类收口，别在 N 处复制粘贴。
///   别往 PayloadJson 里塞结构化字段：塞进去能存，但查不动。
///
/// deliverable 这条通用线仍然保留：阶段编排是数据驱动的，用户新加的阶段也得有地方落，
/// 它是兜底，不是模块表——拆出去的模块（P0 立项、P1 剧本）照样各写各的表。
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
                               int projectId, int episodeId, string? inputText = null,
                               int runId = 0, int stepId = 0, string? stageKey = null)
    {
        // 交付物例外：它只是存档，不属于任何项目的业务数据，没绑项目也该留住
        if (projectId <= 0 && target != "deliverable")
            return new ImportResult(0, "本次运行没绑定项目，只留文本不入库");

        try
        {
            return target switch
            {
                "assets" => ImportAssets(outputJson, outputText, projectId),
                "asset_prompts" => ImportAssetPrompts(outputJson, outputText, projectId),
                "frames" => ImportFrames(outputJson, outputText, projectId, episodeId),
                "prompts" => ImportPrompts(outputText ?? "", projectId, inputText),
                "deliverable" => ImportDeliverable(runId, stepId, stageKey, outputJson, outputText, projectId, episodeId),
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

        /* 重跑 P2a 就是台账重出一版：旧的整类清掉再建，连同挂在资产上的出图结果一起作废。
           不清的话，模型这次把「外婆（照片/回忆态）」写成「外婆」，就又多出一个角色。 */
        _db.DeleteAllAssets(projectId);

        // 项目没挂在漫剧下（手工建的项目）就没有跨集复用一说，身份层跳过
        var dramaId = _db.GetDramaIdByProject(projectId);

        int n = 0, skipped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 同一批里的重名只建一次
        foreach (var it in items)
        {
            var table = DbService.AssetTableOf(it.Category);
            if (string.IsNullOrEmpty(table)) { skipped++; continue; }   // AUD 声音资产不出图也没有表
            if (!seen.Add(table + " " + it.Name)) { skipped++; continue; }
            /* 先认人：这一集抽到的「杨彦刚」跟上一集抽到的是同一个人（同一个 IdentityId），
               不是各起一份。认上了，出图才知道该照着哪一集那张定妆图出——
               不然 12 集下来就是 12 张不同的脸。 */
            var identityId = 0;
            if (dramaId > 0)
            {
                var cat = DbService.IdentityCategoryOf(it.Category);
                if (!string.IsNullOrEmpty(cat))
                    identityId = _db.GetOrCreateDramaIdentity(dramaId, cat, it.Name, it.Desc);
            }
            _db.AddAsset(projectId, table, it.Name, it.Desc, identityId);
            n++;
        }

        return new ImportResult(n, n == 0 ? "没识别到可入库的资产条目" : null);
    }

    // ========== 资产出图提示词（P2c 三批）==========

    private ImportResult ImportAssetPrompts(string? outputJson, string? outputText, int projectId)
    {
        var items = new List<(string Category, string Code, string Name, string? Image, string? Negative)>();

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
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(code)) continue;
            items.Add((category ?? "", code.Trim(), name.Trim(), Str(e, "imagePrompt"), Str(e, "negativePrompt")));
        }

        // md 兜底：按「## N. CODE-名字 · …」切块，块内取【中文正式提示词】与【负面约束】
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(outputText))
        {
            foreach (var block in SplitByHeading(outputText))
            {
                /* 标题行的排版每批都不一样，别锚死在开头：
                   「## SCN-01 · 老屋堂屋」「## 2. SCN-老屋堂屋」「## 01 · SCN-老屋堂屋（A·核心）｜功能图：…」
                   都见过。行里找第一个类别代码，它后面那段才是名字。 */
                var head = Regex.Replace(block.Split('\n')[0].Trim(), @"^#+\s*", "");
                var m = Regex.Match(head, @"(CHR|SCN|PRP|VFX|AUD)[-－—:：]\s*(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var name = MdName(m.Groups[2].Value);
                if (string.IsNullOrWhiteSpace(name)) continue;
                items.Add((m.Groups[1].Value.ToUpperInvariant(), m.Groups[0].Value.Trim(), name,
                           MdPrompt(block), MdNegative(block)));
            }
        }

        /* 重跑 P2c = 这批提示词整体重写：先把本批涉及的类别清一遍，
           不让上一版残留的词还挂在资产上跟新的混着。 */
        /* 只清「确实要重写」的那些类别。以前按条目类别清，结果是：名字一条都没对上、
           什么也写不进去，却先把整类提示词清空了——上一轮跑出来的好结果白丢。 */
        var touched = new HashSet<string>();
        foreach (var it in items)
        {
            if (it.Image == null && it.Negative == null) continue;
            var t = DbService.AssetTableOf(it.Category);
            if (!string.IsNullOrEmpty(t)) touched.Add(t);
        }
        foreach (var t in touched) _db.ClearAssetPrompts(projectId, t);

        int n = 0, missed = 0;
        var claimed = new HashSet<string>(StringComparer.Ordinal);   // 已经回填过的资产，后面的条目不再覆盖
        foreach (var it in items)
        {
            var table = DbService.AssetTableOf(it.Category);
            if (string.IsNullOrEmpty(table)) continue;
            if (it.Image == null && it.Negative == null) continue;

            /* 一个资产在产出里通常有好几条（场景母版 / 拼接图 / 功能图），
               名字还常被模型写成别名（「外婆老屋·堂屋」），只有 code 跟台账对得上。
               所以：候选键按可靠度排（原名 → 剥壳的名字 → code 里的名字），
               先命中先写入，同一资产的后续组件不再覆盖——母版一般排在前面，正是要留下的那条。
               一条都对不上就跳过，不再顺手建资产：造资产是 P2a 的活，P2c 只回填。 */
            var written = false;
            foreach (var key in KeysOf(it.Name, it.Code))
            {
                if (claimed.Contains(table + " " + key)) { written = true; break; }
                if (!_db.UpdateAssetPrompt(projectId, table, key, it.Image, it.Negative)) continue;
                claimed.Add(table + " " + key);
                n++; written = true; break;
            }
            /* 精确名全都对不上时再用「包含」救一次：提示词那批常把台账名写成简称
               （台账「刘如烟植物染工坊」，它写「染工坊」），一模一样比自然一条也不中，
               整批入库 0 —— 这一步明明跑完了，资产上却什么提示词都没有。
               只在同类里唯一命中时才写（见 FindAssetNameLike），命中多条就宁可跳过。 */
            if (!written)
            {
                foreach (var key in KeysOf(it.Name, it.Code))
                {
                    var hit = _db.FindAssetNameLike(projectId, table, key);
                    if (string.IsNullOrEmpty(hit)) continue;
                    if (claimed.Contains(table + " " + hit)) { written = true; break; }
                    if (!_db.UpdateAssetPrompt(projectId, table, hit, it.Image, it.Negative)) continue;
                    claimed.Add(table + " " + hit);
                    n++; written = true; break;
                }
            }
            if (!written) missed++;
        }

        if (n == 0) return new ImportResult(0, "没匹配到可回填的资产（提示词里的名字跟台账对不上）");
        return new ImportResult(n, missed > 0 ? missed + " 条名字对不上台账，已跳过" : null);
    }

    /// <summary>
    /// 取一块里的正向提示词。模型每批的排版都不一样：有的写「### 中文正式提示词」、
    /// 有的写「**中文正式提示词：**」、有的干脆把正文塞进 ``` 围栏里不给标签。
    /// 三级兜底：有标签取标签 → 没有就取第一个围栏 → 都没有才放弃。
    /// </summary>
    private static string? MdPrompt(string block)
    {
        var labeled = Section(block, "中文正式提示词", "中文提示词", "正式提示词");
        if (!string.IsNullOrWhiteSpace(labeled)) return labeled;

        var lines = block.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("```")) continue;
            var body = new List<string>();
            for (int j = i + 1; j < lines.Length && !lines[j].TrimStart().StartsWith("```"); j++)
                body.Add(lines[j]);
            var s = string.Join('\n', body).Trim();
            if (s.Length > 0) return s;
        }
        return Section(block, "场景母版");     // 连围栏都没有，至少把母版那节整个拿走
    }

    /// <summary>取负面约束：小节式、加粗标签式、以及「**负面约束**：内容」这种标签正文同一行的写法。</summary>
    private static string? MdNegative(string block)
    {
        var labeled = Section(block, "负面约束", "负面提示词", "禁止项");
        if (!string.IsNullOrWhiteSpace(labeled)) return labeled;

        foreach (var line in block.Split('\n'))
        {
            var m = Regex.Match(line.Trim(),
                @"^\*{0,2}\s*(?:负面约束|负面提示词|禁止项)\s*\*{0,2}\s*[:：]\s*(.+)$",
                RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>
    /// 从 md 标题的代码后缀里取资产名：「01 · 老屋堂屋」取「老屋堂屋」，
    /// 「赵日天 · 角色 4 View」取「赵日天」。只认第一段的话，编号型的
    /// 场景批、道具批会整批被叫成「01」「02」，台账上一个也对不上，入库 0 条。
    /// </summary>
    private static string MdName(string rest)
    {
        var parts = Regex.Split(rest ?? "", @"\s*[·|｜‖]\s*")
                         .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return "";
        var first = parts[0];
        var name = parts.Length > 1 && Regex.IsMatch(first, @"^[0-9A-Za-z]{1,4}$") ? parts[1] : first;
        var cut = name.IndexOfAny(new[] { '（', '(' });   // 「老屋堂屋（A·核心）」只要名字本身
        return cut > 0 ? name.Substring(0, cut).Trim() : name;
    }

    /// <summary>
    /// 回填时用来找资产的候选名字，按可靠度排序，谁先命中算谁。
    /// 模型爱给资产起别名（台账叫「老屋堂屋」，它写「外婆老屋·堂屋」），
    /// 但 code 一般老老实实照台账写（「SCN-老屋堂屋·母版」），所以两条路都要试。
    /// </summary>
    private static IEnumerable<string> KeysOf(string? name, string? code)
    {
        var cn = CodeName(code);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in new[] { name, BareName(name ?? ""), cn, BareName(cn) })
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var v = s!.Trim();
            if (v.Length > 0 && seen.Add(v)) yield return v;
        }
    }

    /// <summary>「SCN-老屋堂屋·母版」→「老屋堂屋」：剥掉类别前缀，再截掉组件后缀。</summary>
    private static string CodeName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var s = Regex.Replace(code!, @"^(CHR|SCN|PRP|VFX|AUD)\s*[-－—:：]\s*", "", RegexOptions.IgnoreCase);
        var cut = s.IndexOfAny(new[] { '·', '｜', '|', '（', '(' });
        return cut > 0 ? s.Substring(0, cut).Trim() : s.Trim();
    }

    /// <summary>
    /// 提示词里的名字常常裹着类别前缀和括注——「SCN-老屋屋顶（外婆家·夜）」，
    /// 而台账上就是「老屋屋顶」。剥掉这两层再比一次，能救回一大批本该匹配的条目。
    /// </summary>
    private static string BareName(string name)
    {
        var s = (name ?? "").Trim();
        s = Regex.Replace(s, @"^(CHR|SCN|PRP|VFX|AUD)\s*[-－—:：]\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[（(]\s*[^）)]*\s*[）)]", "").Trim();
        return s;
    }

    // ========== 分镜（P3）==========

    private ImportResult ImportFrames(string? outputJson, string? outputText, int projectId, int episodeId)
    {
        // 剧集本来是阶段 3 才产出的，但流水线能直接产出分镜——不能因为没分集就让这一步白跑。
        // 有明确选集就用选的那集；没有就看每个单元号自带的集号（「12.1」＝ 第 12 集第 1 单元）。
        var selectedEpNo = episodeId > 0 ? _db.GetEpisodeNumber(episodeId) : 0;
        var frames = ParseFrames(outputJson, outputText, selectedEpNo);
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

        // 按集落库。以前不管单元号写的是第几集，统统塞进第一集——
        // 分镜页按集筛选和 P4 按集投喂就全乱了套，12 集的分镜全挤在「第1集」里。
        var saved = 0;
        foreach (var g in frames.GroupBy(f => f.EpisodeNumber ?? 0))
        {
            var epId = g.Key > 0 ? _db.EnsureEpisodeNumber(projectId, g.Key)
                                 : (episodeId > 0 ? episodeId : _db.EnsureFirstEpisode(projectId));
            if (epId <= 0) continue;
            var epNo = g.Key > 0 ? g.Key : _db.GetEpisodeNumber(epId);
            var list = g.ToList();
            foreach (var f in list) f.EpisodeNumber = epNo;
            _db.SaveFramesIncremental(epId, projectId, list);
            saved += list.Count;
        }
        return new ImportResult(saved, null);
    }

    /// <summary>解析分镜产出：优先 JSON 数组，没有再按 md 表格兜底。</summary>
    private List<StoryboardFrame> ParseFrames(string? outputJson, string? outputText, int defaultEpNo)
    {
        var frames = new List<StoryboardFrame>();
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
                EpisodeNumber = EpisodeNoOf(unit, defaultEpNo),
                UnitNumber = unit,
                ShotNumber = shot.Trim(),
                ShotSize = Str(e, "shotSize"),
                Camera = Str(e, "camera"),
                Characters = Str(e, "characters"),
                Duration = Str(e, "duration"),
                Description = Str(e, "description"),
                Composition = Str(e, "composition"),
                Dialogue = Str(e, "dialogue"),
                // 场景：模型写过 scene / startScene / endScene 三种键名，都认。
                // 少了它，10 张场景母版图一张也进不了镜头参考图槽。
                Scene = Str(e, "scene"),
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

        if (frames.Count == 0 && !string.IsNullOrWhiteSpace(outputText))
            frames.AddRange(ParseFramesFromMarkdown(outputText, defaultEpNo));
        return frames;
    }

    /// <summary>
    /// md 表格兜底：按表头列名取列，不再按列的物理位置硬取。
    /// 以前固定把第 2/3/4 列当景别/机位/描述，可模型常在「机位」后插一列「站位四要素」——
    /// 于是描述存成了站位信息，镜长和台词直接丢光。
    /// 场景不在表格里，写在表格上方那行「**场景**：@SCN-老屋厨房」，得单独抓下来带上。
    /// </summary>
    private static List<StoryboardFrame> ParseFramesFromMarkdown(string outputText, int defaultEpNo)
    {
        var frames = new List<StoryboardFrame>();
        string[]? header = null;
        var unitScene = "";

        foreach (var line in outputText.Split('\n'))
        {
            var t = line.Trim();
            var mScene = Regex.Match(t, @"^\**\s*场景\s*\**\s*[：:]\s*(.+)$");
            if (mScene.Success) { unitScene = mScene.Groups[1].Value.Trim(); continue; }
            if (!t.StartsWith("|")) continue;

            var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2) continue;
            if (header == null)
            {
                if (cells.Any(c => c.Contains("镜号") || c.Contains("镜头"))) header = cells;
                continue;
            }
            if (cells.All(c => Regex.IsMatch(c, "^:?-{2,}:?$"))) continue;   // |---|---| 分隔行

            var shot = Col(header, cells, "镜号", "镜头") ?? "";
            if (!Regex.IsMatch(shot, @"^\d+(\.\d+)?-\d+$")) continue;
            var unit = Col(header, cells, "单元", "单元号");
            if (string.IsNullOrWhiteSpace(unit))
            {
                var dash = shot.LastIndexOf('-');
                unit = dash > 0 ? shot[..dash] : "";
            }
            frames.Add(new StoryboardFrame
            {
                EpisodeNumber = EpisodeNoOf(unit, defaultEpNo),
                UnitNumber = unit,
                ShotNumber = shot,
                ShotSize = Col(header, cells, "景别"),
                Camera = Col(header, cells, "机位", "运镜"),
                Composition = Col(header, cells, "站位", "站位四要素"),
                Duration = Col(header, cells, "镜长", "时长"),
                Description = Col(header, cells, "画面内容", "画面描述", "内容"),
                Dialogue = Col(header, cells, "台词", "对白"),
                Scene = unitScene.Length > 0 ? unitScene : null
            });
        }
        return frames;
    }

    /// <summary>按表头列名取该行的值；表头里没有这几个名字就返回 null。</summary>
    private static string? Col(string[] header, string[] cells, params string[] keys)
    {
        for (var i = 0; i < header.Length; i++)
        {
            if (!keys.Any(k => header[i].Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            if (i >= cells.Length) return null;
            var v = cells[i].Trim();
            return v is "-" or "—" or "–" ? null : (v.Length == 0 ? null : v);
        }
        return null;
    }

    /// <summary>单元号「12.1」→ 集号 12；取不到就用选中的集号，再没有返回 0（调用方退回第一集）。</summary>
    private static int EpisodeNoOf(string? unit, int defaultEpNo)
    {
        var s = (unit ?? "").Trim();
        var i = s.IndexOf('.');
        var head = i > 0 ? s[..i] : s;
        return int.TryParse(head, out var n) && n > 0 ? n : defaultEpNo;
    }

    // ========== 交付物：立项 / 剧本 / 资产清单 / 图册 / 质检（P0 P1 P2b P2d P5）==========

    private ImportResult ImportDeliverable(int runId, int stepId, string? stageKey, string? outputJson,
                                           string? outputText, int projectId, int episodeId)
    {
        if (string.IsNullOrWhiteSpace(outputText))
            return new ImportResult(0, "产出为空");

        /* 立项（P0）现在跑在漫剧上：那时一个项目都还没建，交付物表按项目归档，
           写一条 ProjectId=0 的行只会在库里留个谁也查不到的孤儿。
           产出原文照样存在步骤记录里——分集提纲正是从那儿解析出来的，不会丢。 */
        if (projectId <= 0)
            return new ImportResult(0, "立项运行挂在漫剧上，产出原文留在步骤记录");

        _db.InsertDeliverable(runId, stepId, stageKey ?? "", projectId, episodeId,
                              DeliverableTitle(stageKey), outputText, outputJson);

        // 剧本额外接一条线：Projects.ScriptContent 是老流水线阶段 3/4/6/7/8 的输入源。
        // 写进去等于把剧本喂给它们，否则 P1 跑完只是一篇没人读的文本。
        // 覆盖前把旧版另存一份——那是用户可能手动传过的剧本，不能静默冲掉。
        if (stageKey == "P1" && projectId > 0)
        {
            // 剧本落自己的表：重跑留新版本（Version 递增），旧版自然留在 ScriptDrafts 里能回看
            _db.InsertScriptDraft(projectId, episodeId, runId, stepId,
                                  DeliverableTitle(stageKey), outputText, outputJson);

            var old = _db.GetProjectScriptContent(projectId);
            if (!string.IsNullOrWhiteSpace(old) && old.Trim() != outputText.Trim())
                _db.InsertDeliverable(runId, stepId, "P1", projectId, episodeId, "覆盖前旧剧本", old, null);
            _db.SetProjectScriptContent(projectId, outputText);
        }

        return new ImportResult(1, null);
    }

    private static string DeliverableTitle(string? stageKey) => stageKey switch
    {
        "P0" => "立项锁定",
        "P1" => "剧本",
        "P2b" => "资产清单",
        "P2d" => "资产图册",
        "P5" => "质检报告",
        _ => stageKey ?? "产出"
    };

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

        // 有的模型开了 JSON 模式还是要裹一层 ```json 围栏，或者套个对象壳
        var s = Regex.Replace(json!, @"^\s*```(?:json)?\s*|\s*```\s*$", "", RegexOptions.IgnoreCase).Trim();
        var b = s.IndexOf('[');
        if (b > 0) s = s.Substring(b);             // 前面还有「好的，这是结果：」之类

        JsonDocument doc;
        try { doc = JsonDocument.Parse(s); }
        catch { yield break; }
        using (doc)
        {
            // 套了对象壳（{"scenes":[...]}）也认：取里面第一个数组
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var e in p.Value.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Object) yield return e;
                    break;
                }
                yield break;
            }
            if (root.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in root.EnumerateArray())
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
            if (!IsHeadLine(t, out var head)) continue;
            if (!titles.Any(x => head.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            var body = new List<string>();
            for (int j = i + 1; j < lines.Length; j++)
            {
                var n = lines[j].Trim();
                if (IsHeadLine(n, out _) || n.StartsWith("---") || n.StartsWith("***")) break;
                body.Add(lines[j]);
            }
            var s = string.Join('\n', body).Trim();
            if (s.Length > 0) return s;      // 空的小节继续往后找同名小节，别急着放弃
        }
        return null;
    }

    /// <summary>
    /// 小节标题两种写法都得认：「### 中文正式提示词」和「**中文正式提示词：**」。
    /// 只认 # 的话，模型用加粗写小节的那一批，提示词正文一条都取不到——
    /// 名字解析得再对也是白搭，因为取不到内容就整条跳过，入库 0。
    /// 加粗只认「整行就是一个标签」的那种，正文里的行内强调不会被误当成小节。
    /// </summary>
    private static bool IsHeadLine(string line, out string head)
    {
        head = "";
        if (line.Length == 0) return false;
        if (line.StartsWith("#")) { head = line.TrimStart('#').Trim(); return head.Length > 0; }
        var m = Regex.Match(line, @"^\*\*\s*(.+?)\s*\*\*\s*[:：]?\s*$");
        if (!m.Success) return false;
        head = m.Groups[1].Value.Trim().TrimEnd('：', ':');
        return head.Length > 0;
    }
}
