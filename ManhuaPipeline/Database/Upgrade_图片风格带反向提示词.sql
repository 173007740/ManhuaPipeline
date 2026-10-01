/* 图片风格库：一条风格自带反向提示词
   ------------------------------------------------------------------
   现象：风格库里一条风格只有一段描述（StyleDesc），出图时把它拼进正向提示词。
        而「图片风格预设 16 种」里每一条都带两段：正向提示词 + 反向提示词。
        没有地方放反向，导入就只剩一半 —— 每种风格该避开什么（真人照片 / 蜡像皮肤 /
        塑料材质 / 错误肢体 / 水印…）全丢了，出图质量靠那几句通用负面词撑着。

   改法：ImageStyles 加一列 StyleNegative，跟 StyleDesc 并列，一条风格两段词。
        保留旧数据：老风格的 StyleNegative 为 NULL，出图时负面词照旧，行为不变。
   ------------------------------------------------------------------ */

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
               WHERE TABLE_NAME = 'ImageStyles' AND COLUMN_NAME = 'StyleNegative')
BEGIN
    ALTER TABLE ImageStyles ADD StyleNegative NVARCHAR(MAX) NULL;
END

SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'ImageStyles' ORDER BY ORDINAL_POSITION;
