/* 剧本来源：Projects.ScriptSource（'user' = 人自己写的定稿，'ai' = 模型跑出来的）

   为什么要有这一列：
   剧本正本在 Projects.ScriptContent 上，人在项目页「剧本」那一格粘自己的剧本就写进这一列。
   但 P1（剧本生成）一跑完，SkillOutputImporter 会拿模型产出把这一列整篇盖掉 ——
   人粘进去的剧本就这么没了，而且没有提示。下游（P2a 提取资产 / P3 分镜 / P4 提示词）
   取的又是 P1 步骤的产出，不是这一列，于是「我自己写剧本」这条路根本走不通。

   标了来源之后：
   - 人保存剧本（项目页那一格，或流水线页「用我自己的剧本」）→ ScriptSource='user'
   - P1 跑完要写回时先看来源：是 'user' 就不覆盖，模型那份只存成一张名为
     「模型写的剧本（你的定稿没动）」的交付物，人自己决定要不要换
   - 明确让模型重写（点「重跑 · 剧本生成」）时来源回到 'ai'

   没有这一列 / 该列为 NULL 时一律按「模型写的」处理，行为跟加列之前一致。
   不做历史回填：库里现存的剧本基本都是 P1 跑出来的（人粘的那份早就被盖掉了），
   回填成 user 会让它们从此再也盖不动，反而改坏现有流程。 */

SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('Projects','ScriptSource') IS NULL
    ALTER TABLE Projects ADD ScriptSource NVARCHAR(16) NULL;
GO
