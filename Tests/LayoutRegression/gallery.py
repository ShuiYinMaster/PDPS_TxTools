"""Create a review index and contact sheet from rendered production-layout fixtures."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import json
import shutil

root = Path(__file__).resolve().parents[2]
directory = root / 'docs/ui-preview'
for scale in (100,175,200):
    shutil.copy2(root/f'Tests/UiStandards/bin/preview/reachability-{scale}.png',directory/f'ReachabilityFull-{scale}.png')
entries = [
 ('机器人路径点位检查','ReachabilityFull','保留五组单行参数和结果表。'),
 ('机器人基坐标检查','BaseFixture','工具栏按内容计算高度，容差列保留必要宽度。'),
 ('跨资源 IO','CrossFixture','路径行按右栏宽度布局；输入框和浏览按钮完整显示。'),
 ('按颜色导出','ColorFixture','960×540；资源列表填充剩余空间，参数卡按内容收紧。'),
 ('自动录像','RecorderFixture','默认高度 700；录像参数卡去除尾部空白，选择区保持弹性。'),
 ('线生成实体','LineFixture','默认收起日志；窗口按当前截面参数调整高度。'),
 ('CATIA 目录树','CatiaFullFixture','底部操作行按所需高度显示，树区保留数据空间。'),
 ('主题调整','ThemeFixture','默认高度 540，预览和底栏按实际控件高度显示。'),
 ('焊枪定义','WizardFixture','简单步骤收紧高度，铰点及参数步骤保留列表空间。'),
 ('焊点标注','AnnotatorFixture','检查左侧参数栏、选择区及日志隐藏状态。'),
 ('焊点分配 / 更新','AllocatorFixture','选择区保留可用高度；修复隐藏日志时的构造异常。'),
 ('焊点自动分组','GrouperFixture','检查选择区、参数区、四个操作按钮与日志状态。'),
 ('焊点矩阵','MatrixFixture','选择列表、点位表填充数据区。'),
 ('创建围栏','FenceFullFixture','网片、立柱参数完整显示；基线列表使用剩余空间。'),
 ('自动路径规划','PathFixture','修复高 DPI 下恢复默认按钮越界；检查所有现有参数页。'),
 ('导插枪 / 点球','GunFixture','960×600、等宽三列；左侧操作选择框高 188，点列表满高，点球仅保留零件名称输入。'),
 ('机器人竞技场','MechFixture','检查帮助、状态和机器人列表布局。'),
 ('贪吃蛇','SnakeFixture','隐藏日志时收紧窗口；接入统一 DPI 缩放。'),
 ('TxAgent','AgentChat','WebView 离线布局与操作按钮尺寸检查。'),
 ('快捷配方','RecipeQuick','WebView 离线配方列表与操作按钮尺寸检查。'),
]
lines = ['# 插件布局预览', '',
 '预览直接使用当前生产布局构建代码；PS 拾取器、C1 网格、场景回调及主题绘制使用测试替身。WebView 使用离线示例消息和配方。图片用于审阅排布，PS 原生皮肤和真实场景仍需宿主复测。', '',
 '聊天、选择列表和结果表作为数据区保留。没有自定义主窗口的点位选择、库路径同步和资源目录命令不单独绘制窗口。', '',
 '| 插件 | 100% | 175% | 200% | 隐藏日志 | 检查与调整 |', '|---|---|---|---|---|---|']
manifest=[]
for title, key, note in entries:
    def link(scale, suffix=''):
        name=f'{key}-{scale}{suffix}.png'
        return f'[预览]({name})' if (directory/name).exists() else '—'
    lines.append(f'| {title} | {link(100)} | {link(175)} | {link(200)} | {link(100,"-logs-hidden")} | {note} |')
    variants=sorted(p.name for p in directory.glob(f'{key}-100-tab-*.png') if 'logs-hidden' not in p.name)
    manifest.append({'title':title,'image':f'{key}-100.png','variants':variants,'note':note})
lines += ['', '## 页签与步骤', '']
for item in manifest:
    if item['variants']:
        lines.append('- '+item['title']+'：'+' · '.join(f'[第 {i+2} 页]({name})' for i,name in enumerate(item['variants'])))
(directory/'review.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
(directory/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',20)
columns=4;cell_w=400;cell_h=300
sheet=Image.new('RGB',(columns*cell_w,((len(entries)+columns-1)//columns)*cell_h),'#e9edf2')
draw=ImageDraw.Draw(sheet)
for i,(title,key,note) in enumerate(entries):
    path=directory/f'{key}-100.png'
    if not path.exists():raise FileNotFoundError(path)
    x=(i%columns)*cell_w;y=(i//columns)*cell_h
    draw.text((x+12,y+8),title,fill='#162238',font=font)
    with Image.open(path) as source:
        thumbnail=source.convert('RGB');thumbnail.thumbnail((cell_w-20,cell_h-44),Image.Resampling.LANCZOS)
        sheet.paste(thumbnail,(x+(cell_w-thumbnail.width)//2,y+38))
sheet.save(directory/'overview.png')
print(f'Created review index and overview for {len(entries)} interfaces')
