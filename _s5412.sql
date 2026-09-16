SET NOCOUNT ON;
SELECT ReferenceImages FROM SeedancePrompts WHERE PromptId = 5412;
SELECT N'=== BODY ===';
SELECT PromptTextH3 FROM SeedancePrompts WHERE PromptId = 5412;
SELECT N'=== neighbors 5410-5414 arrays ===';
SELECT PromptId, ReferenceImages FROM SeedancePrompts WHERE PromptId BETWEEN 5410 AND 5414 ORDER BY PromptId;
