#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把一个外部 Agent Skill 包（SKILL.md + references/*.md）整体导入 ManhuaPipeline，
落成 DirectorSkillPacks / DirectorSkillDocs 两张表，之后规则就能在系统里在线改。

用法：
    python ImportSkillPack.py --dir "F:\\80 Agent Skill\\short-drama-director-v6.8" --out _pack.sql
    sqlcmd -S . -d ManhuaPipeline -U <user> -P <pwd> -C -f 65001 -i _pack.sql

要点：
1. 每个 md 按文件名前缀打「加载范围」标记（always / stage:Px / when:xxx / ref），
   控制它什么时候进 prompt——38 份全塞进上下文会撑爆，按需加载是必须的。
   映射写在下面的 SCOPE_MAP 里，导入后还能在系统页面上改。
2. SKILL.md 里被标 ★ 的文件记为 IsCore=1（原包的"权威文件"标记）。
3. 幂等：按 PackKey + FileName 判重，重复导入是覆盖（内容变了会更新，不是插重复行）。
"""

import argparse
import os
import re
import sys

H1_RE = re.compile(r'^#\s+(.+)')

# 文件名 -> (Scope, ScopeValue)。没列出的落 ref（备查，不进 prompt）
SCOPE_MAP = {
    # 常驻核心：路由总控 + 质量门禁 + 投喂格式，每次调用都要有
    'asset-first-pipeline':        ('always', None),
    'model-adapters':              ('always', None),
    'quality-gate-review':         ('always', None),
    'prompt-feeding-checklist':    ('always', None),
    'aspect-ratio-adaptation':     ('always', None),
    # 按阶段
    'screenplay-gate-engine':      ('stage', 'P1'),
    'dialogue-doctor-7d':          ('stage', 'P1'),
    'dialogue-speed-check':        ('stage', 'P1'),
    'emotion-beat-curve':          ('stage', 'P1'),
    'asset-spatial-ledger':        ('stage', 'P2'),
    'character-lineage-and-sheets': ('stage', 'P2'),
    'production-ledger-handbook':  ('stage', 'P2'),
    'cinematic-dramaturgy-rules':  ('stage', 'P3'),
    'camera-specs-15rules':        ('stage', 'P3'),
    'camera-transitions-6types':   ('stage', 'P3'),
    'spatial-topview-camera':      ('stage', 'P3'),
    'lighting-and-antifake':       ('stage', 'P3'),
    'audiovisual-aesthetic-presets': ('stage', 'P3'),
    'seedance-render-engine':      ('stage', 'P4'),
    'storyboard-board-lite':       ('stage', 'P4'),
    'agent-platform-adapters':     ('stage', 'P4'),
    'comfyui-canvas-automation':   ('stage', 'P4'),
    # 按需：有打戏才加载
    'action-previs-15grid':        ('when', '战斗'),
    'xuanhuan-magic-combat':       ('when', '战斗'),
    'martial-arts-combat-library': ('when', '战斗'),
    'martial-arts-arsenal':        ('when', '战斗'),
    'authentic-martial-taxonomy':  ('when', '战斗'),
    'combat-direction-engine':     ('when', '战斗'),
    'combat-rhythm-defense3state': ('when', '战斗'),
    'action-cinematography-breakdown': ('when', '战斗'),
    'action-ultimate-finisher':    ('when', '战斗'),
    'anime-ultimate-vfx-paradigm': ('when', '战斗'),
    # 按需：微表情专项
    'facs-micro-expression':       ('when', '微表情'),
    'facs-au-dictionary':          ('when', '微表情'),
    'facs-emotion-recipes':        ('when', '微表情'),
    'wenxi-micro-expression':      ('when', '微表情'),
    # 备查
    'spatial-reference-system-V3': ('ref', None),
    'platform-safety-compliance-guide': ('ref', None),
}


def read(path):
    with open(path, 'r', encoding='utf-8') as f:
        return f.read()


def esc(s):
    return (s or '').replace("'", "''")


def nv(s):
    return "N'" + esc(s) + "'"


def pick_title(text, fallback):
    for ln in text.split('\n'):
        m = H1_RE.match(ln)
        if m:
            return m.group(1).strip()[:200]
    return fallback


def main():
    ap = argparse.ArgumentParser(description='导入 Agent Skill 包到 DirectorSkill* 表')
    ap.add_argument('--dir', required=True)
    ap.add_argument('--pack-key', default=None)
    ap.add_argument('--version', default=None)
    ap.add_argument('--out', required=True)
    args = ap.parse_args()

    skill_md = os.path.join(args.dir, 'SKILL.md')
    if not os.path.exists(skill_md):
        sys.exit('目录下没有 SKILL.md：%s' % args.dir)

    meta = read(skill_md)
    key = args.pack_key
    if not key:
        m = re.search(r'^name:\s*(.+)', meta, re.M)
        key = m.group(1).strip() if m else os.path.basename(args.dir.rstrip('\\/'))
    ver = args.version
    if not ver:
        m = re.search(r'V(\d+\.\d+)', meta)
        ver = 'V' + m.group(1) if m else 'V1.0'

    ref_dir = os.path.join(args.dir, 'references')
    files = [('SKILL.md', os.path.join(args.dir, 'SKILL.md'))]
    if os.path.isdir(ref_dir):
        files += sorted((f, os.path.join(ref_dir, f))
                        for f in os.listdir(ref_dir) if f.lower().endswith('.md'))

    L = ['-- 由 Scripts/ImportSkillPack.py 生成', 'SET NOCOUNT ON;', 'BEGIN TRAN;', '']
    L.append('DECLARE @pk INT;')
    L.append('SELECT @pk = PackId FROM DirectorSkillPacks WHERE PackKey = %s;' % nv(key))
    L.append('IF @pk IS NULL')
    L.append('BEGIN')
    L.append('    INSERT INTO DirectorSkillPacks(PackKey, Name, Version, SourcePath, Description)')
    L.append('    VALUES(%s, %s, %s, %s, %s);' % (nv(key), nv(key), nv(ver), nv(args.dir),
                                                  nv('由 %s 导入' % os.path.basename(args.dir.rstrip('\\/')))))
    L.append('    SET @pk = SCOPE_IDENTITY();')
    L.append('END')
    L.append('ELSE')
    L.append('    UPDATE DirectorSkillPacks SET Version=%s, SourcePath=%s, UpdatedAt=SYSDATETIME() WHERE PackId=@pk;' % (nv(ver), nv(args.dir)))
    L.append('')

    n = 0
    for fname, fpath in files:
        stem = fname[:-3]
        text = read(fpath)
        title = pick_title(text, stem)
        scope, scope_val = SCOPE_MAP.get(stem, ('ref', None))
        core = ('★ ' + fname) in meta or ('★' + fname) in meta
        n += 1
        L.append('IF EXISTS (SELECT 1 FROM DirectorSkillDocs WHERE PackId=@pk AND FileName=%s)' % nv(fname))
        L.append('    UPDATE DirectorSkillDocs SET Title=%s, Scope=%s, ScopeValue=%s, IsCore=%d, Chars=%d, SortOrder=%d, Content=%s, UpdatedAt=SYSDATETIME()'
                 ' WHERE PackId=@pk AND FileName=%s;'
                 % (nv(title), nv(scope), nv(scope_val) if scope_val else 'NULL', 1 if core else 0,
                    len(text), n, nv(text), nv(fname)))
        L.append('ELSE')
        L.append('    INSERT INTO DirectorSkillDocs(PackId, FileName, Title, Scope, ScopeValue, IsCore, Chars, SortOrder, Content)')
        L.append('    VALUES(@pk, %s, %s, %s, %s, %d, %d, %d, %s);'
                 % (nv(fname), nv(title), nv(scope), nv(scope_val) if scope_val else 'NULL',
                    1 if core else 0, len(text), n, nv(text)))
    L.append('')
    L.append('COMMIT TRAN;')
    L.append("PRINT 'skill pack imported.';")

    with open(args.out, 'w', encoding='utf-8') as f:
        f.write('\n'.join(L))
    sys.stderr.write('包 %s %s，文档 %d 份 -> %s\n' % (key, ver, len(files), args.out))
    from collections import Counter
    c = Counter(SCOPE_MAP.get(f[:-3], ('ref', None))[0] for f, _ in files)
    sys.stderr.write('加载分布：%s\n' % dict(c))


if __name__ == '__main__':
    main()
