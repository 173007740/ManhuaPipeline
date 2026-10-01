/* 漫剧级剧本素材：Dramas.ScriptContent

   为什么放在漫剧上：创建 / 编辑漫剧（dashboard）是立项的最早一步，那时一集都还没建，
   人手里那份剧本只能是整部的。Projects.ScriptContent 是「某一集的剧本」，必须在有了项目之后才有落点。

   这列的用途：某一集打开流水线立项那一格时，本集还没剧本就把这份整部素材预填进框里，
   人删到自己那一集的部分，再点「当底本交给模型」。
   不自动喂给模型 —— 12 集每一集都拿整部剧本去跑，等于每集都在写全集。

   每集的剧本仍存在 Projects.ScriptContent 上，两者不互相覆盖。 */

SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('Dramas','ScriptContent') IS NULL
    ALTER TABLE Dramas ADD ScriptContent NVARCHAR(MAX) NULL;
GO
