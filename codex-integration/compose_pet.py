from pathlib import Path
from PIL import Image
import json, hashlib, shutil

root=Path('G:/Desktop-Apps/codex-integration')
package=root/'pet-package'
package.mkdir(exist_ok=True)
qa=root/'qa';qa.mkdir(exist_ok=True)
atlas=Image.new('RGBA',(1536,2288))
entries=json.loads((root/'frames.json').read_text(encoding='utf-8'))
rows={}
for entry in entries:
    im=Image.open(entry['path']).convert('RGBA').resize((192,208),Image.Resampling.LANCZOS)
    im.putdata([(r,g,b,a) if a else (0,0,0,0) for r,g,b,a in im.getdata()])
    assert im.getbbox() is not None
    bbox=im.getbbox()
    assert bbox[0]>1 and bbox[1]>1 and bbox[2]<191 and bbox[3]<207, (entry,bbox)
    atlas.paste(im,(entry['col']*192,entry['row']*208))
    rows.setdefault(entry['row'],[]).append((entry['col'],im,entry['state']))
    directory=qa/'frames'/entry['state'];directory.mkdir(parents=True,exist_ok=True)
    im.save(directory/f"{entry['col']:02}.png")
atlas.save(package/'spritesheet.webp',lossless=True,exact=True)
atlas.save(qa/'spritesheet.png')
manifest=dict(id='waysyou-minecraft',displayName='Waysyou · Minecraft',
    description='保留 Waysyou 皮肤与披风的 Minecraft 伙伴，由 Codex 提供原生任务状态、聊天和语音入口。',
    spriteVersionNumber=2,spritesheetPath='spritesheet.webp')
(package/'pet.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
for row,frames in rows.items():
    if row>8:continue
    frames=sorted(frames)
    if row==0:frames=frames[:6]
    images=[]
    for _,im,_ in frames:
        bg=Image.new('RGBA',im.size,'#e8edf5');bg.alpha_composite(im);images.append(bg.convert('RGB'))
    images[0].save(qa/f'{frames[0][2]}.gif',save_all=True,append_images=images[1:],duration=150,loop=0)
shutil.copy2('G:/Desktop-Apps/Minecraft-Desktop-Pet/LICENSE',package/'UPSTREAM-LICENSE.txt')
(package/'SOURCE.md').write_text('角色由用户已安装的皮肤、披风离线渲染，未上传到第三方宠物库。立方体 UV 依据 DLmember/Minecraft-Desktop-Pet 的 MIT 源码。\nhttps://github.com/DLmember/Minecraft-Desktop-Pet/blob/95ec18d/native/src/render.rs\n',encoding='utf-8')
(qa/'export-summary.json').write_text(json.dumps(dict(frames=len(entries),size=list(atlas.size),skin_sha256=hashlib.sha256(Path('G:/Desktop-Apps/data/Minecraft-Desktop-Pet/skin.png').read_bytes()).hexdigest(),atlas_sha256=hashlib.sha256((package/'spritesheet.webp').read_bytes()).hexdigest()),indent=2),encoding='utf-8')
print('COMPOSED',len(entries),atlas.size)
