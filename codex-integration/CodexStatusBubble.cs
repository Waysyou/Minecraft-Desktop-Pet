// V3: a read-only status bubble alongside the original Minecraft pet.
// No keyboard hooks, animation messages, prompt reading or Codex UI changes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Wire
{
    internal const string Pipe="Waysyou.Minecraft.NativeBridge.v2";
    internal const string Stop="Waysyou.MinecraftCodexBridge.Stop.v2";
    internal static readonly string Root=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    internal static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=4*1024*1024};}
    internal static string Str(IDictionary<string,object> data,string key,int max)
    {object v;string s=data.TryGetValue(key,out v)?Convert.ToString(v):"";return new string(s.Where(c=>!Char.IsControl(c)).Take(max).ToArray());}
}

internal static class Native
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")]internal static extern bool IsWindowVisible(IntPtr w);
    [DllImport("user32.dll")]internal static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
    [DllImport("user32.dll")]internal static extern bool GetWindowRect(IntPtr w,out Rect rect);
    [DllImport("user32.dll")]internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]internal static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")]internal static extern IntPtr SendMessageTimeout(IntPtr w,uint m,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleDC(IntPtr h);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(IntPtr h);
    [DllImport("gdi32.dll")]private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")]internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UpdateLayeredWindow(IntPtr h,IntPtr screen,ref XY destination,ref XY size,IntPtr source,ref XY origin,uint key,ref Blend blend,uint flags);
    [StructLayout(LayoutKind.Sequential)]private struct XY{internal int X,Y;internal XY(int x,int y){X=x;Y=y;}}
    [StructLayout(LayoutKind.Sequential,Pack=1)]private struct Blend{internal byte Op,Flags,Alpha,Format;}
    internal static byte PetAlpha(IntPtr window)
    {
        IntPtr value;if(window==IntPtr.Zero || SendMessageTimeout(window,0x800e,IntPtr.Zero,IntPtr.Zero,2,20,out value)==IntPtr.Zero)return 255;
        long result=value.ToInt64();return (result&0xffffff00L)==0x50455400L?(byte)(result&255): (byte)255;
    }
    internal static void PresentBitmap(IntPtr window,IntPtr bitmap,Point position,Size dimensions,byte alpha)
    {
        IntPtr screen=GetDC(IntPtr.Zero),dc=CreateCompatibleDC(screen),old=SelectObject(dc,bitmap);
        try{var dest=new XY(position.X,position.Y);var size=new XY(dimensions.Width,dimensions.Height);var origin=new XY(0,0);
            var blend=new Blend{Op=0,Flags=0,Alpha=alpha,Format=1};
            if(!UpdateLayeredWindow(window,screen,ref dest,ref size,dc,ref origin,0,ref blend,2))throw new System.ComponentModel.Win32Exception();}
        finally{SelectObject(dc,old);DeleteDC(dc);ReleaseDC(IntPtr.Zero,screen);}
    }
    [StructLayout(LayoutKind.Sequential)]internal struct Rect{internal int Left,Top,Right,Bottom;}
    internal static Point Position(Rectangle pet,Rectangle work,Size bubble)
    {
        // Anchor to the character's upper body rather than its transparent canvas edge.
        int x=pet.Left+pet.Width/2-(int)(bubble.Width*.76);
        int y=pet.Top+pet.Height/3-bubble.Height;
        return new Point(Math.Max(work.Left+6,Math.Min(work.Right-bubble.Width-6,x)),
            Math.Max(work.Top+6,Math.Min(work.Bottom-bubble.Height-6,y)));
    }
}

internal sealed class WorkItem
{
    public string session="",turn="",workspace="",state="thinking",lastEvent="",tool="",source="codex";
    public DateTime updated;
    internal readonly Dictionary<string,int> tools=new Dictionary<string,int>();
    internal readonly HashSet<string> retired=new HashSet<string>();
    internal bool Active{get{return state=="thinking" || state=="working" || state=="waiting";}}
    internal bool Terminal{get{return !Active;}}
}

internal sealed class WorkState
{
    internal readonly Dictionary<string,WorkItem> Items=new Dictionary<string,WorkItem>();
    internal bool Apply(Dictionary<string,object> data,DateTime now)
    {
        string ev=Wire.Str(data,"event",40),id=Wire.Str(data,"session",100),turn=Wire.Str(data,"turn",100),tool=Wire.Str(data,"tool",100),source=Wire.Str(data,"source",20);
        if(id.Length==0 || !new[]{"UserPromptSubmit","PreToolUse","PostToolUse","PermissionRequest","Stop","Interrupt","SessionEnd"}.Contains(ev))return false;
        WorkItem item;
        if(!Items.TryGetValue(id,out item)){item=new WorkItem{session=id,turn=turn,source=source.Length>0?source:"codex"};Items.Add(id,item);}
        else
        {
            if(turn.Length>0 && item.retired.Contains(turn))return false;
            if(turn.Length>0 && item.turn.Length>0 && turn!=item.turn)
            {
                // Only a start signal can supersede a known turn. Late completions cannot.
                if(ev!="UserPromptSubmit" && ev!="PreToolUse" && ev!="PermissionRequest")return false;
                item.retired.Add(item.turn);if(item.retired.Count>64)item.retired.Clear();
                item.turn=turn;item.tools.Clear();item.state="thinking";
            }
            else if(item.Terminal && ev!="UserPromptSubmit")return false;
            if(item.turn.Length==0 && turn.Length>0)item.turn=turn;
        }
        string workspace=Wire.Str(data,"workspace",80);if(workspace.Length>0)item.workspace=workspace;
        if(source.Length>0)item.source=source;
        item.updated=now;item.lastEvent=ev;item.tool=tool;
        bool input=tool.IndexOf("request_user_input",StringComparison.OrdinalIgnoreCase)>=0
            || tool.IndexOf("ask_user_question",StringComparison.OrdinalIgnoreCase)>=0
            || tool.IndexOf("AskUserQuestion",StringComparison.OrdinalIgnoreCase)>=0
            || tool.Equals("ExitPlanMode",StringComparison.OrdinalIgnoreCase);
        switch(ev)
        {
            case "UserPromptSubmit":item.tools.Clear();item.state="thinking";break;
            case "PreToolUse":
                if(!input){int n;item.tools.TryGetValue(tool,out n);item.tools[tool]=Math.Min(64,n+1);}
                item.state=input?"waiting":"working";break;
            case "PostToolUse":
                int count;if(item.tools.TryGetValue(tool,out count)){if(count<=1)item.tools.Remove(tool);else item.tools[tool]=count-1;}
                // A question returning from the tool is not evidence that the user answered.
                if(input && item.state=="waiting")break;
                item.state=item.tools.Count>0?"working":"thinking";break;
            case "PermissionRequest":item.state="waiting";break;
            case "Stop":item.state="completed";item.tools.Clear();break;
            case "Interrupt":item.state="interrupted";item.tools.Clear();break;
            case "SessionEnd":item.state="ended";item.tools.Clear();break;
        }
        if(Items.Count>32)
        {
            var victim=Items.Values.OrderBy(x=>x.Active?1:0).ThenBy(x=>x.updated).First();Items.Remove(victim.session);
        }
        return true;
    }
    internal List<WorkItem> Visible(DateTime now)
    {
        return Items.Values.Where(x=>x.Active).OrderByDescending(x=>x.updated).ToList();
    }
    internal static WorkItem SelectSource(List<WorkItem> rows,string source,string currentSession)
    {return rows.FirstOrDefault(x=>x.source==source && x.session==currentSession)??rows.FirstOrDefault(x=>x.source==source);}
    internal static string Label(string state)
    {switch(state){case "working":return "正在调用工具";case "waiting":return "等待你的输入或确认";
        case "completed":return "本轮已回复";case "interrupted":return "本轮已停止";case "ended":return "会话已结束";default:return "正在处理任务";}}
    internal static string Identity(WorkItem item)
    {return (item.source=="qoder"?"Qoder · ":"")+(item.workspace.Length>0?item.workspace:"未指定工作区")+" · "+item.session.Substring(Math.Max(0,item.session.Length-6));}
}

internal sealed class Bridge:ApplicationContext
{
    private readonly string petExe=@"G:\Desktop-Apps\Minecraft-Desktop-Pet\Minecraft桌宠.exe";
    private readonly string dataDir=@"G:\Desktop-Apps\data\Minecraft-Desktop-Pet";
    private readonly WorkState work=new WorkState();
    private readonly StatusBubble bubble=new StatusBubble();
    private readonly TrayBridgeControl dispatcher=new TrayBridgeControl();
    private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=40};
    private readonly KeyStatsService keyStats=new KeyStatsService(Path.Combine(Wire.Root,"key-stats.json"));
    private readonly EventWaitHandle stop=new EventWaitHandle(false,EventResetMode.ManualReset,Wire.Stop);
    private bool showBubble=true;
    private volatile bool disposed;
    private NamedPipeServerStream listener;
    private DateTime lastCheck=DateTime.MinValue,lastStartup=DateTime.MinValue;
    private DateTime lastQuotaCheck=DateTime.MinValue;
    private QuotaSnapshot quota;
    private bool quotaPending;
    private readonly LocalDailyTokenReader dailyReader=new LocalDailyTokenReader(LocalDailyTokenReader.Home,Path.Combine(Wire.Root,"daily-token-index.json"));
    private DailyTokenSnapshot localDaily,officialDaily;
    private DateTime lastDailyCheck=DateTime.MinValue,lastOfficialCheck=DateTime.MinValue;
    private bool dailyPending,officialPending;
    private string officialDailyStatus="pending";
    private readonly QoderUsageReader qoderReader=new QoderUsageReader();
    private QoderUsageSnapshot qoderUsage;
    private string codexMetricsSession="",qoderUsageSession="";
    private DateTime lastQoderUsageCheck=DateTime.MinValue;
    private bool qoderUsagePending;
    private Dictionary<string,RequestMetrics> requestMetrics=new Dictionary<string,RequestMetrics>();
    private bool codexRunning,qoderRunning,previousHostRunning;
    private IntPtr pet;
    private string lastStatus="",lastError="";
    internal Bridge(bool openAppearance=false,bool openKeyStats=false)
    {
        bubble.PetDrag=command=>{IntPtr result;return pet!=IntPtr.Zero && Native.SendMessageTimeout(pet,0x800f,new IntPtr(command),IntPtr.Zero,2,150,out result)!=IntPtr.Zero && result.ToInt64()==0x50455401;};
        dispatcher.BubbleEnabled=()=>showBubble;
        dispatcher.Command=command=>{if(command==1)showBubble=!showBubble;else if(command==2)showBubble=true;else if(command==3)showBubble=false;
            else if(command==4)dispatcher.BeginInvoke((Action)(()=>ExitThread()));else if(command==5)dispatcher.BeginInvoke((Action)(()=>bubble.OpenAppearance()));else if(command==6)dispatcher.BeginInvoke((Action)(()=>KeyStatsForm.OpenShared()));};
        IntPtr controlHandle=dispatcher.Handle;
        // Existing shortcuts launch the bridge. Start the original pet once, never undo a user's later exit.
        pet=FindPet();if(pet==IntPtr.Zero && File.Exists(petExe))
            Process.Start(new ProcessStartInfo(petExe,"--user-data-dir=\""+dataDir+"\""){WorkingDirectory=Path.GetDirectoryName(petExe),UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});
        Task.Run((Action)Listen);timer.Tick+=(s,e)=>Tick();timer.Start();Tick();if(openAppearance)dispatcher.BeginInvoke((Action)(()=>bubble.OpenAppearance()));if(openKeyStats)dispatcher.BeginInvoke((Action)(()=>KeyStatsForm.OpenShared()));
    }
    private IntPtr FindPet()
    {
        var h=Native.FindWindow("MinecraftDesktopPetNative",null);if(h==IntPtr.Zero)return h;
        uint pid;Native.GetWindowThreadProcessId(h,out pid);
        // The native window class identifies the pet; allow renamed builds only in its install directory.
        try{using(var p=Process.GetProcessById((int)pid))return String.Equals(Path.GetDirectoryName(p.MainModule.FileName),Path.GetDirectoryName(petExe),StringComparison.OrdinalIgnoreCase)?h:IntPtr.Zero;}
        catch(ArgumentException){return IntPtr.Zero;}catch(InvalidOperationException){return IntPtr.Zero;}catch(System.ComponentModel.Win32Exception){return IntPtr.Zero;}
    }
    private static bool CodexRunning()
    {
        foreach(var p in Process.GetProcessesByName("ChatGPT"))using(p)try
        {if(p.MainModule.FileName.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0)return true;}
        catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}
        return false;
    }
    private static bool QoderRunning()
    {
        foreach(string name in new[]{"Qoder CN","Qoder"})
            foreach(var p in Process.GetProcessesByName(name))using(p)try
            {if(p.MainModule.FileName.IndexOf("Qoder",StringComparison.OrdinalIgnoreCase)>=0)return true;}
            catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}
        return false;
    }
    private void Listen()
    {
        while(!disposed)try
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
                    if(!disposed)dispatcher.BeginInvoke((Action)(()=>{if(data!=null && Wire.Str(data,"kind",30)=="hook")work.Apply(data,DateTime.UtcNow);Tick();}));
                }
            }
        }
        catch(ObjectDisposedException){if(disposed)return;}
        catch(IOException){if(disposed)return;}
        catch(ArgumentException){}
        catch(InvalidOperationException){if(disposed)return;}
        finally{listener=null;}
    }
    private void Tick()
    {
        if(disposed)return;
        try
        {
            if(stop.WaitOne(0)){ExitThread();return;}
            DateTime now=DateTime.UtcNow;
            bool hostRunning=codexRunning || qoderRunning;
            if(now-lastCheck>TimeSpan.FromSeconds(1))
            {
                lastCheck=now;codexRunning=CodexRunning();qoderRunning=QoderRunning();pet=FindPet();
                hostRunning=codexRunning || qoderRunning;
                if(previousHostRunning && !hostRunning)work.Items.Clear();
                previousHostRunning=hostRunning;
            }
            var rows=work.Visible(now);bool petVisible=pet!=IntPtr.Zero && Native.IsWindowVisible(pet);
            if(hostRunning && !quotaPending && now-lastQuotaCheck>TimeSpan.FromSeconds(20))
            {
                quotaPending=true;lastQuotaCheck=now;
                Task.Run(()=>
                {
                    QuotaSnapshot next=null;try{next=QuotaReader.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex","sessions"));}catch(IOException){}catch(UnauthorizedAccessException){}
                    try{if(!disposed)dispatcher.BeginInvoke((Action)(()=>{if(next!=null && (quota==null || next.observedUtc>=quota.observedUtc))quota=next;quotaPending=false;}));}catch(InvalidOperationException){}
                });
            }
            if(hostRunning && !dailyPending && now-lastDailyCheck>TimeSpan.FromSeconds(2))
            {
                dailyPending=true;lastDailyCheck=now;
                Task.Run(()=>
                {
                    DailyTokenSnapshot next=null;var metrics=new Dictionary<string,RequestMetrics>();
                    try{next=dailyReader.Read(DateTime.UtcNow);metrics=dailyReader.Metrics();}catch(IOException){}catch(UnauthorizedAccessException){}
                    try{if(!disposed)dispatcher.BeginInvoke((Action)(()=>{localDaily=next;requestMetrics=metrics;dailyPending=false;}));}catch(InvalidOperationException){}
                });
            }
            if(hostRunning && !officialPending && now-lastOfficialCheck>TimeSpan.FromMinutes(10))
            {
                officialPending=true;lastOfficialCheck=now;
                Task.Run(()=>
                {
                    string result;var next=OfficialDailyTokenReader.Read(DateTime.UtcNow,out result);
                    try{if(!disposed)dispatcher.BeginInvoke((Action)(()=>{if(next!=null)officialDaily=next;officialDailyStatus=result;officialPending=false;}));}catch(InvalidOperationException){}
                });
            }
            var codexItem=WorkState.SelectSource(rows,"codex",codexMetricsSession);
            var qoderItem=WorkState.SelectSource(rows,"qoder",qoderUsageSession);
            codexMetricsSession=codexItem==null?"":codexItem.session;qoderUsageSession=qoderItem==null?"":qoderItem.session;
            string qoderSession=qoderUsageSession;
            if(!qoderUsagePending && now-lastQoderUsageCheck>TimeSpan.FromSeconds(2))
            {
                qoderUsagePending=true;lastQoderUsageCheck=now;string session=qoderSession;
                Task.Run(()=>
                {
                    QoderUsageSnapshot next=null;
                    try{next=qoderReader.Read(session,DateTime.UtcNow);}catch(Exception){}
                    try{if(!disposed)dispatcher.BeginInvoke((Action)(()=>{if(next!=null)qoderUsage=next;qoderUsagePending=false;}));}catch(InvalidOperationException){}
                });
            }
            string today=now.ToLocalTime().ToString("yyyy-MM-dd");
            var daily=officialDaily!=null && officialDaily.date==today && now-officialDaily.observedUtc<TimeSpan.FromMinutes(15)?officialDaily:
                localDaily!=null && localDaily.date==today?localDaily:null;
            RequestMetrics primaryMetrics=null;if(codexItem!=null)requestMetrics.TryGetValue(RequestMetrics.Key(codexItem.session,codexItem.turn),out primaryMetrics);
            var displayedQoder=qoderUsage;
            if(displayedQoder!=null && displayedQoder.session!=qoderSession)
                displayedQoder=new QoderUsageSnapshot{dailyCredits=qoderUsage.dailyCredits,observedUtc=qoderUsage.observedUtc,session=qoderSession};
            DateTime metricsClock=new DateTime(now.Ticks-now.Ticks%TimeSpan.TicksPerSecond,DateTimeKind.Utc);
            byte petAlpha=Native.PetAlpha(pet);
            if(hostRunning && petVisible && showBubble && rows.Count>0)
            {
                bubble.Quota=quota;bubble.Daily=daily;bubble.Qoder=displayedQoder;bubble.Metrics=primaryMetrics;bubble.UpdateRows(rows,now);Native.Rect r;
                if(Native.GetWindowRect(pet,out r))
                {var rect=Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);var area=Screen.FromRectangle(rect).WorkingArea;bubble.SetViewport(area.Size);bubble.Present(Native.Position(rect,area,bubble.Size),petAlpha);}
            }
            else if(bubble.Visible)bubble.Hide();
            string status=Wire.Json().Serialize(new{version=3,mode="status-bubble-only",codexRunning=codexRunning,qoderRunning=qoderRunning,
                keyStatsEnabled=keyStats.Enabled,keyStatsHook=keyStats.HookReady,keyStatsToday=keyStats.TodayTotal,keyStatsError=keyStats.LastError,
                ordinaryPetFound=pet!=IntPtr.Zero,ordinaryPetVisible=petVisible,bubbleVisible=bubble.Visible,
                state=rows.Count>0?rows[0].state:"idle",activeConversations=work.Items.Values.Count(x=>x.Active),petOpacity=petAlpha,bubbleOpacity=bubble.PresentedAlpha,
                receivedSessions=work.Items.Count,keyboardOverlayEnabled=false,petAnimationControl=false,bridgeTrayIcon=false,showBubble=showBubble,
                bubbleZoom=bubble.Zoom,bubbleWidth=bubble.Width,bubbleHeight=bubble.Height,bubbleInteracting=bubble.Interacting,
                lastHookUtc=work.Items.Count==0?(DateTime?)null:work.Items.Values.Max(x=>x.updated),quota=quota,dailyTokens=daily,officialDailyStatus=officialDailyStatus,qoderUsage=displayedQoder,
                codexMetricsSession=codexMetricsSession,qoderUsageSession=qoderUsageSession,backdrop=bubble.BackdropMode,
                requestMetrics=primaryMetrics,outputTokensPerSecond=primaryMetrics==null?(double?)null:primaryMetrics.Speed(metricsClock),speedScope="turn-wall-time-including-tools-and-waiting",
                conversations=work.Items.Values.OrderByDescending(x=>x.updated).Select(x=>new{session=x.session,turn=x.turn,workspace=x.workspace,source=x.source,state=x.state,lastEvent=x.lastEvent,updatedUtc=x.updated,visibleInBubble=rows.Contains(x)}).ToArray()});
            if(status!=lastStatus){File.WriteAllText(Path.Combine(Wire.Root,"status.json"),status,new UTF8Encoding(false));lastStatus=status;}
            SyncStartup(now);
        }
        catch(Exception e)
        {string error=e.GetType().Name+": "+e.Message;if(error!=lastError){lastError=error;File.AppendAllText(Path.Combine(Wire.Root,"bubble-errors.log"),DateTimeOffset.Now+" "+error+Environment.NewLine);}}
    }
    private void SyncStartup(DateTime now)
    {
        if(now-lastStartup<TimeSpan.FromSeconds(30))return;lastStartup=now;
        string file=Path.Combine(dataDir,"settings.json");if(!File.Exists(file))return;
        var settings=Wire.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));object v;
        if(!settings.TryGetValue("autostart",out v)||!(v is bool))return;
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true))
        {
            if(key==null)return;string wanted="\""+Path.Combine(Wire.Root,"MinecraftCodexBridge.exe")+"\"";
            if((bool)v){if(Convert.ToString(key.GetValue("MinecraftCodexBridge"))!=wanted)key.SetValue("MinecraftCodexBridge",wanted);}
            else if(Convert.ToString(key.GetValue("MinecraftCodexBridge"))==wanted)key.DeleteValue("MinecraftCodexBridge",false);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if(!disposed){disposed=true;timer.Stop();var pipe=listener;if(pipe!=null)pipe.Dispose();bubble.Dispose();keyStats.Dispose();dispatcher.Dispose();timer.Dispose();stop.Dispose();}
        base.Dispose(disposing);
    }
}

internal static class Program
{
    [STAThread]private static void Main(string[] args)
    {
        if(args.Length==1 && args[0]=="--exit")
        {try{using(var e=EventWaitHandle.OpenExisting(Wire.Stop))e.Set();}catch(WaitHandleCannotBeOpenedException){}return;}
        bool first;using(var mutex=new Mutex(true,Wire.Pipe,out first))
        {
            if(!first){if(args.Contains("--appearance"))TrayBridgeControl.RequestAppearance();else if(args.Contains("--key-stats"))TrayBridgeControl.RequestKeyStats();return;}Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var bridge=new Bridge(args.Contains("--appearance"),args.Contains("--key-stats")))Application.Run(bridge);
        }
    }
}
