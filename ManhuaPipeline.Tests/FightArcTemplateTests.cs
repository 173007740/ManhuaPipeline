using System.Text.Json;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Combat;
using ManhuaPipeline.Services.Director;
using Xunit;

namespace ManhuaPipeline.Tests;

public class FightArcTemplateTests
{
    private static StageUnit CombatUnit() => new()
    {
        EpisodeNumber = 1,
        UnitNumber = "1.4",
        Type = "打斗/动作",
        Duration = 11,
        RawText = "岳沉天与七宗宗主正面交手"
    };

    [Fact]
    public void BuildLibraryText_ContainsSixSeedArcs()
    {
        var arcs = new List<FightArcTemplate>
        {
            Template("full_duel", "完整对决"),
            Template("encounter", "遭遇战"),
            Template("assassination", "偷袭/速杀"),
            Template("siege_breakout", "围杀/突围"),
            Template("chase", "追逐战"),
            Template("drama_confrontation", "文戏对峙")
        };

        var text = FightArcCatalog.BuildLibraryText(arcs);

        Assert.Contains("完整对决", text);
        Assert.Contains("遭遇战", text);
        Assert.Contains("偷袭/速杀", text);
        Assert.Contains("围杀/突围", text);
        Assert.Contains("追逐战", text);
        Assert.Contains("文戏对峙", text);
    }

    [Fact]
    public void ParseSequence_NormalizesArcAndPhases()
    {
        var json = """
            {
              "arcType": "完整对决",
              "arcName": "完整对决",
              "totalDurationSeconds": 11,
              "winner": "岳沉天",
              "phases": [
                { "phaseNo": 1, "name": "对峙进场", "purpose": "立住对手", "durationPercent": 20, "minSeconds": 2, "maxSeconds": 3, "shotCount": 2, "shotStyle": "特写", "camera": "缓推", "vfxLevel": 10, "skills": [], "dialogue": "", "endState": "双方进入战斗距离", "nextCondition": "开打" },
                { "phaseNo": 2, "name": "攻防交锋", "purpose": "连续攻防", "durationPercent": 40, "minSeconds": 4, "maxSeconds": 6, "shotCount": 3, "shotStyle": "快切", "camera": "跟拍", "vfxLevel": 40, "skills": [], "dialogue": "", "endState": "一方露破绽", "nextCondition": "大招" },
                { "phaseNo": 3, "name": "胜负收束", "purpose": "定格收尾", "durationPercent": 40, "minSeconds": 4, "maxSeconds": 6, "shotCount": 2, "shotStyle": "特写", "camera": "定格", "vfxLevel": 80, "skills": [], "dialogue": "", "endState": "岳沉天取胜", "nextCondition": "切场" }
              ]
            }
            """;

        var sequence = FightArcCatalog.ParseSequence(json);

        Assert.NotNull(sequence);
        Assert.Equal("full_duel", sequence.ArcType);
        Assert.Equal(3, sequence.Phases.Count);
        Assert.Equal(100, sequence.Phases.Sum(p => p.DurationPercent));
    }

    [Fact]
    public void NormalizePercentSum_KeepsCloseLlmOutput()
    {
        var sequence = new FightSequencePlan
        {
            ArcType = "full_duel",
            Phases = new List<FightArcPhase>
            {
                new() { PhaseNo = 1, Name = "对峙", DurationPercent = 30 },
                new() { PhaseNo = 2, Name = "交锋", DurationPercent = 71 }
            }
        };

        var normalized = FightArcCatalog.NormalizePercentSum(sequence);

        Assert.NotNull(normalized);
        Assert.Equal(100, normalized.Phases.Sum(p => p.DurationPercent));
        Assert.Equal(70, normalized.Phases[^1].DurationPercent);
    }

    [Fact]
    public void DirectorValidator_MapsFightSequence()
    {
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "岳沉天",
              "secondarySubject": "七宗宗主",
              "conflictType": "1VN",
              "corePayoff": "破局",
              "emotionCurve": ["冷静", "爆发"],
              "rhythmStrategy": "慢到快",
              "actionStrategy": "先守后攻",
              "performanceStrategy": "不屈",
              "cameraStrategy": "环绕",
              "vfxStrategy": "后期峰值",
              "intensityLevel": 8,
              "combatGrammarIds": [],
              "actionPlan": null,
              "fightArcType": "siege_breakout",
              "fightSequence": {
                "arcType": "围杀/突围",
                "arcName": "围杀/突围",
                "totalDurationSeconds": 11,
                "winner": "岳沉天",
                "phases": [
                  { "phaseNo": 1, "name": "合围", "purpose": "建立压迫", "durationPercent": 15, "minSeconds": 2, "maxSeconds": 3, "shotCount": 2, "shotStyle": "大远景", "camera": "环绕", "vfxLevel": 15, "skills": [], "dialogue": "", "endState": "包围圈成型", "nextCondition": "轮攻" },
                  { "phaseNo": 2, "name": "轮攻", "purpose": "连续应对", "durationPercent": 35, "minSeconds": 4, "maxSeconds": 6, "shotCount": 4, "shotStyle": "快切", "camera": "手持", "vfxLevel": 40, "skills": [], "dialogue": "", "endState": "多人被击退", "nextCondition": "硬抗" },
                  { "phaseNo": 3, "name": "硬抗", "purpose": "撑住合击", "durationPercent": 15, "minSeconds": 2, "maxSeconds": 3, "shotCount": 2, "shotStyle": "特写", "camera": "缓推", "vfxLevel": 60, "skills": [], "dialogue": "", "endState": "找到破局时机", "nextCondition": "大招" },
                  { "phaseNo": 4, "name": "破局", "purpose": "撕开包围", "durationPercent": 15, "minSeconds": 2, "maxSeconds": 3, "shotCount": 2, "shotStyle": "正面全景", "camera": "慢动作", "vfxLevel": 100, "skills": [], "dialogue": "", "endState": "包围被撕开", "nextCondition": "反杀" },
                  { "phaseNo": 5, "name": "反杀", "purpose": "确认战果", "durationPercent": 20, "minSeconds": 2, "maxSeconds": 4, "shotCount": 2, "shotStyle": "远景", "camera": "跟随", "vfxLevel": 50, "skills": [], "dialogue": "", "endState": "突围成功", "nextCondition": "切场" }
                ]
              },
              "combatRoundCount": 3,
              "vfxPeakPhase": "Late"
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(30, CombatUnit(), raw);

        Assert.NotNull(plan);
        Assert.Equal("siege_breakout", plan.FightArcType);
        Assert.False(string.IsNullOrWhiteSpace(plan.FightSequenceJson));
        var sequence = FightArcCatalog.ParseSequence(plan.FightSequenceJson);
        Assert.NotNull(sequence);
        Assert.Equal(5, sequence.Phases.Count);
        Assert.Equal(100, sequence.Phases.Sum(p => p.DurationPercent));
    }

    [Fact]
    public void DirectorValidator_CombatWithoutFightSequence_AddsFallback()
    {
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "岳沉天",
              "conflictType": "1VN",
              "corePayoff": "hard hit",
              "emotionCurve": ["calm", "burst"],
              "rhythmStrategy": "slow to fast",
              "cameraStrategy": "push in",
              "vfxStrategy": "late peak"
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(30, CombatUnit(), raw);

        Assert.NotNull(plan);
        Assert.False(string.IsNullOrWhiteSpace(plan.FightArcType));
        var sequence = FightArcCatalog.ParseSequence(plan.FightSequenceJson);
        Assert.NotNull(sequence);
        Assert.True(sequence.Phases.Count >= 3);
        Assert.Equal(100, sequence.Phases.Sum(p => p.DurationPercent));
    }

    [Fact]
    public void DirectorValidator_NonCombat_ClearsFightSequence()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.2",
            Type = "文戏/情感",
            Duration = 5,
            RawText = "两人对视"
        };
        var raw = """
            {
              "dramaticPurpose": "reveal",
              "primarySubject": "岳沉天",
              "conflictType": "NonCombat",
              "corePayoff": "reveal",
              "emotionCurve": ["calm", "tension"],
              "rhythmStrategy": "slow",
              "cameraStrategy": "fixed",
              "vfxStrategy": "none",
              "fightArcType": "drama_confrontation",
              "fightSequence": {
                "arcType": "drama_confrontation",
                "arcName": "文戏对峙",
                "totalDurationSeconds": 5,
                "winner": "",
                "phases": []
              }
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(1, unit, raw);

        Assert.NotNull(plan);
        Assert.Equal("", plan.FightArcType);
        Assert.Equal("", plan.FightSequenceJson);
    }

    [Fact]
    public void BuildDecisionSection_ContainsFightSequence()
    {
        var plan = new DirectorPlan
        {
            DramaticPurpose = "payoff",
            PrimarySubject = "岳沉天",
            ConflictType = "1V1",
            CorePayoff = "hard hit",
            EmotionCurve = "冷静 → 爆发",
            RhythmStrategy = "慢到快",
            CameraStrategy = "环绕",
            VfxStrategy = "后期峰值",
            FightArcType = "full_duel",
            FightSequenceJson = JsonSerializer.Serialize(new FightSequencePlan
            {
                ArcType = "full_duel",
                ArcName = "完整对决",
                TotalDurationSeconds = 11,
                Phases = new List<FightArcPhase>
                {
                    new() { PhaseNo = 1, Name = "对峙进场", DurationPercent = 100, Purpose = "立住对手" }
                }
            })
        };

        var text = DirectorPromptBuilder.BuildDecisionSection(plan);

        Assert.Contains("战斗段落骨架", text);
        Assert.Contains("对峙进场", text);
    }

    private static FightArcTemplate Template(string arcTypeId, string name) => new()
    {
        ArcTypeId = arcTypeId,
        Name = name,
        Description = "测试骨架",
        Version = "1.0",
        DurationBudget = new FightDurationBudget { MinSeconds = 10, MaxSeconds = 30, DefaultSeconds = 20 },
        Phases = new List<FightArcPhase>
        {
            new() { PhaseNo = 1, Name = "对峙", DurationPercent = 50 },
            new() { PhaseNo = 2, Name = "收束", DurationPercent = 50 }
        },
        Status = "Active"
    };
    [Fact]
    public void ResolveCombatArcType_MovementUnit_ReturnsEmpty()
    {
        var unit = new StageUnit
        {
            UnitNumber = "1.10",
            Type = "打斗/动作",
            Duration = 5,
            CoreAction = "岳沉天发动踏天步一步踏出破庙，越过荒野落在枯井边",
            RawText = "单元1.10：踏天步赶路"
        };

        Assert.Equal("", FightArcCatalog.ResolveCombatArcType(unit, "full_duel"));
    }

    [Fact]
    public void ResolveCombatArcType_SiegeUnit_OverridesAssassination()
    {
        var unit = new StageUnit
        {
            UnitNumber = "1.1",
            Type = "高潮/对决",
            Duration = 11,
            CoreAction = "七大宗主围于阵心，七曜诛圣阵压制岳沉天",
            RawText = "单元1.1：围攻"
        };

        Assert.Equal("siege_breakout", FightArcCatalog.ResolveCombatArcType(unit, "assassination"));
    }

    [Fact]
    public void ResolveCombatArcType_FullDuelOnFiveSeconds_ReturnsEncounter()
    {
        var unit = new StageUnit
        {
            UnitNumber = "1.2",
            Type = "打斗/动作",
            Duration = 5,
            CoreAction = "岳沉天与敌人交手",
            RawText = "5秒短对决"
        };

        Assert.Equal("encounter", FightArcCatalog.ResolveCombatArcType(unit, "full_duel"));
    }

    [Fact]
    public void FitToDuration_ScalesPhaseSecondsToUnitDuration()
    {
        var sequence = new FightSequencePlan
        {
            ArcType = "encounter",
            TotalDurationSeconds = 11,
            Phases = new List<FightArcPhase>
            {
                new() { PhaseNo = 1, Name = "警觉", DurationPercent = 20, MinSeconds = 6, MaxSeconds = 9 },
                new() { PhaseNo = 2, Name = "快速交锋", DurationPercent = 80, MinSeconds = 18, MaxSeconds = 27 }
            }
        };

        var fitted = FightArcCatalog.FitToDuration(sequence, 5);

        Assert.NotNull(fitted);
        Assert.Equal(5, fitted.TotalDurationSeconds);
        Assert.All(fitted.Phases, p => Assert.True(p.MaxSeconds <= 5));
        Assert.All(fitted.Phases, p => Assert.True(p.MinSeconds >= 1));
    }
}
