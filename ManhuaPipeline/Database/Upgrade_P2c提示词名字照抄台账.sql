/* P2c 三批：资产名字必须照抄台账
   ------------------------------------------------------------------
   现象：P2c2（场景提示词）跑完，产出 8300 字，ImportedCount = 0，
        ImportError = 「没匹配到可回填的资产（提示词里的名字跟台账对不上）」。
        于是环境资产表里的两条场景 ImagePrompt 一直是空的 —— 页面上看着就是
        「提示词没有生成」，人只能反复点重跑，重跑多少次结果都一样。

   根因：入库按「名字精确相等」把提示词回填到资产上（UpdateAssetPrompt 里
        WHERE ProjectId=@p AND Name=@n）。模型在 P2c 这批里给资产起了新名字：
        台账上叫「刘如烟植物染工坊」，它写「染工坊场景母版」/「染工坊」。
        名字对不上，一条也写不进去。

   改法两条腿：
   1. 契约层面（本文件）：要求 json 里的 name 照抄台账上的资产名称，
      不得改写、不得起别名、不得自行加「母版 / 场景母版 / 资产板」这类后缀。
   2. 兜底层面（代码 SkillOutputImporter + DbService.FindAssetNameLike）：
      精确名全都不中时，在同类资产里按「互相包含」找唯一命中的那一条再写一次。
      光有兜底不够 —— 它救得回简称，救不回整个名字换了的情况，所以契约也要立。

   注：拼接用 CHAR(13)+CHAR(10) 而不是字面换行，这样这份脚本用 -i 直接跑也不会
       因为文件编码把 N'…' 里的换行读坏了。
   ------------------------------------------------------------------ */

UPDATE DirectorSkillStages
SET OutputContract = OutputContract + CHAR(13)+CHAR(10)+N'【资产名称必须照抄台账】结构化输出里每条的 name 必须与上游 P2b 定稿清单里的资产名称完全一致：照抄台账上的那个名字，不得改写、不得起别名、不得自行加母版 / 场景母版 / 资产板这类后缀或组件名（组件写在 code 里，如 SCN-染工坊·母版）。
原因：入库是按名字把提示词回填到资产上的，名字跟台账差一个字就一条也写不进去（入库 0 条），这一步等于白跑 —— 产出看着很长，资产上却什么都没有。'
WHERE PackId = 1 AND StageKey IN (N'P2c1', N'P2c2', N'P2c3')
  AND OutputContract NOT LIKE N'%资产名称必须照抄台账%';

SELECT StageKey, Name, LEN(OutputContract) AS ContractLen
FROM DirectorSkillStages WHERE PackId = 1 AND StageKey IN (N'P2c1', N'P2c2', N'P2c3');
