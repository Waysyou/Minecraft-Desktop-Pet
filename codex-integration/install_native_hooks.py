"""Install a small user hook config. Does not grant Codex hook trust."""
from pathlib import Path
import json,datetime,hashlib
root=Path(__file__).resolve().parent
destination=Path.home()/'.codex'/'hooks.json'
events=['UserPromptSubmit','PreToolUse','PostToolUse','PermissionRequest','Stop','Interrupt','SessionEnd']
command='G:/Desktop-Apps/codex-integration/MinecraftEventRelay.exe --hook'
old_command='G:/Desktop-Apps/codex-integration/MinecraftCodexBridge.exe --hook'
config={'description':'Minecraft original native desktop pet: local status metadata only. Review before trusting.',
        'hooks':{event:[{'hooks':[{'type':'command','command':command,'commandWindows':command,'timeout':3}]}] for event in events}}
(root/'minecraft-hooks.json').write_text(json.dumps(config,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
backup=root/'backups'/'native-hooks'
backup.mkdir(parents=True,exist_ok=True)
if destination.exists():
 raw=destination.read_bytes();prior=json.loads(raw)
 (backup/('hooks-before-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S')+'.json')).write_bytes(raw)
 for event,groups in config['hooks'].items():
  active=prior.setdefault('hooks',{}).setdefault(event,[])
  for group in active:
   group['hooks']=[h for h in group.get('hooks',[]) if h.get('command')!=old_command]
  active[:]=[g for g in active if g.get('hooks')]
  if not any(any(h.get('command')==command for h in g.get('hooks',[])) for g in active):active.extend(groups)
 config=prior
else:
 (backup/'hooks-file-was-absent.txt').write_text('No hooks.json existed before Minecraft native bridge installation.\n',encoding='utf8')
destination.write_text(json.dumps(config,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
report={'path':str(destination),'events':events,'sha256':hashlib.sha256(destination.read_bytes()).hexdigest(),
        'automatic_trust_granted':False,'requires_user_hook_review':True}
(root/'qa-native'/'hook-installation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps(report,ensure_ascii=False))
