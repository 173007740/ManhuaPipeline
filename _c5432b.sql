SET NOCOUNT ON;
SELECT PromptId, CAST(ReferenceImages AS NVARCHAR(MAX)) AS FullRef
FROM SeedancePrompts
WHERE PromptId BETWEEN 5430 AND 5434
ORDER BY PromptId;
SELECT '----BINDINGS----' AS x;
SELECT FrameId, Category, AssetId, Name, HasImage, SortOrder
FROM FrameAssetBindings
WHERE FrameId BETWEEN 5430 AND 5434
ORDER BY FrameId, SortOrder;
