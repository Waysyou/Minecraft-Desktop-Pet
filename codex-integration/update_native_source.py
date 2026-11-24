from pathlib import Path
p=Path(__file__).parent/'NativeWorkBridge.cs'
s=p.read_text(encoding='utf-8')
a=s.index('internal sealed class Companion:Form')
b=s.index('internal sealed class Bridge:ApplicationContext')
s=s[:a]+'''internal sealed class Companion:Form
{
    internal readonly ListView TasksView=new ListView{View=View.Details,FullRowSelect=true,Dock=DockStyle.Fill};
    internal readonly Label Status=new Label{Dock=DockStyle.Top,Height=52,Padding=new Padding(12,12,0,0)};
    internal Companion(Action original,Action codex,Action guide)
    {
        Text="Waysyou · Minecraft 任务面板";Size=new Size(650,470);MinimumSize=new Size(520,380);
        StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",9f);
        var tabs=new TabControl{Dock=DockStyle.Fill};var work=new TabPage("Codex 任务");var settings=new TabPage("桌宠与按键");
        TasksView.Columns.Add("工作区",160);TasksView.Columns.Add("状态",240);TasksView.Columns.Add("更新",90);
        work.Controls.Add(TasksView);work.Controls.Add(Status);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=60,Padding=new Padding(12)};
        foreach(var pair in new[]{new KeyValuePair<string,Action>("原版桌宠设置",original),new KeyValuePair<string,Action>("回到 Codex",codex),new KeyValuePair<string,Action>("接入与恢复说明",guide)})
        {var button=new Button{Text=pair.Key,AutoSize=true,Height=34};var action=pair.Value;button.Click+=(o,e)=>action();actions.Controls.Add(button);}
        settings.Controls.Add(new Label{Text="角色由 GitHub 下载的 Minecraft桌宠.exe 实时绘制。\\n皮肤、披风、鼠标跟随和姿势保持原功能。\\n\\n敲字动作：原版设置 → 常规 → 全局键盘互动 + 精准按键，\\n姿势选择坐下，或自定义坐姿。\\n\\n键名浮层：右键桌宠下方小条 → 显示按键名称。\\n只短暂显示当前按键组合，不保存、不上传按键。\\n\\n任务联动需要在 Codex 中审核并信任 hooks。\\n当前面板不包含聊天、语音或自动发送提示词。",Dock=DockStyle.Fill,Padding=new Padding(18,76,12,12)});settings.Controls.Add(actions);
        tabs.TabPages.AddRange(new[]{work,settings});Controls.Add(tabs);
        FormClosing+=(o,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
    }
}

''' + s[b:]
s=s.replace('    internal Func<string> FindCli;\n','')
s=s.replace('    private DateTime releaseAt=DateTime.MaxValue,demoUntil=DateTime.MinValue,chatUntil=DateTime.MinValue;', '    private DateTime releaseAt=DateTime.MaxValue,demoUntil=DateTime.MinValue;')
s=s.replace('private string demo="",chatMood="",lastStatus="",codexPath="",lastLog="";', 'private string demo="",lastStatus="",codexPath="",lastLog="";')
s=s.replace('        panel.FindCli=FindCli;panel.ChatMood=s=>{chatMood=s;chatUntil=DateTime.UtcNow.AddSeconds(s=="thinking"?120:8);};\n','')
a=s.index('    private string FindCli()')
b=s.index('    private IntPtr FindPet()',a)
s=s[:a]+s[b:]
s=s.replace('string state=now<chatUntil?chatMood:now<demoUntil?demo:codexRunning && automatic.Checked?work.Aggregate(now):"desktop";', 'string state=now<demoUntil?demo:codexRunning && automatic.Checked?work.Aggregate(now):"desktop";')
s=s.replace('panel.Status.Text=codexRunning?WorkState.Label(work.Aggregate(now))+" · 原版 3D 桌宠":"Codex 未运行 · 原版桌宠保持日常模式";', 'panel.Status.Text=codexRunning?WorkState.Label(work.Aggregate(now))+" · 原版 3D 桌宠":"Codex 未运行 · 原版桌宠保持日常模式";')
p.write_text(s,encoding='utf-8')
print('Removed all model/chat code. Current lines:',len(s.splitlines()))
