SET NOCOUNT ON;
SELECT
  PromptId,
  UnitName,
  ShotLabel,
  CASE WHEN PromptTextH3 LIKE N'%玻吕茜亚%' OR PromptTextH3 LIKE N'%Polyxenia%' OR PromptTextH3 LIKE N'%Poluxia%' OR PromptTextH3 LIKE N'%Subject 1%' THEN N'波' ELSE N'' END AS has_P,
  CASE WHEN PromptTextH3 LIKE N'%遐蝶%' OR PromptTextH3 LIKE N'%Xiadie%' OR PromptTextH3 LIKE N'%Subject 2%' THEN N'遐' ELSE N'' END AS has_X,
  CASE WHEN PromptTextH3 LIKE N'%草帽%' OR PromptTextH3 LIKE N'%帽%' THEN N'帽' ELSE N'' END AS txt_hat,
  CASE WHEN PromptTextH3 LIKE N'%布包%' OR PromptTextH3 LIKE N'%包%' THEN N'包' ELSE N'' END AS txt_bag,
  CASE WHEN ISNULL(ReferenceImages,N'') LIKE N'%3a47870e%' THEN N'帽' ELSE N'' END AS ref_hat,
  CASE WHEN ISNULL(ReferenceImages,N'') LIKE N'%2e59632d%' THEN N'包' ELSE N'' END AS ref_bag
FROM SeedancePrompts
WHERE ProjectId = 43 AND PromptId BETWEEN 5406 AND 5458
ORDER BY PromptId;
