SET NOCOUNT ON;
SELECT FileName, LocalPath FROM ReferenceAssets
WHERE LocalPath LIKE N'%4c8371c5%' OR LocalPath LIKE N'%923f217b%' OR LocalPath LIKE N'%300cc334%'
   OR LocalPath LIKE N'%3a47870e%' OR LocalPath LIKE N'%2e59632d%';
SELECT N'=== context rows 5406..5414 ===';
SELECT PromptId, EpisodeNumber, UnitName, ShotNumber, ShotLabel, Status FROM SeedancePrompts WHERE PromptId BETWEEN 5406 AND 5414 ORDER BY PromptId;
