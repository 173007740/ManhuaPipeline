using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

public static class DatabaseSchemaValidator
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredSchema =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Users"] = ["UserId", "ActiveLLMProvider", "ActiveVideoEngine"],
            ["Projects"] = ["ProjectId", "CoverImage", "StyleId", "Tags", "VideoRatio", "VideoWatermark", "VideoAudio", "VideoResolution", "TargetDurationText", "LibraryCategory"],
            ["VideoStyles"] = ["StyleId", "StyleName", "StylePrompt", "IsDefault"],
            ["LLMConfigs"] = ["ConfigId", "UserId", "Provider", "AutoEnhance", "UpdatedAt"],
            // SourceKey：自动出图的来源标记（Database\Upgrade_项目资产库类型.sql）
            ["ReferenceAssets"] = ["AssetId", "Tags", "FileSize", "SourceKey"],
            // ShotStatus：L5 逐镜状态机（Database\Upgrade_SeedancePrompts_ShotStatus.sql）
            ["SeedancePrompts"] = ["PromptId", "ShotType", "ShotStatus"],
            ["VideoGenerationTasks"] = ["Id", "Engine", "ApiStatus", "ResponseDuration", "EnhanceDuration", "EnhanceResolution"],
            ["TokenUsageConfig"] = ["Id", "QuotaTokens", "UpdatedAt"],
            ["SkillLibrary"] = ["SkillId", "UserId", "OwnerCharacter", "Tags"],
            ["FightTemplate"] = ["FightTemplateId", "UserId"],
            ["CameraAtom"] = ["AtomId", "UserId"],
            ["FightArcTemplates"] = ["FightArcTemplateId", "ArcTypeId", "Name", "Version", "PhasesJson", "DurationBudgetJson", "RulesJson", "Status"],
            ["EpisodeDirectorPlans"] = ["EpisodeDirectorPlanId", "ProjectId", "EpisodeNumber", "EpisodeGoal", "IntensityCurveJson", "PayoffScheduleJson", "ReservedVisualsJson", "RepetitionPolicyJson", "UnitEmotionCurveJson", "UnitTransitionsJson", "UnitEndStatesJson", "ClimaxBudgetJson"],
            ["DirectorPlans"] = ["DirectorPlanId", "ProjectId", "UnitNumber", "UnitType", "DramaticPurpose", "PrimarySubject", "ConflictType", "IntensityLevel", "CombatGrammarIds", "CombatRoundCount", "VfxPeakPhase", "ActionPlan", "FightArcType", "FightSequenceJson"],
            ["EpisodeDirectorStates"] = ["EpisodeDirectorStateId", "ProjectId", "EpisodeNumber", "CameraPatternCountsJson", "CombatPatternCountsJson", "VfxPatternCountsJson", "SlowMotionCount", "MajorExplosionCount", "CurrentPeakIntensity", "SmallClimaxCount", "MidClimaxCount", "LargeClimaxCount"],
            ["EpisodeUnitStateSnapshots"] = ["EpisodeUnitStateSnapshotId", "ProjectId", "EpisodeNumber", "UnitNumber", "StateJson", "Source", "CreatedAt", "UpdatedAt"],
            // L4 镜头状态机六字段（Database\Upgrade_StoryboardFrames_L4StateFields.sql）
            ["StoryboardFrames"] = ["FrameId", "EpisodeId", "ProjectId", "FrameNumber", "CombatBeatIndex", "CombatBeatIds", "Timeline", "StartState", "SingleAction", "EndState", "NextConnection", "ForbiddenChanges", "NewInformation"],
            ["Works"] = ["WorkId", "UserId"],
            // 结构化产物列（Database\Upgrade_StageData_StructuredJson.sql）：
            // L1 阶段 1/2/3 合并为一次调用后，故事基线 JSON 落在阶段 1 行上，阶段 2/3 复用渲染
            ["StageData"] = ["StageId", "ProjectId", "StageNumber", "StructuredJson"],
            ["StageProgressLogs"] = ["ProgressLogId", "ProjectId", "StageNumber", "LogText"],
            // 资产出图提示词：模版表 + 四类资产表的提示词字段
            // （Database\Upgrade_资产提示词模版.sql；ProjectId 来自 Upgrade_资产提示词模版_项目级.sql）
            ["AssetPromptTemplates"] = ["TemplateId", "UserId", "ProjectId", "Category", "StyleLock", "NegativePrompt", "RuleText", "Enabled", "UpdatedAt"],
            ["CharacterAssets"] = ["AssetId", "ImagePrompt", "NegativePrompt"],
            ["PropAssets"] = ["AssetId", "ImagePrompt", "NegativePrompt"],
            ["EnvironmentAssets"] = ["AssetId", "ImagePrompt", "NegativePrompt"],
            ["EffectAssets"] = ["AssetId", "ImagePrompt", "NegativePrompt"],
            // 资产出图任务队列（Database\Upgrade_资产出图任务队列.sql）
            // Source*/Garment* 来自 Database\Upgrade_角色卡派生.sql（带参考图的派生出图）
            ["AssetImageTasks"] = ["TaskId", "ProjectId", "UserId", "Category", "AssetId", "Status", "ImageUrl", "ErrorMessage", "CreatedAt", "StartedAt", "FinishedAt", "SourceProjectId", "SourceAssetId", "SourceImageUrl", "GarmentImageUrl", "SourceNote"],
            // 角色卡派生血缘（Database\Upgrade_角色卡派生.sql）
            ["CharacterCardDerivations"] = ["DerivationId", "UserId", "ProjectId", "AssetId", "SourceProjectId", "SourceAssetId", "SourceImageUrl", "GarmentImageUrl", "Prompt", "ResultImageUrl", "CreatedAt"],
            // L2 连续性层（Database\Upgrade_连续性表.sql）
            ["ProjectContinuityTables"] = ["ContinuityId", "ProjectId", "EpisodeNumber", "TableType", "ContentJson", "ContentText", "Source", "CreatedAt", "UpdatedAt"],
            // L3 关键帧层：每剧情节点一张关键帧（Database\Upgrade_关键帧层.sql）
            ["ProjectKeyframes"] = ["KeyframeId", "ProjectId", "EpisodeNumber", "SortOrder", "NodeLabel", "NodeReason", "ShotLabel", "UnitNumber", "Composition", "LockedCharacters", "LockedProps", "LockedSceneDirection", "ClueVisible", "NextConnection", "ImagePrompt", "Status", "Source", "CreatedAt", "UpdatedAt"]
        };

    public static void Validate(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("数据库连接字符串未配置。");

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT t.name AS TableName, c.name AS ColumnName
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.columns c ON c.object_id = t.object_id
WHERE s.name = N'dbo';", connection);

        var actual = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var tableName = reader.GetString(0);
                if (!actual.TryGetValue(tableName, out var columns))
                {
                    columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    actual[tableName] = columns;
                }

                if (!reader.IsDBNull(1))
                    columns.Add(reader.GetString(1));
            }
        }

        var missing = new List<string>();
        foreach (var (tableName, requiredColumns) in RequiredSchema)
        {
            if (!actual.TryGetValue(tableName, out var columns))
            {
                missing.Add("dbo." + tableName + "（整表缺失）");
                continue;
            }

            foreach (var columnName in requiredColumns)
            {
                if (!columns.Contains(columnName))
                    missing.Add("dbo." + tableName + "." + columnName);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "数据库结构不完整，应用不会在运行时自动建表或加字段。缺失项：" +
                string.Join("、", missing) +
                "。请先执行 docs_project/database_baseline 下的 Baseline.sql / Upgrade_Current.sql，或 ManhuaPipeline/Database 下对应功能的 Upgrade_*.sql。");
        }
    }
}
