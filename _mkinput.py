s = open('_p2c_input.md', encoding='utf-8').read()
old = '【本批任务】第一批：角色 4 View —— CHR-赵日天、CHR-刘如烟'

b2 = '【本批任务】第二批：场景 —— SCN-天印山门、SCN-天印封印台（另出 SCN-天印封印台·裂界状态图 附加页）'
b3 = '【本批任务】第三批：道具与特效 —— PRP-裂仇刃、PRP-守印符环、PRP-天道锁链（仅静态结构图）；VFX 按合并模式并入宿主资产状态图'

assert old in s, '未找到本批任务标记'
open('_p2c2_input.md', 'w', encoding='utf-8').write(s.replace(old, b2))
open('_p2c3_input.md', 'w', encoding='utf-8').write(s.replace(old, b3))
print('已生成 _p2c2_input.md / _p2c3_input.md')
