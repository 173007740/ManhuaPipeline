SET NOCOUNT ON;
SELECT Id, PromptId, TaskId, Status, ApiStatus, RequestDuration, ResponseSeed, ErrorMessage FROM VideoGenerationTasks WHERE PromptId = 5412 ORDER BY Id;
