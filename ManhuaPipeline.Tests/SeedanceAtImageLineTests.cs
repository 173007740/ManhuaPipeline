using ManhuaPipeline.Services;
using Xunit;
using System.Linq;

namespace ManhuaPipeline.Tests;

public class SeedanceAtImageLineTests
{
    [Fact]
    public void Parse_KeepsShortDescriptionAfterCategory()
    {
        var line = "@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [前世岳沉天战斗态]战斗态参考，保持气血与衣损状态一致；@图3 [七宗宗主]群像参考，保持整体造型一致；@图4 [七曜诛圣阵]特效参考，保持形态、色调、氛围一致；@图5 [葬天台]场景参考，保持空间布局、色调一致；@图6 [光影质感]光影质感参考，保持光线质感一致。";

        var segments = SeedanceAtImageLine.Parse(line);

        Assert.Equal(6, segments.Count);
        Assert.Equal("人物", segments[0].Category);
        Assert.Equal("形象参考，保持外貌、发型、服装、气质一致", segments[0].Description);
        Assert.Equal("战斗态", segments[1].Category);
        Assert.Equal("参考，保持气血与衣损状态一致", segments[1].Description);
        Assert.Equal("光影质感", segments[^1].Category);
    }

    [Fact]
    public void Rebuild_PreservesDescription()
    {
        var line = "@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [光影质感]光影质感参考，保持光线质感一致。";

        var rebuilt = SeedanceAtImageLine.Rebuild(SeedanceAtImageLine.Parse(line));

        Assert.Contains("@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致", rebuilt);
        Assert.Contains("@图2 [光影质感]光影质感参考，保持光线质感一致", rebuilt);
    }

    [Fact]
    public void CompactLine_StillParsesWithoutDescription()
    {
        var line = "@图1 [前世岳沉天]人物；@图2 [前世岳沉天战斗态]战斗态；@图3 [光影质感]光影质感。";

        var rebuilt = SeedanceAtImageLine.Rebuild(SeedanceAtImageLine.Parse(line));

        Assert.Equal("@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [前世岳沉天战斗态]战斗姿态参考，保持外貌、发型、服装、气质一致；@图3 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致", rebuilt);
    }

    [Fact]
    public void StyleCard_ForcesFixedAssetName()
    {
        var line = "@图1 [前世岳沉天]人物；@图2 [葬天台冷暗光影]光影质感参考，保持光线质感一致；";

        var segments = SeedanceAtImageLine.Parse(line);

        var style = segments.Single(s => s.Category == "光影质感");
        Assert.Equal("光影质感", style.Name);
        Assert.Equal("参考，保持光线质感一致", style.Description);

        var rebuilt = SeedanceAtImageLine.Rebuild(segments);
        Assert.Contains("@图2 [光影质感]光影质感参考，保持光线质感一致", rebuilt);
    }

    [Fact]
    public void Rebuild_NewFormat_MatchesUserTemplate()
    {
        var line = "@图1 前世岳沉天人物形象参考，保持外貌、发型、服装、气质一致；@图2 前世岳沉天战斗姿态参考，保持外貌、发型、服装、气质一致；@图3 七宗宗主群像参考，保持袍服各异、七色法光缠身、冷漠森严一致；@图4 七曜诛圣阵特效参考，保持形态、色调、阵纹氛围一致；@图5 赤金气血特效参考，保持赤金色调、能量形态、压迫氛围一致；@图6 葬天台场景参考，保持空间布局、黑石台面、阵柱位置、云海氛围一致；@图7 光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致。";

        var rebuilt = SeedanceAtImageLine.Rebuild(SeedanceAtImageLine.Parse(line));

        Assert.Equal("@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [前世岳沉天战斗态]战斗姿态参考，保持外貌、发型、服装、气质一致；@图3 [七宗宗主]群像参考，保持袍服各异、七色法光缠身、冷漠森严一致；@图4 [七曜诛圣阵]特效参考，保持形态、色调、阵纹氛围一致；@图5 [赤金气血]特效参考，保持赤金色调、能量形态、压迫氛围一致；@图6 [葬天台]场景参考，保持空间布局、黑石台面、阵柱位置、云海氛围一致；@图7 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致", rebuilt);
    }

    [Fact]
    public void Rebuild_FixesMissingRefWord()
    {
        var line = "@图1 [少年岳沉天]人物形象，保持外貌、发型、服装、气质一致；@图2 [七宗血契]道具参考，保持外观、材质、大小一致；@图3 [枯井遗府]场景参考，保持空间布局、色调一致；@图4 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致。";

        var rebuilt = SeedanceAtImageLine.Rebuild(SeedanceAtImageLine.Parse(line));

        Assert.Contains("@图1 [少年岳沉天]人物形象参考，保持外貌、发型、服装、气质一致", rebuilt);
    }

    [Fact]
    public void Parse_MultiLineAtImageLines_ParsesAll()
    {
        // 每行一个 @图N（Excel 导入常见写法），应全部解析。
        var line = "@图1 [黄泉]人物形象参考，保持银蓝长发、青银星轨眼瞳、黑灰青银战斗服、星河虚无太刀、星海流浪者气质一致；\n"
            + "@图2 [稻妻天守阁]场景参考，保持雨夜石阶、天守建筑、阴云雷光、积水倒影氛围一致；\n"
            + "@图3 [高燃战斗质感]光影参考，保持强明暗对比、电影级体积光、刀光特效、震屏冲击感一致；";

        var segments = SeedanceAtImageLine.Parse(line);

        Assert.Equal(3, segments.Count);
        Assert.Equal((1, "黄泉", "人物"), (segments[0].Index, segments[0].Name, segments[0].Category));
        Assert.Equal((2, "稻妻天守阁", "场景"), (segments[1].Index, segments[1].Name, segments[1].Category));
        Assert.Equal((3, "光影质感", "光影质感"), (segments[2].Index, segments[2].Name, segments[2].Category));
    }

    [Fact]
    public void Parse_GuangYingRef_RecognizedAsStyleCard()
    {
        // “光影参考”写法应等价于“光影质感参考”，资产名固定为“光影质感”。
        var line = "@图1 [黄泉]人物形象参考，保持银蓝长发气质一致；@图3 [高燃战斗质感]光影参考，保持强明暗对比、电影级体积光一致；";

        var segments = SeedanceAtImageLine.Parse(line);

        var style = segments.Single(s => s.Index == 3);
        Assert.Equal("光影质感", style.Name);
        Assert.Equal("光影质感", style.Category);
        Assert.Equal("参考，保持强明暗对比、电影级体积光一致", style.Description);
    }
}
