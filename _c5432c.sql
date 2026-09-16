SET NOCOUNT ON;
SELECT '---- ReferenceAssets match ----' AS x;
SELECT AssetId, Category, FileName, LocalPath, tags, SubCategory
FROM ReferenceAssets
WHERE FileName LIKE '%4c8371c5%' OR FileName LIKE '%923f217b%' OR FileName LIKE '%2e59632d%'
   OR LocalPath LIKE '%4c8371c5%' OR LocalPath LIKE '%923f217b%' OR LocalPath LIKE '%2e59632d%';
SELECT '---- UnitAssetBindings sample ----' AS x;
SELECT TOP 30 BindingId, ProjectId, EpisodeNumber, UnitNumber, Category, AssetId, Name, HasImage, SortOrder
FROM UnitAssetBindings
ORDER BY BindingId DESC;
