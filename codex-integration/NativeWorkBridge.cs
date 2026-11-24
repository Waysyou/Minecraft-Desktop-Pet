// Minecraft 2.4.12 / 2.5 remain the renderer. Only their documented-in-source window
// messages are sent; no SendInput, OS keyboard injection, or Codex UI patching.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Wire
{
    internal const string Pipe="Waysyou.Minecraft.NativeBridge.v2";
    internal static readonly string Root=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    internal static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=128*1024};}
    internal static string Str(IDictionary<string,object> data,string key,int max)
    {object value;string s=data.TryGetValue(key,out value)?Convert.ToString(value):"";return new string(s.Where(c=>!Char.IsControl(c)).Take(max).ToArray());}
    internal static bool Send(Dictionary<string,object> data)
    {
        try{using(var pipe=new NamedPipeClientStream(".",Pipe,PipeDirection.Out))
        {pipe.Connect(250);using(var writer=new StreamWriter(pipe,new UTF8Encoding(false))){writer.WriteLine(Json().Serialize(data));writer.Flush();}}return true;}
        catch(IOException){return false;}catch(TimeoutException){return false;}
    }
    internal static void Hook()
    {
        try
        {
            if(Environment.GetEnvironmentVariable("MINECRAFT_COMPANION_CHAT")!="1")
            {
                // Do not forward prompts, tool arguments, outputs, transcripts or credentials.
                var raw=Console.In.ReadToEnd();if(raw.Length>4*1024*1024)return;
                var input=new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Deserialize<Dictionary<string,object>>(raw);
                Send(new Dictionary<string,object>{{"kind","hook"},{"session",Str(input,"session_id",100)},
                    {"turn",Str(input,"turn_id",100)},{"event",Str(input,"hook_event_name",40)},
                    {"tool",Str(input,"tool_name",100)},
                    {"workspace",Path.GetFileName(Str(input,"cwd",500).TrimEnd('\\','/'))}});
            }
        }catch(Exception){}finally{Console.Write("{}");}
    }
}

internal static class Native
{
    internal delegate IntPtr KeyboardProc(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)]internal static extern IntPtr SetWindowsHookEx(int type,KeyboardProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")]internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]internal static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")]internal static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]internal static extern IntPtr GetModuleHandle(string module);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")]internal static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")]internal static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
    [DllImport("user32.dll")]internal static extern bool GetWindowRect(IntPtr w,out Rect rect);
    [DllImport("user32.dll")]internal static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")]internal static extern bool ShowWindow(IntPtr w,int command);
    [DllImport("user32.dll")]internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]internal static extern IntPtr SendMessageTimeout(IntPtr w,uint message,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
    [StructLayout(LayoutKind.Sequential)]internal struct Rect{internal int Left,Top,Right,Bottom;}
    internal static bool Message(IntPtr window,uint message,int wp,int lp)
    {IntPtr result;return window!=IntPtr.Zero && SendMessageTimeout(window,message,new IntPtr(wp),new IntPtr(lp),2,250,out result)!=IntPtr.Zero;}
    internal static Point HandlePosition(Rectangle pet,Rectangle work,Size size)
    {return new Point(Math.Max(work.Left+6,Math.Min(work.Right-size.Width-6,pet.Left+(pet.Width-size.Width)/2)),Math.Max(work.Top+6,Math.Min(work.Bottom-size.Height-6,pet.Bottom-size.Height)));}
}

// Current physical key names only, retained in RAM for at most 900 ms after
// release. No text translation, screenshots, history, persistence or network.
internal sealed class KeyNames:IDisposable
{
    private readonly HashSet<int> held=new HashSet<int>();
    private readonly Native.KeyboardProc callback;
    private IntPtr hook;
    private string last="";
    private DateTime until=DateTime.MinValue;
    internal bool Ready{get{return hook!=IntPtr.Zero;}}
    internal KeyNames(){callback=OnKey;}
    internal void Enable(bool enabled)
    {
        if(enabled && hook==IntPtr.Zero)hook=Native.SetWindowsHookEx(13,callback,Native.GetModuleHandle(null),0);
        if(!enabled){if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);hook=IntPtr.Zero;held.Clear();last="";}
    }
    private IntPtr OnKey(int code,IntPtr message,IntPtr data)
    {
        if(code>=0)
        {
            int vk=Marshal.ReadInt32(data),flags=Marshal.ReadInt32(data,8),msg=message.ToInt32();
            if((flags&0x12)==0 && vk>0 && vk<256 && vk!=0xe7)
            {
                if(msg==0x0100 || msg==0x0104)held.Add(vk);
                else if(msg==0x0101 || msg==0x0105)held.Remove(vk);
                if(held.Count>0)last=Format(held);until=DateTime.UtcNow.AddMilliseconds(900);
            }
        }
        return Native.CallNextHookEx(hook,code,message,data);
    }
    internal string Current(DateTime now)
    {
        foreach(int key in held.ToArray())if(Native.GetAsyncKeyState(key)>=0)held.Remove(key);
        if(held.Count>0){last=Format(held);until=now.AddMilliseconds(900);}
        return now<=until?last:"";
    }
    internal static string Format(IEnumerable<int> values)
    {
        return String.Join(" + ",values.OrderBy(Rank).ThenBy(x=>x).Select(Name).Distinct().Take(6).ToArray());
    }
    private static int Rank(int key){string n=Name(key);return n=="Ctrl"?0:n=="Alt"?1:n=="Shift"?2:n=="Win"?3:4;}
    private static string Name(int key)
    {
        if(key==0x11 || key==0xa2 || key==0xa3)return "Ctrl";
        if(key==0x12 || key==0xa4 || key==0xa5)return "Alt";
        if(key==0x10 || key==0xa0 || key==0xa1)return "Shift";
        if(key==0x5b || key==0x5c)return "Win";
        if(key>=0x41 && key<=0x5a)return ((char)key).ToString();
        if(key>=0x30 && key<=0x39)return ((char)key).ToString();
        switch(key){case 8:return "Backspace";case 9:return "Tab";case 13:return "Enter";case 27:return "Esc";case 32:return "Space";
            case 37:return "←";case 38:return "↑";case 39:return "→";case 40:return "↓";case 0xba:return ";";case 0xbb:return "=";
            case 0xbc:return ",";case 0xbd:return "-";case 0xbe:return ".";case 0xbf:return "/";case 0xc0:return "`";
            case 0xdb:return "[";case 0xdc:return "\\";case 0xdd:return "]";case 0xde:return "'";default:return ((Keys)key).ToString();}
    }
    public void Dispose(){Enable(false);}
}

internal sealed class WorkItem
{
    public string session,turn,workspace,state;
    public DateTime updated;
}
internal sealed class WorkState
{
    internal readonly Dictionary<string,WorkItem> Items=new Dictionary<string,WorkItem>();
    internal bool Apply(Dictionary<string,object> data,DateTime now)
    {
        string ev=Wire.Str(data,"event",40),session=Wire.Str(data,"session",100),turn=Wire.Str(data,"turn",100),state;
        switch(ev)
        {
            case "UserPromptSubmit":state="thinking";break;
            case "PreToolUse":state=Wire.Str(data,"tool",100).IndexOf("request_user_input",StringComparison.OrdinalIgnoreCase)>=0?"waiting":"working";break;
            case "PostToolUse":state="thinking";break;
            case "PermissionRequest":state="waiting";break;
            case "Stop":state="completed";break;
            case "Interrupt":state="interrupted";break;
            case "SessionEnd":state="ended";break;
            default:return false;
        }
        if(session.Length==0)return false;
        WorkItem item;
        if(!Items.TryGetValue(session,out item))
        {item=new WorkItem{session=session,turn=turn,workspace=Wire.Str(data,"workspace",80)};Items[session]=item;}
        else if(ev=="UserPromptSubmit"){item.turn=turn;}
        else if(turn.Length!=0 && item.turn.Length!=0 && item.turn!=turn)return false;
        item.state=state;item.updated=now;
        if(Items.Count>32){var oldest=Items.Values.OrderBy(x=>x.updated).First();Items.Remove(oldest.session);}
        return true;
    }
    internal string Aggregate(DateTime now)
    {
        var recent=Items.Values.Where(x=>now-x.updated<TimeSpan.FromMinutes(10)).ToArray();
        foreach(string s in new[]{"working","thinking","waiting"})if(recent.Any(x=>x.state==s))return s;
        var latest=recent.OrderByDescending(x=>x.updated).FirstOrDefault();
        if(latest==null)return Items.Count==0?"unconnected":"unknown";
        return now-latest.updated<TimeSpan.FromSeconds(12)?latest.state:"idle";
    }
    internal static string Label(string s)
    {
        switch(s){case "working":return "正在调用工具";case "thinking":return "正在处理你的任务";
            case "waiting":return "需要你的输入或确认";case "completed":return "本轮已回复";
            case "interrupted":return "本轮已停止";case "ended":return "会话已结束";
            case "idle":return "待命中";case "unknown":return "状态已过期，等待新事件";
            case "desktop":return "普通桌宠模式";default:return "等待 Codex 状态连接";}
    }
}

internal sealed class PetBar:Form
{
    internal IntPtr Pet;
    internal Action OpenPanel,OpenCodex;
    internal readonly Label TextLabel;
    internal readonly Label KeyLabel;
    private readonly System.Windows.Forms.Timer follow=new System.Windows.Forms.Timer{Interval=33};
    internal PetBar()
    {
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;ClientSize=new Size(270,70);
        BackColor=Color.FromArgb(30,35,43);Opacity=.95;
        TextLabel=new Label{Text="拖动桌宠",Location=new Point(10,4),Size=new Size(230,21),ForeColor=Color.White,Cursor=Cursors.SizeAll,Font=new Font("Microsoft YaHei UI",9f)};
        KeyLabel=new Label{Text="按键：等待输入",Location=new Point(10,27),Size=new Size(250,20),ForeColor=Color.FromArgb(159,228,203),Font=new Font("Consolas",10f)};
        var panel=new LinkLabel{Text="任务面板",Location=new Point(10,49),Size=new Size(85,18),LinkColor=Color.FromArgb(164,205,255)};
        var codex=new LinkLabel{Text="回到 Codex",Location=new Point(163,49),Size=new Size(95,18),LinkColor=panel.LinkColor};
        Controls.AddRange(new Control[]{TextLabel,KeyLabel,panel,codex});
        panel.LinkClicked+=(s,e)=>OpenPanel();codex.LinkClicked+=(s,e)=>OpenCodex();
        TextLabel.MouseDown+=(s,e)=>{if(e.Button==MouseButtons.Left){Native.Message(Pet,0x0201,1,0);follow.Start();}};
        TextLabel.DoubleClick+=(s,e)=>OpenPanel();
        follow.Tick+=(s,e)=>{if((Control.MouseButtons&MouseButtons.Left)==0)follow.Stop();Follow();};
    }
    protected override bool ShowWithoutActivation{get{return true;}}
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
    internal void Follow()
    {
        Native.Rect r;if(Pet==IntPtr.Zero || !Native.GetWindowRect(Pet,out r))return;
        var bounds=Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
        Location=Native.HandlePosition(bounds,Screen.FromRectangle(bounds).WorkingArea,Size);
    }
    protected override void Dispose(bool disposing){if(disposing)follow.Dispose();base.Dispose(disposing);}
}

internal sealed class Companion:Form
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
        settings.Controls.Add(new Label{Text="角色由 GitHub 下载的 Minecraft桌宠.exe 实时绘制。\n皮肤、披风、鼠标跟随和姿势保持原功能。\n\n敲字动作：原版设置 → 常规 → 全局键盘互动 + 精准按键，\n姿势选择坐下，或自定义坐姿。\n\n键名浮层：右键桌宠下方小条 → 显示按键名称。\n只短暂显示当前按键组合，不保存、不上传按键。\n\n任务联动需要在 Codex 中审核并信任 hooks。\n当前面板不包含聊天、语音或自动发送提示词。",Dock=DockStyle.Fill,Padding=new Padding(18,76,12,12)});settings.Controls.Add(actions);
        tabs.TabPages.AddRange(new[]{work,settings});Controls.Add(tabs);
        FormClosing+=(o,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
    }
}

internal sealed class Bridge:ApplicationContext
{
    private readonly string petExe=Path.GetFullPath(Path.Combine(Wire.Root,"..","Minecraft-Desktop-Pet","Minecraft桌宠.exe"));
    private readonly string dataDir=Path.GetFullPath(Path.Combine(Wire.Root,"..","data","Minecraft-Desktop-Pet"));
    private readonly WorkState work=new WorkState();
    private readonly NotifyIcon tray=new NotifyIcon();
    private readonly PetBar bar=new PetBar();
    private readonly Companion panel;
    private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=80};
    private readonly EventWaitHandle stop=new EventWaitHandle(false,EventResetMode.AutoReset,"Waysyou.MinecraftCodexBridge.Stop.v2");
    private readonly ToolStripMenuItem automatic=new ToolStripMenuItem("响应 Codex 任务状态"){Checked=true,CheckOnClick=true};
    private readonly ToolStripMenuItem visible=new ToolStripMenuItem("显示气泡与拖动条"){Checked=true,CheckOnClick=true};
    private readonly ToolStripMenuItem keyItem=new ToolStripMenuItem("显示按键名称"){Checked=true,CheckOnClick=true};
    private readonly KeyNames keyNames=new KeyNames();
    private readonly Control dispatcher=new Control();
    private volatile bool disposed;
    private NamedPipeServerStream listener;
    private bool codexRunning;
    private IntPtr pet,lastPet;
    private DateTime lastLaunch=DateTime.MinValue,lastCheck=DateTime.MinValue,lastPulse=DateTime.MinValue,lastStartup=DateTime.MinValue;
    private DateTime releaseAt=DateTime.MaxValue,demoUntil=DateTime.MinValue;
    private string demo="",lastStatus="",codexPath="",lastLog="";
    private string petVersion="unknown",animationState="";
    private bool keyDown;
    internal Bridge()
    {
        using(var digest=SHA256.Create())using(var file=File.OpenRead(petExe))
        {
            string hash=BitConverter.ToString(digest.ComputeHash(file)).Replace("-","").ToLowerInvariant();
            if(hash=="d578e0e70250940e385f27937d451b63d3e4ddf7bb342d02e0e53d966c65c97f")petVersion="2.5";
            else if(hash=="67e762dac4c1e52d9691ecff2ffd6f9f8d492405e62fa2687e51c3be8c93704a")petVersion="2.4.12";
        }
        dispatcher.CreateControl();
        panel=new Companion(()=>Native.Message(pet,0x0111,100,0),OpenCodex,Guide);
        bar.OpenPanel=OpenPanel;bar.OpenCodex=OpenCodex;
        var menu=new ContextMenuStrip();menu.Items.Add("打开任务面板",null,(s,e)=>OpenPanel());menu.Items.Add(automatic);menu.Items.Add(visible);menu.Items.Add(keyItem);
        menu.Items.Add("原版桌宠设置",null,(s,e)=>Native.Message(pet,0x0111,100,0));
        menu.Items.Add("演示工作动作（8秒）",null,(s,e)=>{demo="working";demoUntil=DateTime.UtcNow.AddSeconds(8);});
        menu.Items.Add("接入与恢复说明",null,(s,e)=>Guide());menu.Items.Add("退出联动，保留原桌宠",null,(s,e)=>ExitThread());
        bar.ContextMenuStrip=menu;tray.ContextMenuStrip=menu;tray.Icon=Icon.ExtractAssociatedIcon(petExe)??SystemIcons.Application;
        tray.Text="Minecraft 本体 × Codex";tray.Visible=true;tray.DoubleClick+=(s,e)=>OpenPanel();
        LoadPreferences();keyNames.Enable(keyItem.Checked);
        automatic.CheckedChanged+=(s,e)=>SavePreferences();visible.CheckedChanged+=(s,e)=>SavePreferences();
        keyItem.CheckedChanged+=(s,e)=>{keyNames.Enable(keyItem.Checked);SavePreferences();};
        Task.Run((Action)Listen);timer.Tick+=(s,e)=>Tick();timer.Start();Tick();
    }
    private void Guide(){Process.Start(new ProcessStartInfo(Path.Combine(Wire.Root,"原生本体接入说明.md")){UseShellExecute=true});}
    private void OpenPanel(){panel.Show();panel.WindowState=FormWindowState.Normal;panel.Activate();RefreshPanel(DateTime.UtcNow);}
    private void OpenCodex()
    {
        foreach(var p in Process.GetProcessesByName("ChatGPT"))using(p)try
        {if(p.MainModule.FileName.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0 && p.MainWindowHandle!=IntPtr.Zero)
            {Native.ShowWindow(p.MainWindowHandle,9);Native.SetForegroundWindow(p.MainWindowHandle);return;}}
        catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}
        if(File.Exists(codexPath))Process.Start(new ProcessStartInfo(codexPath){UseShellExecute=true});
    }
    private void DetectCodex()
    {
        codexRunning=false;
        foreach(var p in Process.GetProcessesByName("ChatGPT"))using(p)try
        {string path=p.MainModule.FileName;if(path.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0){codexRunning=true;codexPath=path;break;}}
        catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}
    }
    private IntPtr FindPet()
    {
        IntPtr window=Native.FindWindow("MinecraftDesktopPetNative",null);if(window==IntPtr.Zero)return window;
        uint pid;Native.GetWindowThreadProcessId(window,out pid);
        try{using(var p=Process.GetProcessById((int)pid))return String.Equals(p.MainModule.FileName,petExe,StringComparison.OrdinalIgnoreCase)?window:IntPtr.Zero;}
        catch(ArgumentException){return IntPtr.Zero;}catch(System.ComponentModel.Win32Exception){return IntPtr.Zero;}
    }
    private void Listen()
    {
        while(!disposed)
        {
            try
            {
                var acl=new PipeSecurity();acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                using(var pipe=new NamedPipeServerStream(Wire.Pipe,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,acl))
                {
                    listener=pipe;pipe.WaitForConnection();
                    using(var reader=new StreamReader(pipe,Encoding.UTF8))
                    {
                        var line=new StringBuilder();int c;
                        while(line.Length<8192 && (c=reader.Read())>=0 && c!='\n')line.Append((char)c);
                        if(line.Length>=8192)continue;
                        var data=Wire.Json().Deserialize<Dictionary<string,object>>(line.ToString());
                        if(!disposed)dispatcher.BeginInvoke((Action)(()=>Receive(data)));
                    }
                }
            }
            catch(ObjectDisposedException){if(disposed)return;}
            catch(IOException){if(disposed)return;}
            catch(ArgumentException){}
            catch(InvalidOperationException){if(disposed)return;}
            finally{listener=null;}
        }
    }
    private void Receive(Dictionary<string,object> data)
    {
        if(data==null)return;
        string kind=Wire.Str(data,"kind",30);
        if(kind=="hook")work.Apply(data,DateTime.UtcNow);
        else if(kind=="open")OpenPanel();
        else if(kind=="demo"){demo="working";demoUntil=DateTime.UtcNow.AddSeconds(8);}
    }
    private void Release()
    {if(keyDown){Native.Message(lastPet,0x8002,0x1001,0);keyDown=false;}releaseAt=DateTime.MaxValue;}
    private void Animate(string state,DateTime now)
    {
        if(keyDown && now>=releaseAt)Release();
        if(pet!=lastPet){Release();lastPet=pet;animationState="";}
        if(state!=animationState)
        {
            animationState=state;
            if(state=="completed")
            {
                Release();
                if(petVersion=="2.5")Native.Message(pet,0x0111,168,0); // native apple animation, once
                else if(petVersion=="2.4.12")Native.Message(pet,0x8003,0,0);
            }
        }
        int interval=state=="working"?240:state=="thinking"?1000:0;
        if(interval==0){Release();return;}
        if(now-lastPulse<TimeSpan.FromMilliseconds(interval))return;
        lastPulse=now;
        {
            // App-private virtual code 0x1001 has no real key position. The original
            // Pose chooses alternating hands. No WM_KEYDOWN or SendInput is used.
            if(Native.Message(pet,0x8002,0x1001,1)){keyDown=true;releaseAt=now.AddMilliseconds(80);}
        }
    }
    private void Tick()
    {
        if(disposed)return;
        try
        {
            if(stop.WaitOne(0)){ExitThread();return;}
            DateTime now=DateTime.UtcNow;
            if(now-lastCheck>TimeSpan.FromSeconds(1)){lastCheck=now;DetectCodex();pet=FindPet();RefreshPanel(now);}
            if(pet==IntPtr.Zero && now-lastLaunch>TimeSpan.FromSeconds(15))
            {lastLaunch=now;Process.Start(new ProcessStartInfo(petExe,"--user-data-dir=\""+dataDir+"\""){WorkingDirectory=Path.GetDirectoryName(petExe),UseShellExecute=false});}
            string state=now<demoUntil?demo:codexRunning && automatic.Checked?work.Aggregate(now):"desktop";
            Animate(state,now);
            string keys=keyItem.Checked?keyNames.Current(now):"";
            bar.KeyLabel.Text=keyItem.Checked?(keyNames.Ready?(keys.Length>0?keys:"按键：等待输入"):"按键显示未连接"):"按键显示已关闭";
            if(pet!=IntPtr.Zero && Native.IsWindowVisible(pet) && visible.Checked)
            {bar.Pet=pet;bar.TextLabel.Text=WorkState.Label(state)+(now<demoUntil?" · 演示":"");bar.Follow();if(!bar.Visible)bar.Show();}
            else bar.Hide();
            string status=Wire.Json().Serialize(new{version=2,petVersion=petVersion,mode="original-native-app",codexRunning=codexRunning,
                ordinaryPetFound=pet!=IntPtr.Zero,ordinaryPetVisible=pet!=IntPtr.Zero && Native.IsWindowVisible(pet),
                bubbleVisible=bar.Visible,state=state,automatic=automatic.Checked,receivedSessions=work.Items.Count,
                lastHookUtc=work.Items.Count==0?(DateTime?)null:work.Items.Values.Max(x=>x.updated),demoActive=now<demoUntil,
                keyboardOverlayEnabled=keyItem.Checked,keyboardHookReady=keyNames.Ready});
            if(status!=lastStatus){File.WriteAllText(Path.Combine(Wire.Root,"status.json"),status,new UTF8Encoding(false));lastStatus=status;}
            SyncStartup(now);
        }
        catch(Exception e){Release();string msg=e.GetType().Name+": "+e.Message;if(msg!=lastLog){File.AppendAllText(Path.Combine(Wire.Root,"native-bridge.log"),DateTimeOffset.Now+" "+msg+Environment.NewLine);lastLog=msg;}}
    }
    private void RefreshPanel(DateTime now)
    {
        if(!panel.Visible)return;panel.Status.Text=codexRunning?WorkState.Label(work.Aggregate(now))+" · 原版 3D 桌宠":"Codex 未运行 · 原版桌宠保持日常模式";
        panel.TasksView.BeginUpdate();panel.TasksView.Items.Clear();
        foreach(var item in work.Items.Values.OrderByDescending(x=>x.updated))
        {var row=new ListViewItem(String.IsNullOrEmpty(item.workspace)?item.session.Substring(0,Math.Min(8,item.session.Length)):item.workspace);
            row.SubItems.Add(WorkState.Label(now-item.updated>TimeSpan.FromMinutes(10)?"unknown":item.state));row.SubItems.Add(item.updated.ToLocalTime().ToString("HH:mm:ss"));panel.TasksView.Items.Add(row);}
        panel.TasksView.EndUpdate();
    }
    private void LoadPreferences()
    {
        string path=Path.Combine(Wire.Root,"bridge-settings.json");if(!File.Exists(path))return;
        try{var prefs=Wire.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));object v;
            if(prefs.TryGetValue("automatic",out v)&&v is bool)automatic.Checked=(bool)v;
            if(prefs.TryGetValue("showBubble",out v)&&v is bool)visible.Checked=(bool)v;
            if(prefs.TryGetValue("showKeyNames",out v)&&v is bool)keyItem.Checked=(bool)v;}catch(ArgumentException){}
    }
    private void SavePreferences(){File.WriteAllText(Path.Combine(Wire.Root,"bridge-settings.json"),Wire.Json().Serialize(new{automatic=automatic.Checked,showBubble=visible.Checked,showKeyNames=keyItem.Checked}));}
    private void SyncStartup(DateTime now)
    {
        if(now-lastStartup<TimeSpan.FromSeconds(30))return;lastStartup=now;
        var settings=Wire.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(dataDir,"settings.json")));object v;
        if(!settings.TryGetValue("autostart",out v)||!(v is bool))return;
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true))
        {if(key==null)return;string wanted="\""+Path.Combine(Wire.Root,"MinecraftCodexBridge.exe")+"\"";
            if((bool)v){if(Convert.ToString(key.GetValue("MinecraftCodexBridge"))!=wanted)key.SetValue("MinecraftCodexBridge",wanted);}
            else if(Convert.ToString(key.GetValue("MinecraftCodexBridge"))==wanted)key.DeleteValue("MinecraftCodexBridge",false);}
    }
    protected override void Dispose(bool disposing)
    {
        if(!disposed){disposed=true;timer.Stop();Release();keyNames.Dispose();var pipe=listener;if(pipe!=null)pipe.Dispose();tray.Visible=false;tray.Dispose();bar.Dispose();panel.Dispose();dispatcher.Dispose();timer.Dispose();stop.Dispose();}
        base.Dispose(disposing);
    }
}

internal static class Program
{
    [STAThread]private static void Main(string[] args)
    {
        if(args.Length==1 && args[0]=="--hook"){Console.InputEncoding=Encoding.UTF8;Wire.Hook();return;}
        if(args.Length==1 && args[0]=="--exit"){try{using(var e=EventWaitHandle.OpenExisting("Waysyou.MinecraftCodexBridge.Stop.v2"))e.Set();}catch(WaitHandleCannotBeOpenedException){}return;}
        if(args.Length==2 && args[0]=="--self-test"){SelfTest(args[1]);return;}
        if(args.Length==1 && args[0]=="--demo"){Wire.Send(new Dictionary<string,object>{{"kind","demo"}});return;}
        if(args.Length==1 && args[0]=="--panel"){Wire.Send(new Dictionary<string,object>{{"kind","open"}});return;}
        bool first;using(var mutex=new Mutex(true,"Waysyou.Minecraft.NativeBridge.v2",out first))
        {if(!first){Wire.Send(new Dictionary<string,object>{{"kind","open"}});return;}
            Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var bridge=new Bridge())Application.Run(bridge);}
    }
    private static void SelfTest(string output)
    {
        var state=new WorkState();var now=DateTime.UtcNow;int passed=0;
        Action<bool> check=ok=>{if(!ok)throw new Exception("Failed at check "+passed);passed++;};
        Func<string,string,string,Dictionary<string,object>> e=(ev,id,turn)=>new Dictionary<string,object>{{"event",ev},{"session",id},{"turn",turn},{"workspace","test"}};
        check(state.Aggregate(now)=="unconnected");check(!state.Apply(e("Unknown","s1","t1"),now));
        check(state.Apply(e("UserPromptSubmit","s1","t1"),now));check(state.Aggregate(now)=="thinking");
        state.Apply(e("PreToolUse","s1","t1"),now);check(state.Aggregate(now)=="working");
        state.Apply(e("UserPromptSubmit","s2","t2"),now);state.Apply(e("Stop","s2","t2"),now);check(state.Aggregate(now)=="working");
        check(!state.Apply(e("Stop","s1","old"),now));check(state.Items["s1"].state=="working");
        state.Apply(e("PermissionRequest","s1","t1"),now);check(state.Aggregate(now)=="waiting");
        state.Apply(e("Interrupt","s1","t1"),now);check(state.Items["s1"].state=="interrupted");
        check(state.Aggregate(now.AddMinutes(11))=="unknown");
        check(!state.Apply(e("Stop","","t1"),now));
        for(int i=0;i<40;i++)state.Apply(e("UserPromptSubmit","x"+i,"t"),now.AddSeconds(i));check(state.Items.Count==32);
        check(Native.HandlePosition(new Rectangle(1695,763,360,420),new Rectangle(0,0,1920,1040),new Size(250,48)).Y==986);
        check(Native.HandlePosition(new Rectangle(-1800,500,300,400),new Rectangle(-1920,0,1920,1040),new Size(250,48)).X<0);
        check(Wire.Str(new Dictionary<string,object>{{"x","ab\ncd"}},"x",3)=="abc");
        check(KeyNames.Format(new[]{0x41,0xa2})=="Ctrl + A");
        check(KeyNames.Format(new[]{0x57,0xa0,0xa2})=="Ctrl + Shift + W");
        check(KeyNames.Format(new[]{0xa2,0xa3,0x43})=="Ctrl + C");
        File.WriteAllText(output,Wire.Json().Serialize(new{passed=passed,failed=0,scope="event aggregation, stale turns, expiry, bounded history, drag bounds"}));
    }
}
