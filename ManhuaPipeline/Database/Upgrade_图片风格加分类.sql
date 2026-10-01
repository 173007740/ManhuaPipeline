/* 图片风格库：加分类
   ------------------------------------------------------------------
   现象：16 条风格摊平在一个下拉里，越加越长，挑的时候只能靠名字猜是哪一路画风。
        「图片风格预设 16 种」本身是分好类的（2D动画 / 3D动画 / 真人影视 / 漫画与插画），
        但库里没有放分类的地方，导入时这一层信息丢了。

   改法：ImageStyles 加 Category 列，把这套预设的四类回填进去。
        老风格（苔岬映画体 V1~V3）没有分类，留 NULL，页面上显示「未分类」。
        分类只是给挑风格时分组用，不参与出图提示词。
   ------------------------------------------------------------------ */

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
               WHERE TABLE_NAME = 'ImageStyles' AND COLUMN_NAME = 'Category')
BEGIN
    ALTER TABLE ImageStyles ADD Category NVARCHAR(50) NULL;
END

UPDATE ImageStyles SET Category = N'2D动画'
WHERE StyleName IN (N'清透电影动画', N'温暖手绘动画', N'国风赛璐璐',
                    N'热血少年动画', N'精致少女动画', N'暗黑幻想动画');

UPDATE ImageStyles SET Category = N'3D动画'
WHERE StyleName IN (N'华丽国漫3D', N'家庭卡通电影', N'写实CG电影');

UPDATE ImageStyles SET Category = N'真人影视'
WHERE StyleName IN (N'电影感', N'都市短剧影视', N'东方古装电影', N'写实科幻电影');

UPDATE ImageStyles SET Category = N'漫画与插画'
WHERE StyleName IN (N'韩式彩色网漫', N'黑白日漫', N'国风幻想厚涂');

SELECT StyleId, StyleName, ISNULL(Category, N'（未分类）') AS Category, LEN(StyleDesc) AS PosLen, LEN(StyleNegative) AS NegLen
FROM ImageStyles ORDER BY StyleId;
