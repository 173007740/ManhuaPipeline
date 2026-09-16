SET NOCOUNT ON;
DECLARE @p NVARCHAR(MAX);
SET @p = N'@@A1@@@@A2@@@@A3@@@@A4@@';
UPDATE SeedancePrompts SET PromptTextH3 = @p WHERE PromptId = 5432;
