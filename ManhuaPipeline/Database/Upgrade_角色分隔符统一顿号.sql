/* characters 字段里的多个角色，分隔符出现过三种写法：
   「@CHR-刘如烟、@CHR-杨彦刚」「@CHR-杨彦刚, @CHR-青鸟」。
   入库时不影响，但 P4 绑定资产图要按分隔符把角色拆开，
   混用会漏掉一半角色——那一镜的参考图就绑不全。
   定死一种：顿号。 */

-- 1) 契约：P3 产出、P4 引用，两边都写死顿号
UPDATE DirectorSkillStages
SET OutputContract = OutputContract + N'

【出场角色一律用顿号分隔】characters 字段写多个角色时，分隔只用「、」：
@CHR-杨彦刚、@CHR-刘如烟、@CHR-青鸟。
不许用逗号（中英文都不行）、斜杠、空格或「和 / 与 / 及」连接；单个角色就只写一个锚点。'
WHERE PackId = 1
  AND StageKey IN (N'P3', N'P4')
  AND OutputContract NOT LIKE N'%出场角色一律用顿号分隔%';

SELECT @@ROWCOUNT AS ContractRows;

-- 2) 存量数据：把已入库的逗号写法统一成顿号，免得 P4 现在就踩到
UPDATE StoryboardFrames
SET Characters = REPLACE(REPLACE(REPLACE(Characters, N', ', N'、'), N',', N'、'), N'，', N'、')
WHERE ProjectId = 57
  AND Characters IS NOT NULL
  AND (Characters LIKE N'%,%' OR Characters LIKE N'%，%');

SELECT @@ROWCOUNT AS FixedRows;
