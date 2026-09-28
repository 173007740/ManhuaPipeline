#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把 agent skill 产出的 md 资产包导入 ManhuaPipeline 数据库。

用法：
    python ImportSkillBundle.py --dir "F:\\80 Agent Skill\\short-drama-director-v6.8" \
                                --title "破天一战" --out import.sql
    sqlcmd -S . -d ManhuaPipeline -U <user> -P <pwd> -C -f 65001 -i import.sql

设计要点：
1. 只解析、不直连数据库 —— 输出 SQL 文件，由 sqlcmd 执行，避免依赖 pyodbc。
2. 幂等：项目按 Title、资产按 ProjectId+Name、提示词按 ProjectId+UnitName+ShotNumber 判重，
   同一个包重复导入不会灌出重复行（重新生成提示词后要先删旧行再导，见 --replace）。
3. 资产按编码前缀路由到对应表：
   CHR -> CharacterAssets, SCN -> EnvironmentAssets, PRP -> PropAssets, VFX -> EffectAssets
4. 每个资产可能挂多个代码块（中文主提示词 / English production prompt / 激活状态附加页），
   第一个块进 ImagePrompt，其余块附在 Description 里，信息不丢。
"""

import argparse
import glob
import os
import re
import sys

# 编码前缀 -> 目标表
ASSET_TABLE = {
    'CHR': 'CharacterAssets',
    'SCN': 'EnvironmentAssets',
    'PRP': 'PropAssets',
    'VFX': 'EffectAssets',
}

FENCE_RE = re.compile(r'^```')
H3_RE = re.compile(r'^###\s+(.+)')
H4_RE = re.compile(r'^####\s+(.+)')
# 段1 · U01 双眼与裂天对峙（8秒）
SEG_RE = re.compile(r'^###\s+段(\d+)\s*[·•]\s*(U\d+)\s+(.+?)（(\d+)秒）')
REF_RE = re.compile(r'^【参考图】(.+)')
BAN_RE = re.compile(r'^【强制禁止项】(.+)')


def read_text(path):
    with open(path, 'r', encoding='utf-8') as f:
        return f.read()


def pick_file(directory, patterns, what):
    for pat in patterns:
        hits = sorted(glob.glob(os.path.join(directory, pat)))
        if hits:
            return hits[0]
    return None


def split_blocks(lines):
    """把 markdown 切成 (三级标题, 四级标题, 代码块正文) 序列，保留出现顺序。"""
    out = []
    h3 = h4 = None
    fence = None
    for ln in lines:
        if FENCE_RE.match(ln):
            if fence is None:
                fence = []
            else:
                out.append((h3, h4, '\n'.join(fence).strip()))
                fence = None
            continue
        if fence is not None:
            fence.append(ln)
            continue
        m = H4_RE.match(ln)
        if m:
            h4 = m.group(1).strip()
            continue
        m = H3_RE.match(ln)
        if m:
            h3 = m.group(1).strip()
            h4 = None
            continue
    return out


def parse_assets(md_text, wanted):
    """返回资产列表：dict(code, prefix, name, kind, prompt, extras)"""
    items = []
    for h3, h4, body in split_blocks(md_text.split('\n')):
        if not h3 or '·' not in h3:
            continue
        parts = [p.strip() for p in h3.split('·')]
        code = parts[0]
        m = re.match(r'^([A-Za-z]{3})', code)
        if not m:
            continue
        prefix = m.group(1).upper()
        if prefix not in ASSET_TABLE or prefix not in wanted:
            continue
        name = parts[1] if len(parts) > 1 else code
        kind = parts[2] if len(parts) > 2 else ''

        key = (code, name, kind)
        if not items or items[-1]['key'] != key:
            items.append({'key': key, 'code': code, 'prefix': prefix, 'name': name,
                          'kind': kind, 'prompt': body, 'extras': []})
        else:
            # 同一资产下的后续块（英文版 / 附加页）
            label = h4 or '附加提示词'
            items[-1]['extras'].append((label, body))
    return items


def parse_video(md_text):
    """返回视频投喂提示词列表：dict(seg, unit, title, duration, refs, prompt, ban)"""
    lines = md_text.split('\n')
    segs = []
    cur = None
    fence = None
    for ln in lines:
        if FENCE_RE.match(ln):
            if fence is None:
                fence = []
            else:
                if cur is not None:
                    cur['prompt'] = '\n'.join(fence).strip()
                fence = None
            continue
        if fence is not None:
            fence.append(ln)
            continue
        m = SEG_RE.match(ln)
        if m:
            cur = {'seg': int(m.group(1)), 'unit': m.group(2), 'title': m.group(3).strip(),
                   'duration': int(m.group(4)), 'refs': '', 'prompt': '', 'ban': ''}
            segs.append(cur)
            continue
        if cur is None:
            continue
        m = REF_RE.match(ln)
        if m:
            cur['refs'] = m.group(1).strip().rstrip('。 ')
            continue
        m = BAN_RE.match(ln)
        if m:
            cur['ban'] = m.group(1).strip().rstrip('。 ')
            continue

    # 投喂提示词里【强制禁止项】写在代码块内部，再从正文里兜一次
    for s in segs:
        if s['ban']:
            continue
        for ln in s['prompt'].split('\n'):
            m = BAN_RE.match(ln)
            if m:
                s['ban'] = m.group(1).strip().rstrip('。 ')
                break
    return segs


def esc(s):
    return (s or '').replace("'", "''")


def nv(s):
    return "N'" + esc(s) + "'"


def build_sql(title, desc, assets, segs, replace):
    L = []
    L.append('-- 由 Scripts/ImportSkillBundle.py 自动生成，请勿手工编辑')
    L.append('SET NOCOUNT ON;')
    L.append('BEGIN TRAN;')
    L.append('')
    # Projects.DramaId 有外键指向 Dramas 且不允许为空，所以先保证「剧」存在
    L.append('DECLARE @did INT;')
    L.append('SELECT @did = DramaId FROM Dramas WHERE Title = %s;' % nv(title))
    L.append('IF @did IS NULL')
    L.append('BEGIN')
    L.append('    INSERT INTO Dramas(UserId, Title, Description, CreatedAt, UpdatedAt)')
    L.append('    VALUES(1, %s, %s, SYSDATETIME(), SYSDATETIME());' % (nv(title), nv(desc)))
    L.append('    SET @did = SCOPE_IDENTITY();')
    L.append("    PRINT 'drama created: ' + CAST(@did AS varchar);")
    L.append('END')
    L.append('')
    L.append('DECLARE @pid INT;')
    L.append('SELECT @pid = ProjectId FROM Projects WHERE Title = %s;' % nv(title))
    L.append('IF @pid IS NULL')
    L.append('BEGIN')
    L.append('    INSERT INTO Projects(UserId, DramaId, Title, Description, ScriptContent, CurrentStage, Status,')
    L.append('                         CreatedAt, UpdatedAt, EpisodeCount, CurrentBatch,')
    L.append('                         VideoRatio, VideoResolution, VideoMegapixels, ProjectType,')
    L.append('                         LibraryCategory, TargetDurationText)')
    L.append('    VALUES(1, @did, %s, %s, NULL, 9, %s, SYSDATETIME(), SYSDATETIME(), 1, 1,' % (nv(title), nv(desc), nv('draft')))
    L.append("            N'16:9', N'1080p', 1.0, N'drama', N'玄幻', N'1分30秒');")
    L.append('    SET @pid = SCOPE_IDENTITY();')
    L.append("    PRINT 'project created: ' + CAST(@pid AS varchar);")
    L.append('END')
    L.append('ELSE')
    L.append("    PRINT 'project exists: ' + CAST(@pid AS varchar);")
    L.append('')

    if replace:
        L.append('-- --replace：先清掉本次要写入的旧行，便于重新导入新的提示词')
        if assets:
            names = ','.join(nv('%s（%s）' % (a['name'], a['kind']) if a['kind'] else a['name']) for a in assets)
            for tbl in sorted({ASSET_TABLE[a['prefix']] for a in assets}):
                L.append('DELETE FROM %s WHERE ProjectId = @pid AND Name IN (%s);' % (tbl, names))
        if segs:
            units = ','.join(nv(s['unit']) for s in segs)
            L.append('DELETE FROM SeedancePrompts WHERE ProjectId = @pid AND UnitName IN (%s);' % units)
        L.append('')

    for a in assets:
        tbl = ASSET_TABLE[a['prefix']]
        full_name = '%s（%s）' % (a['name'], a['kind']) if a['kind'] else a['name']
        desc_parts = ['%s · %s' % (a['code'], a['kind'] or '资产')]
        for label, body in a['extras']:
            desc_parts.append('\n\n[%s]\n%s' % (label, body))
        L.append('IF NOT EXISTS (SELECT 1 FROM %s WHERE ProjectId = @pid AND Name = %s)' % (tbl, nv(full_name)))
        L.append('BEGIN')
        L.append('    INSERT INTO %s(ProjectId, Name, Description, ImagePrompt, CreatedAt)' % tbl)
        L.append('    VALUES(@pid, %s, %s, %s, SYSDATETIME());' % (nv(full_name), nv('\n'.join(desc_parts)), nv(a['prompt'])))
        L.append('END')
        L.append('')

    for s in segs:
        label = '段%d · %s %s' % (s['seg'], s['unit'], s['title'])
        L.append('IF NOT EXISTS (SELECT 1 FROM SeedancePrompts WHERE ProjectId = @pid AND UnitName = %s AND ShotNumber = %d)' % (nv(s['unit']), s['seg']))
        L.append('BEGIN')
        L.append('    INSERT INTO SeedancePrompts(ProjectId, PromptText, NegativePrompt, Status, CreatedAt,')
        L.append('                                BatchNumber, EpisodeNumber, UnitName, ShotNumber, ShotLabel,')
        L.append('                                ReferenceImages, Duration)')
        L.append('    VALUES(@pid, %s, %s, %s, SYSDATETIME(), 1, 1, %s, %d, %s, %s, %d);'
                 % (nv(s['prompt']), nv(s['ban']), nv('pending'), nv(s['unit']), s['seg'], nv(label), nv(s['refs']), s['duration']))
        L.append('END')
        L.append('')

    L.append('COMMIT TRAN;')
    L.append("PRINT 'import done.';")
    return '\n'.join(L)


def main():
    ap = argparse.ArgumentParser(description='把 skill 产出的 md 资产包导入 ManhuaPipeline')
    ap.add_argument('--dir', required=True, help='skill 输出目录（含 md 与 png）')
    ap.add_argument('--title', default=None, help='项目名，默认取目录里的剧名')
    ap.add_argument('--out', default=None, help='输出 SQL 文件路径；不填则打印到标准输出')
    ap.add_argument('--only', default='CHR,SCN,PRP,VFX,VIDEO',
                    help='只导入哪些类：CHR/SCN/PRP/VFX/VIDEO，逗号分隔')
    ap.add_argument('--replace', action='store_true',
                    help='先删除本次涉及的旧行再插入（提示词重做后用它）')
    args = ap.parse_args()

    wanted = {w.strip().upper() for w in args.only.split(',') if w.strip()}

    asset_md = pick_file(args.dir, ['*数字资产包*.md', '*资产图册*.md'], '资产')
    video_md = pick_file(args.dir, ['*投喂提示词*.md', '*Seedance*.md'], '视频') if 'VIDEO' in wanted else None

    if not asset_md and not video_md:
        sys.exit('目录下没找到资产 md 或投喂提示词 md：%s' % args.dir)

    title = args.title
    if not title:
        m = re.search(r'^#\s*《(.+?)》', read_text(asset_md or video_md), re.M)
        title = m.group(1) if m else os.path.basename(args.dir.rstrip('\\/'))

    assets = parse_assets(read_text(asset_md), wanted) if asset_md else []
    segs = parse_video(read_text(video_md)) if video_md else []

    if not assets and not segs:
        sys.exit('没解析出任何资产或提示词，检查 md 结构（需要 ### 编码 · 名称 与 ```text 块）')

    sys.stderr.write('解析结果：项目=%s，资产 %d 条，视频段 %d 条\n' % (title, len(assets), len(segs)))
    for a in assets:
        sys.stderr.write('  [%s] %s %s（%s） prompt=%d字 附加块=%d\n'
                         % (a['prefix'], a['code'], a['name'], a['kind'], len(a['prompt']), len(a['extras'])))
    for s in segs:
        sys.stderr.write('  [VIDEO] %s %s %ds prompt=%d字\n'
                         % (s['unit'], s['title'], s['duration'], len(s['prompt'])))

    sql = build_sql(title, '由 %s 导入' % os.path.basename(args.dir.rstrip('\\/')), assets, segs, args.replace)
    if args.out:
        with open(args.out, 'w', encoding='utf-8') as f:
            f.write(sql)
        sys.stderr.write('已生成 SQL：%s\n' % args.out)
    else:
        sys.stdout.write(sql)


if __name__ == '__main__':
    main()
