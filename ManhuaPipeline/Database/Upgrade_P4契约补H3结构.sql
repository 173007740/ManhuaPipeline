/* 上一版替换后，H3 描述后面还拖着一句旧的收尾：
   「；口型台词用 <d>台词</d> 包裹；单段 ≤10s…两种类型的切分标题完全一致…只是正文写法不同。」
   「只是正文写法不同」是错的——两种类型差的不只是措辞，是整个结构（七段式 vs 六节）。
   而且「单段 ≤10s」没说是谁的（那是 H3 的档，SD 是 15s / 30s）。这里一并说清楚。 */

UPDATE DirectorSkillStages
SET OutputContract = REPLACE(OutputContract,
    N'。；口型台词用 <d>台词</d> 包裹；单段 ≤10s，镜长由剧情倒推。两种类型的切分标题完全一致：【第X集】【单元X.Y】【镜头X.Y-N】，只是正文写法不同。',
    N'。口型台词用 <d>[Chinese] 台词原文</d> 包裹（H3 必写，SD 无此要求）；H3 单段 ≤10s，SD 按上面各自的时长上限，镜长一律由剧情倒推。两种类型的切分标题完全一致：【第X集】【单元X.Y】【镜头X.Y-N】——标题是系统切块入库的依据，两种类型都照写，差别只在标题之后的正文。')
WHERE PackId = 1 AND StageKey = N'P4'
  AND OutputContract LIKE N'%只是正文写法不同%';

SELECT StageKey, LEFT(OutputContract, 620) AS Head FROM DirectorSkillStages WHERE PackId = 1 AND StageKey = N'P4';
