using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 从 .xlsx 文件解析提示词列表（纯 BCL 实现，不依赖第三方 Excel 库）。
/// 约定：第一行为表头（列名支持中英文别名，如 集数/单元/镜头/提示词/负面提示词/类型/时长）。
/// 仅支持 .xlsx；老版 .xls 请先另存为 .xlsx。
/// </summary>
public static class ExcelPromptParser
{
    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public sealed record ParseResult(List<SeedancePrompt> Prompts, List<string> Warnings);

    /// <summary>列索引。</summary>
    private enum Col
    {
        Episode = 0, Unit = 1, Shot = 2, Prompt = 3, Negative = 4, ShotType = 5, Duration = 6
    }

    // 表头别名 -> 列。表头会归一化（小写、去空白）后匹配。
    private static readonly Dictionary<string, Col> HeaderMap = new()
    {
        ["集数"] = Col.Episode, ["集"] = Col.Episode, ["第几集"] = Col.Episode,
        ["episode"] = Col.Episode, ["ep"] = Col.Episode, ["集号"] = Col.Episode,
        ["单元"] = Col.Unit, ["单元名"] = Col.Unit, ["单元号"] = Col.Unit, ["unit"] = Col.Unit,
        ["镜头"] = Col.Shot, ["镜号"] = Col.Shot, ["镜头号"] = Col.Shot, ["镜头编号"] = Col.Shot,
        ["shot"] = Col.Shot, ["镜次"] = Col.Shot,
        ["提示词"] = Col.Prompt, ["提示词内容"] = Col.Prompt, ["正向提示词"] = Col.Prompt,
        ["prompt"] = Col.Prompt, ["prompttext"] = Col.Prompt, ["正文"] = Col.Prompt, ["文案"] = Col.Prompt,
        ["负面"] = Col.Negative, ["负面提示词"] = Col.Negative, ["负向提示词"] = Col.Negative,
        ["negative"] = Col.Negative, ["negativeprompt"] = Col.Negative,
        ["类型"] = Col.ShotType, ["镜头类型"] = Col.ShotType, ["景别"] = Col.ShotType,
        ["shottype"] = Col.ShotType, ["分类"] = Col.ShotType,
        ["时长"] = Col.Duration, ["时长秒"] = Col.Duration, ["秒"] = Col.Duration, ["秒数"] = Col.Duration,
        ["duration"] = Col.Duration,
    };

    /// <summary>
    /// 解析 xlsx 字节流。返回解析出的提示词与警告信息。
    /// </summary>
    public static ParseResult Parse(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var warnings = new List<string>();
        var sharedStrings = LoadSharedStrings(zip);
        var sheetEntry = ResolveFirstSheet(zip);
        if (sheetEntry == null)
        {
            warnings.Add("未在 Excel 中找到工作表");
            return new ParseResult(new List<SeedancePrompt>(), warnings);
        }

        XDocument sheetDoc;
        using (var s = sheetEntry.Open())
        {
            sheetDoc = XDocument.Load(s);
        }

        XNamespace ns = SheetNs;
        var rows = sheetDoc.Root!.Elements(ns + "sheetData").Elements(ns + "row").ToList();
        if (rows.Count == 0)
        {
            warnings.Add("工作表为空");
            return new ParseResult(new List<SeedancePrompt>(), warnings);
        }

        // 第一行为表头，映射列。
        var header = ReadRowCells(rows[0], sharedStrings);
        var colMap = new Dictionary<Col, int>(); // 列 -> 单元格列索引
        for (var i = 0; i < header.Count; i++)
        {
            var key = NormalizeHeader(header[i]);
            if (key.Length == 0) continue;
            if (HeaderMap.TryGetValue(key, out var col) && !colMap.ContainsKey(col))
                colMap[col] = i;
        }
        if (!colMap.ContainsKey(Col.Prompt))
        {
            warnings.Add("未找到“提示词”列（第一行应为表头，需包含“提示词”列）");
            return new ParseResult(new List<SeedancePrompt>(), warnings);
        }

        var prompts = new List<SeedancePrompt>();
        for (var r = 1; r < rows.Count; r++)
        {
            var lineNo = ReadRowNumber(rows[r]);
            var cells = ReadRowCells(rows[r], sharedStrings);

            string Cell(Col c) => colMap.TryGetValue(c, out var idx) && idx < cells.Count ? cells[idx].Trim() : "";

            var promptText = Cell(Col.Prompt);
            var unit = Cell(Col.Unit);
            if (promptText.Length == 0 && unit.Length == 0 && Cell(Col.Shot).Length == 0)
                continue; // 空行

            if (promptText.Length == 0)
            {
                warnings.Add($"第 {lineNo} 行缺少提示词，已跳过");
                continue;
            }

            // 时长：非法时用默认 11 秒。
            var duration = 11;
            var durText = Cell(Col.Duration);
            if (durText.Length > 0 && !int.TryParse(durText.TrimEnd('秒'), out duration))
            {
                duration = 11;
                warnings.Add($"第 {lineNo} 行时长“{durText}”不是有效数字，已用默认 11 秒");
            }

            var episode = 0;
            var epText = Cell(Col.Episode);
            if (epText.Length > 0 && !int.TryParse(epText, out episode))
                warnings.Add($"第 {lineNo} 行集数“{epText}”不是有效数字，已用 0");

            prompts.Add(new SeedancePrompt
            {
                PromptText = promptText,
                NegativePrompt = Cell(Col.Negative),
                EpisodeNumber = episode,
                UnitName = unit,
                ShotLabel = Cell(Col.Shot),
                ShotType = Cell(Col.ShotType),
                Duration = duration,
                Status = "",
                BatchNumber = 1,
            });
        }

        return new ParseResult(prompts, warnings);
    }

    /// <summary>表头归一化：小写、去空白。</summary>
    private static string NormalizeHeader(string v)
    {
        var s = v.ToLowerInvariant();
        return s.Replace(" ", "").Replace("　", "").Trim();
    }

    /// <summary>读取共享字符串表。富文本 run 拼接。</summary>
    private static List<string> LoadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        XNamespace ns = SheetNs;
        foreach (var si in doc.Root!.Elements(ns + "si"))
        {
            // 富文本 <r><t>…</t></r> 拼接；普通文本直接 <t>。
            var text = si.Elements(ns + "t").Select(t => t.Value)
                .Concat(si.Elements(ns + "r").Elements(ns + "t").Select(t => t.Value));
            result.Add(string.Concat(text));
        }
        return result;
    }

    /// <summary>解析第一个工作表 entry。</summary>
    private static ZipArchiveEntry? ResolveFirstSheet(ZipArchive zip)
    {
        var wbEntry = zip.GetEntry("xl/workbook.xml");
        if (wbEntry == null) return null;

        XDocument wbDoc;
        using (var s = wbEntry.Open()) wbDoc = XDocument.Load(s);
        XNamespace ns = SheetNs;

        var firstSheet = wbDoc.Root!.Elements(ns + "sheets").Elements(ns + "sheet").FirstOrDefault();
        if (firstSheet == null) return null;
        // workbook.xml 中 r:id 属于 officeDocument 关系命名空间。
        var rid = firstSheet.Attribute("{" + OfficeRelNs + "}id")?.Value;
        if (string.IsNullOrEmpty(rid)) return null;

        // 通过 rels 找到 r:id -> Target（Relationship 元素属于 package 关系命名空间）。
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        string? target = null;
        if (relsEntry != null)
        {
            XDocument relsDoc;
            using (var s = relsEntry.Open()) relsDoc = XDocument.Load(s);
            XNamespace pkg = PackageRelNs;
            var rel = relsDoc.Root!.Elements(pkg + "Relationship").FirstOrDefault(e => e.Attribute("Id")?.Value == rid);
            target = rel?.Attribute("Target")?.Value;
        }
        if (string.IsNullOrEmpty(target)) return null;

        // 规范化 target -> 归档内路径（通常 worksheets/sheet1.xml，相对 xl/ 目录）。
        var t = target.TrimStart('/');
        while (t.StartsWith("../")) t = t.Substring(3);
        if (!t.StartsWith("xl/")) t = "xl/" + t;
        return zip.GetEntry(t);
    }

    /// <summary>读取一行所有单元格（按列顺序填满，空列补空串）。</summary>
    private static List<string> ReadRowCells(XElement row, List<string> sharedStrings)
    {
        XNamespace ns = SheetNs;
        var cells = row.Elements(ns + "c").ToList();
        var maxCol = cells.Count == 0 ? 0 : cells.Max(c => ColIndex(c.Attribute("r")?.Value ?? "A"));
        var values = new string[maxCol + 1];
        foreach (var c in cells)
        {
            var ref_ = c.Attribute("r")?.Value ?? "A";
            var idx = ColIndex(ref_);
            if (idx > maxCol) continue;
            var type = c.Attribute("t")?.Value ?? "n";
            var v = c.Element(ns + "v")?.Value;
            var value = type switch
            {
                "s" => int.TryParse(v, out var si) && si >= 0 && si < sharedStrings.Count ? sharedStrings[si] : "",
                "inlineStr" => string.Concat(c.Element(ns + "is")?.Elements(ns + "t").Select(t => t.Value) ?? Enumerable.Empty<string>()),
                "str" => v ?? "",
                "b" => v == "1" ? "TRUE" : "FALSE",
                _ => v ?? "",
            };
            values[idx] = value ?? "";
        }
        return values.ToList();
    }

    private static int ReadRowNumber(XElement row)
    {
        var r = row.Attribute("r")?.Value;
        return int.TryParse(r, out var n) ? n : 0;
    }

    /// <summary>单元格引用（如 "B2"）转 0 基列索引。</summary>
    private static int ColIndex(string ref_)
    {
        var letters = new string(ref_.TakeWhile(char.IsLetter).ToArray());
        var idx = 0;
        foreach (var ch in letters)
        {
            var up = char.ToUpperInvariant(ch);
            if (up is < 'A' or > 'Z') continue;
            idx = idx * 26 + (up - 'A' + 1);
        }
        return idx - 1;
    }
}
