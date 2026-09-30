/* P3 分镜改成两阶段：引擎先要结构清单，再按清单分批要镜头明细。
   模型得知道会有两次不同性质的请求，否则收到「只列清单」的指令时，
   很可能顺手把镜头细节也写满，第一段就撞输出上限——清单反而拿不到。 */

UPDATE DirectorSkillStages
SET OutputContract = OutputContract + N'

【本阶段分两步向你要产出】引擎会先请你列一份结构清单（单元 / 镜数 / 场景 / 台词），
再按这份清单分批向你要镜头明细。收到哪一步的指令就严格按那一步的要求输出：
让你列清单时只列清单，不要写镜头细节；让你写某一批时只写那几个单元，不要输出清单里的其他单元。'
WHERE PackId = 1
  AND StageKey = N'P3'
  AND OutputContract NOT LIKE N'%本阶段分两步向你要产出%';

SELECT @@ROWCOUNT AS Affected;
