using System.IO.Compression;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class ExcelPromptParserTests
{
    [Fact]
    public void Parse_StandardHeader_MapsAllColumns()
    {
        var xlsx = BuildXlsx(new[]
        {
            new[] { "集数", "单元", "镜头", "提示词", "负面提示词", "类型", "时长" },
            new[] { "1", "1.2", "S3", "龙形虚影盘旋于空，寒霜凝界", "镜头抖动", "全景", "5" },
            new[] { "2", "3.1", "", "剑光斩落，山崩地裂", "", "特写", "" },
            new[] { "", "", "", "无集数信息的一段", "负面", "中景", "12" },
        });

        var result = ExcelPromptParser.Parse(new MemoryStream(xlsx));

        Assert.Equal(3, result.Prompts.Count);
        Assert.Empty(result.Warnings);

        var p0 = result.Prompts[0];
        Assert.Equal("龙形虚影盘旋于空，寒霜凝界", p0.PromptText);
        Assert.Equal(1, p0.EpisodeNumber);
        Assert.Equal("1.2", p0.UnitName);
        Assert.Equal("S3", p0.ShotLabel);
        Assert.Equal("镜头抖动", p0.NegativePrompt);
        Assert.Equal("全景", p0.ShotType);
        Assert.Equal(5, p0.Duration);

        Assert.Equal(2, result.Prompts[1].EpisodeNumber);
        Assert.Equal(11, result.Prompts[1].Duration); // 缺时长用默认 11
        Assert.Equal(0, result.Prompts[2].EpisodeNumber); // 缺集数用 0
        Assert.Equal("负面", result.Prompts[2].NegativePrompt);
    }

    [Fact]
    public void Parse_SkipsEmptyRows_AndWarnsOnMissingPromptAndBadDuration()
    {
        var xlsx = BuildXlsx(new[]
        {
            new[] { "提示词", "单元", "时长" },
            new[] { "第一段", "", "8" },
            new[] { "", "", "" },
            new[] { "", "1.2", "5" },
            new[] { "第二段", "", "abc" },
        });

        var result = ExcelPromptParser.Parse(new MemoryStream(xlsx));

        Assert.Equal(2, result.Prompts.Count);
        Assert.Equal("第一段", result.Prompts[0].PromptText);
        Assert.Equal("第二段", result.Prompts[1].PromptText);
        Assert.Equal(11, result.Prompts[1].Duration); // abc -> 默认 11
        Assert.Contains(result.Warnings, w => w.Contains("缺少提示词"));
        Assert.Contains(result.Warnings, w => w.Contains("不是有效数字"));
    }

    [Fact]
    public void Parse_MissingPromptColumn_ReturnsEmptyWithWarning()
    {
        var xlsx = BuildXlsx(new[]
        {
            new[] { "集数", "单元" },
            new[] { "1", "1.2" },
        });

        var result = ExcelPromptParser.Parse(new MemoryStream(xlsx));

        Assert.Empty(result.Prompts);
        Assert.Contains(result.Warnings, w => w.Contains("提示词"));
    }

    [Fact]
    public void Parse_SharedStrings_ResolvesText()
    {
        var xlsx = BuildSharedStringsXlsx();
        var result = ExcelPromptParser.Parse(new MemoryStream(xlsx));

        var p = Assert.Single(result.Prompts);
        Assert.Equal("烈焰焚天", p.PromptText);
        Assert.Equal(9, p.Duration);
    }

    [Fact]
    public void Parse_HeaderAliases_ChineseAndEnglish()
    {
        var xlsx = BuildXlsx(new[]
        {
            new[] { "EP", "Unit", "Shot", "Prompt", "Negative", "ShotType", "Duration" },
            new[] { "3", "5.1", "S2", "奔雷一击", "模糊", "远景", "14" },
        });

        var result = ExcelPromptParser.Parse(new MemoryStream(xlsx));

        var p = Assert.Single(result.Prompts);
        Assert.Equal("奔雷一击", p.PromptText);
        Assert.Equal(3, p.EpisodeNumber);
        Assert.Equal("5.1", p.UnitName);
        Assert.Equal("S2", p.ShotLabel);
        Assert.Equal("模糊", p.NegativePrompt);
        Assert.Equal("远景", p.ShotType);
        Assert.Equal(14, p.Duration);
    }

    // ---- helpers ----

    private static byte[] BuildXlsx(string[][] rows)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            WriteEntry(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
                "Target=\"worksheets/sheet1.xml\"/></Relationships>");

            var sheetXml = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>";
            for (var r = 0; r < rows.Length; r++)
            {
                sheetXml += $"<row r=\"{r + 1}\">";
                for (var c = 0; c < rows[r].Length; c++)
                {
                    var addr = $"{(char)('A' + c)}{r + 1}";
                    var text = System.Security.SecurityElement.Escape(rows[r][c] ?? "");
                    sheetXml += $"<c r=\"{addr}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";
                }
                sheetXml += "</row>";
            }
            sheetXml += "</sheetData></worksheet>";
            WriteEntry(zip, "xl/worksheets/sheet1.xml", sheetXml);
        }
        return ms.ToArray();
    }

    private static byte[] BuildSharedStringsXlsx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            WriteEntry(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
                "Target=\"worksheets/sheet1.xml\"/></Relationships>");
            WriteEntry(zip, "xl/sharedStrings.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"4\" uniqueCount=\"4\">" +
                "<si><t>提示词</t></si><si><t>时长</t></si><si><t>烈焰焚天</t></si><si><t>9</t></si></sst>");
            WriteEntry(zip, "xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\" t=\"s\"><v>3</v></c></row>" +
                "</sheetData></worksheet>");
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
