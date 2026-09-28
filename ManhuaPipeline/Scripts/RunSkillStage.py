#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
真跑一次 Skill 阶段（命令行版引擎）。

为什么不直接用系统的 HTTP 接口：接口有 session 鉴权，为跑一次去加免鉴权端点属于污染代码。
这里用同一套拼装逻辑（DocFilter 挑文档 → 拼 System prompt → 调 LLM），产出可回灌
DirectorSkillRunSteps —— 跟在页面上点「执行」落的是同一张表，页面上看得到。

数据走 pyodbc 直连：sqlcmd 文本模式对长中文正文（含换行）会截断、折行，不可靠。
"""
import argparse, json, os, sys, urllib.request
import pyodbc

CNSTR = ("DRIVER={ODBC Driver 17 for SQL Server};SERVER=.;DATABASE=ManhuaPipeline;"
         "UID=ManhuaApp1;PWD=!QAZ2wsxE;TrustServerCertificate=yes")


def conn():
    return pyodbc.connect(CNSTR)


def rows(query, params=()):
    with conn() as cn:
        cur = cn.cursor()
        cur.execute(query, params)
        return [['' if c is None else str(c) for c in r] for r in cur.fetchall()]


def match_filter(doc_filter, scope, scope_value):
    """与 DirectorAgentService.MatchFilter 保持一致。"""
    if not doc_filter:
        return scope == 'always'
    for raw in [x.strip() for x in doc_filter.split(',') if x.strip()]:
        if raw.lower() in ('always', 'ref') and scope == raw.lower():
            return True
        if ':' in raw:
            k, v = raw.split(':', 1)
            if k.lower() in ('stage', 'when') and scope == k.lower() and scope_value == v:
                return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pack', type=int, required=True)
    ap.add_argument('--stage', required=True)
    ap.add_argument('--input', required=True, help='输入文本文件路径')
    ap.add_argument('--save', type=int, default=0, help='回灌的 runId，0=不回灌')
    ap.add_argument('--out', default='')
    ap.add_argument('--head', type=int, default=900, help='终端只打印前 N 字符，全文写文件')
    args = ap.parse_args()

    # 1) 阶段定义
    st = None
    for r in rows("SELECT StageKey, Name, SortOrder, DocFilter, InputsJson, OutputContract, Gates, HumanConfirm "
                  "FROM DirectorSkillStages WHERE PackId=? AND StageKey=?", (args.pack, args.stage)):
        st = dict(stageKey=r[0], name=r[1], sortOrder=int(r[2]), docFilter=r[3] or '',
                  inputsJson=r[4] or '', output=r[5] or '', gates=r[6] or '', humanConfirm=(r[7] == '1'))
    if not st:
        sys.exit('阶段不存在：%s' % args.stage)

    # 2) LLM 渠道（跟系统一样取当前启用的那个）
    prov = rows("SELECT ActiveLLMProvider FROM Users WHERE UserId=1")[0][0]
    cfg = None
    for r in rows("SELECT ApiKey, ApiUrl, ModelName FROM LLMConfigs WHERE UserId=1 AND Provider=? AND IsActive=1", prov):
        cfg = dict(key=r[0], url=r[1], model=r[2])
    if not cfg:
        sys.exit('没有启用的 LLM 渠道：%s' % prov)

    # 3) 文档 + 过滤
    docs = [dict(id=r[0], title=r[1], scope=r[2], scopeValue=r[3], content=r[4])
            for r in rows("SELECT DocId, Title, Scope, ScopeValue, Content FROM DirectorSkillDocs "
                          "WHERE PackId=? ORDER BY SortOrder, DocId", args.pack)]
    picked = [d for d in docs if match_filter(st['docFilter'], d['scope'], d['scopeValue'])]

    # 4) 拼 prompt（结构同 DirectorAgentService.BuildPrompt）
    input_text = open(args.input, encoding='utf-8').read()
    lines = ['# 角色', '你是「AI 分镜导演」。严格按下面的规则工作，规则里没写的动作一律不许自创。', '',
             "# 当前阶段：%s · %s" % (st['stageKey'], st['name'])]
    if st['output']:
        lines += ['', '## 本阶段产出要求', st['output']]
    if st['gates']:
        lines += ['', '## 本阶段门禁（不满足就不要产出，直接指出卡在哪）', st['gates']]
    lines += ['', '# 行业规则（skills 库）']
    for d in picked:
        lines += ['', "## " + d['title'], d['content']]
    lines += ['', '# 输入（用户提供的素材）', input_text]
    system = '\n'.join(lines)

    print('== 阶段 %s · %s' % (st['stageKey'], st['name']))
    print('== 渠道 %s / %s' % (prov, cfg['model']))
    print('== 加载规则 %d 份：%s' % (len(picked), ', '.join(d['title'] for d in picked)))
    print('== prompt 字符数 %d' % len(system))

    # 5) 调用
    body = json.dumps({
        'model': cfg['model'],
        'messages': [{'role': 'system', 'content': system},
                     {'role': 'user', 'content': '请严格按「本阶段产出要求」输出，不要输出多余解释。'}],
        'temperature': 0.7
    }, ensure_ascii=False).encode('utf-8')
    req = urllib.request.Request(cfg['url'], data=body,
                                headers={'Content-Type': 'application/json',
                                         'Authorization': 'Bearer ' + cfg['key']})
    with urllib.request.urlopen(req, timeout=1200) as resp:
        data = json.loads(resp.read().decode('utf-8'))

    text = data['choices'][0]['message']['content']
    usage = data.get('usage', {})
    print('== 真实用量 prompt=%s completion=%s total=%s'
          % (usage.get('prompt_tokens'), usage.get('completion_tokens'), usage.get('total_tokens')))
    print('== 产出 %d 字符（终端只显示前 %d）' % (len(text), args.head))
    print(text[:args.head])

    if args.out:
        open(args.out, 'w', encoding='utf-8').write(text)
        print('\n[全文已写入 %s]' % args.out)

    # 6) 回灌（跟页面点「执行」落同一张表）
    if args.save:
        docs_summary = '\n'.join('[%s] %s · %d字' % (d['id'], d['title'], len(d['content'])) for d in picked)
        status = 'await_confirm' if st['humanConfirm'] else 'done'
        with conn() as cn:
            cur = cn.cursor()
            cur.execute("""INSERT INTO DirectorSkillRunSteps(RunId, StageKey, Name, SortOrder, InputText,
                           DocsSummary, PromptChars, Gates, OutputText, Status)
                           VALUES(?,?,?,?,?,?,?,?,?,?)""",
                        (args.save, st['stageKey'], st['name'], st['sortOrder'], input_text,
                         docs_summary, len(system), st['gates'], text, status))
            cur.execute("UPDATE DirectorSkillRuns SET CurrentStage=?, Status=?, UpdatedAt=SYSDATETIME() WHERE RunId=?",
                        (st['stageKey'], status, args.save))
            cn.commit()
        print('[已回灌 runId=%s step=%s status=%s]' % (args.save, st['stageKey'], status))


if __name__ == '__main__':
    main()
