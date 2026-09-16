SET NOCOUNT ON;
SELECT PromptId, UnitName, ShotLabel,
       SUBSTRING(ReferenceImages, 1, 260) AS RefImgs,
       CAST(PromptTextH3 AS NVARCHAR(MAX)) AS H3
FROM SeedancePrompts
WHERE PromptId BETWEEN 5430 AND 5434
ORDER BY PromptId;
