using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Press counts only. No prompt text, key order, clipboard or window content is stored.
// Recent keys live in memory and disappear when the bridge exits.
internal sealed class KeyStatsDay
{
    public string date="";
    public int total;
    public Dictionary<string,int> keys=new Dictionary<string,int>();
    public int[] hours=new int[24];
}
internal sealed class KeyStatsTotals
{
    public string since="";
    public int total;
    public int activeDays;
    public Dictionary<string,int> keys=new Dictionary<string,int>();
    public int[] hours=new int[24];
}
internal sealed class KeyStatsMinute
{
    public string at="";
    public Dictionary<string,int> keys=new Dictionary<string,int>();
}
internal sealed class KeyStatsFile
{
    public int version=1;
    public bool enabled=true;
    public List<KeyStatsDay> days=new List<KeyStatsDay>();
    public KeyStatsTotals allTime=new KeyStatsTotals();
    public Dictionary<string,KeyStatsMinute> minutes=new Dictionary<string,KeyStatsMinute>();
}
internal enum KeyStatsRange { Today, Last5Hours, Week, Month, AllTime }
internal sealed class KeyStatsPeriod
{
    public KeyStatsRange range;
    public string label="";
    public string detail="";
    public int total;
    public int peakHour,peakValue;
    public int activeDays,activeHours;
    public Dictionary<string,int> keys=new Dictionary<string,int>();
    public int[] hours=new int[24];
}
internal sealed class KeyStatsDaySummary
{
    public string date="";
    public int total;
}
internal sealed class KeyStatsSnapshot
{
    public string date="";
    public int total;
    public Dictionary<string,int> keys=new Dictionary<string,int>();
    public int[] hours=new int[24];
    public List<KeyStatsDaySummary> history=new List<KeyStatsDaySummary>();
    public string[] recent=new string[0];
    public bool enabled,hookReady;
    public string lastError="";
}
internal sealed class KeyStatsStore
{
    private readonly object gate=new object();
    private readonly string file;
    private KeyStatsFile data;
    private bool dirty;
    private string lastMinuteKey="";

    internal KeyStatsStore(string file)
    {
        this.file=file;data=new KeyStatsFile();
        try
        {
            if(File.Exists(file))
            {
                var loaded=Wire.Json().Deserialize<KeyStatsFile>(File.ReadAllText(file));
                if(loaded!=null && loaded.version==1)
                {
                    data=loaded;
                    if(data.days==null)data.days=new List<KeyStatsDay>();
                }
            }
        }
        catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(FormatException){}catch(OverflowException){}
        Cleanup();
    }
    private void Cleanup()
    {
        DateTime cutoff=DateTime.Today.AddDays(-60);
        data.days=data.days.Where(d=>d!=null && !String.IsNullOrEmpty(d.date)).Where(d=>{
            DateTime parsed;
            return DateTime.TryParseExact(d.date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed) && parsed.Date>=cutoff;
        }).OrderBy(d=>d.date).ToList();
        foreach(var day in data.days)
        {
            if(day.keys==null)day.keys=new Dictionary<string,int>();
            if(day.hours==null || day.hours.Length!=24)day.hours=new int[24];
        }
        if(data.minutes==null)data.minutes=new Dictionary<string,KeyStatsMinute>();
        DateTime minuteCutoff=DateTime.Now.AddHours(-6);
        var validMinutes=new Dictionary<string,KeyStatsMinute>();
        foreach(var pair in data.minutes)
        {
            DateTime at;
            if(pair.Value==null || !DateTime.TryParseExact(pair.Key,"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out at) || at<minuteCutoff)continue;
            if(pair.Value.keys==null)pair.Value.keys=new Dictionary<string,int>();
            pair.Value.at=pair.Key;validMinutes[pair.Key]=pair.Value;
        }
        data.minutes=validMinutes;
        if(data.allTime==null || (data.allTime.total==0 && String.IsNullOrEmpty(data.allTime.since)))
        {
            var totals=new KeyStatsTotals();
            foreach(var day in data.days)
            {
                if(day.total<=0)continue;
                totals.total+=day.total;totals.activeDays++;
                foreach(var pair in day.keys){int count;totals.keys.TryGetValue(pair.Key,out count);totals.keys[pair.Key]=count+pair.Value;}
                for(int i=0;i<24;i++)totals.hours[i]+=day.hours[i];
                if(String.IsNullOrEmpty(totals.since))totals.since=day.date;
            }
            data.allTime=totals;
        }
        else
        {
            if(data.allTime.keys==null)data.allTime.keys=new Dictionary<string,int>();
            if(data.allTime.hours==null || data.allTime.hours.Length!=24)data.allTime.hours=new int[24];
            if(String.IsNullOrEmpty(data.allTime.since))data.allTime.since=data.days.Count>0?data.days[0].date:DateTime.Today.ToString("yyyy-MM-dd");
        }
        dirty=true;
    }
    private void PruneMinutes(DateTime cutoff)
    {
        var stale=data.minutes.Where(pair=>{
            DateTime at;
            return !DateTime.TryParseExact(pair.Key,"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out at) || at<cutoff;
        }).Select(pair=>pair.Key).ToList();
        foreach(string key in stale)data.minutes.Remove(key);
    }
    internal bool Enabled{get{lock(gate)return data.enabled;}}
    internal void SetEnabled(bool enabled)
    {
        lock(gate){data.enabled=enabled;dirty=true;SaveLocked();}
    }
    internal int TodayTotal()
    {
        lock(gate)
        {
            string key=DateTime.Today.ToString("yyyy-MM-dd");
            var day=data.days.FirstOrDefault(d=>d.date==key);
            return day==null?0:day.total;
        }
    }
    private KeyStatsDay DayAt(DateTime date)
    {
        string key=date.ToString("yyyy-MM-dd");
        return data.days.FirstOrDefault(d=>d.date==key);
    }
    private static DateTime StartOfWeek(DateTime date){return date.Date.AddDays(-(((int)date.DayOfWeek+6)%7));}
    internal KeyStatsPeriod Period(KeyStatsRange range,DateTime now)
    {
        lock(gate)
        {
            var period=new KeyStatsPeriod{range=range};
            DateTime start,to;
            switch(range)
            {
                case KeyStatsRange.Today:
                    period.label="今日";period.detail=now.ToString("MM-dd");
                    MergeDay(period,DayAt(now.Date));
                    period.activeDays=period.total>0?1:0;
                    break;
                case KeyStatsRange.Last5Hours:
                    period.label="近5小时";start=now.AddHours(-5);
                    start=new DateTime(start.Year,start.Month,start.Day,start.Hour,start.Minute,0);
                    period.detail=start.ToString("HH:mm")+" – "+now.ToString("HH:mm");
                    MergeMinutes(period,start,now);
                    break;
                case KeyStatsRange.Week:
                    period.label="本周";start=StartOfWeek(now);to=now.Date;
                    period.detail=start.ToString("MM-dd")+" – "+to.ToString("MM-dd");
                    MergeDays(period,start,to);
                    break;
                case KeyStatsRange.Month:
                    period.label="本月";start=new DateTime(now.Year,now.Month,1);to=now.Date;
                    period.detail=start.ToString("MM-dd")+" – "+to.ToString("MM-dd");
                    MergeDays(period,start,to);
                    break;
                case KeyStatsRange.AllTime:
                    period.label="累计";
                    period.total=data.allTime.total;
                    period.activeDays=data.allTime.activeDays;
                    period.keys=new Dictionary<string,int>(data.allTime.keys);
                    period.hours=(int[])data.allTime.hours.Clone();
                    period.detail=String.IsNullOrEmpty(data.allTime.since)?"":"自 "+data.allTime.since+" 起";
                    break;
            }
            for(int i=0;i<24;i++)
            {
                if(period.hours[i]>0)period.activeHours++;
                if(period.hours[i]>period.peakValue){period.peakValue=period.hours[i];period.peakHour=i;}
            }
            return period;
        }
    }
    private static void MergeDay(KeyStatsPeriod period,KeyStatsDay day)
    {
        if(day==null)return;
        period.total+=day.total;
        foreach(var pair in day.keys){int count;period.keys.TryGetValue(pair.Key,out count);period.keys[pair.Key]=count+pair.Value;}
        for(int i=0;i<24;i++)period.hours[i]+=day.hours[i];
    }
    private void MergeDays(KeyStatsPeriod period,DateTime from,DateTime to)
    {
        foreach(var day in data.days)
        {
            DateTime parsed;
            if(!DateTime.TryParseExact(day.date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))continue;
            if(parsed<from || parsed>to)continue;
            if(day.total>0)period.activeDays++;
            MergeDay(period,day);
        }
    }
    private void MergeMinutes(KeyStatsPeriod period,DateTime from,DateTime to)
    {
        var days=new HashSet<string>();
        foreach(var pair in data.minutes)
        {
            DateTime at;
            if(!DateTime.TryParseExact(pair.Key,"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out at))continue;
            if(at<from || at>to || pair.Value==null)continue;
            int sum=0;
            foreach(var key in pair.Value.keys){int count;period.keys.TryGetValue(key.Key,out count);period.keys[key.Key]=count+key.Value;sum+=key.Value;}
            period.total+=sum;period.hours[at.Hour]+=sum;
            if(sum>0)days.Add(at.ToString("yyyy-MM-dd"));
        }
        period.activeDays=days.Count;
    }
    internal void Count(string id,DateTime local)
    {
        lock(gate)
        {
            if(String.IsNullOrEmpty(id))return;
            string key=local.ToString("yyyy-MM-dd");
            var day=data.days.FirstOrDefault(d=>d.date==key);
            bool newDay=day==null;
            if(newDay){day=new KeyStatsDay{date=key};data.days.Add(day);Cleanup();day=data.days.First(d=>d.date==key);}
            day.total++;
            int count;day.keys.TryGetValue(id,out count);day.keys[id]=count+1;
            if(local.Hour>=0 && local.Hour<24)day.hours[local.Hour]++;
            if(data.allTime==null)data.allTime=new KeyStatsTotals();
            if(String.IsNullOrEmpty(data.allTime.since))data.allTime.since=key;
            data.allTime.total++;
            if(newDay)data.allTime.activeDays++;
            data.allTime.keys.TryGetValue(id,out count);data.allTime.keys[id]=count+1;
            if(local.Hour>=0 && local.Hour<24)data.allTime.hours[local.Hour]++;
            string minuteKey=local.ToString("yyyy-MM-dd HH:mm");
            KeyStatsMinute minute;
            if(!data.minutes.TryGetValue(minuteKey,out minute) || minute==null){minute=new KeyStatsMinute{at=minuteKey};data.minutes[minuteKey]=minute;}
            minute.keys.TryGetValue(id,out count);minute.keys[id]=count+1;
            if(minuteKey!=lastMinuteKey){lastMinuteKey=minuteKey;PruneMinutes(local.AddHours(-6));}
            dirty=true;
        }
    }
    internal KeyStatsSnapshot Snapshot(DateTime now)
    {
        lock(gate)
        {
            var snapshot=new KeyStatsSnapshot{date=now.ToString("yyyy-MM-dd"),enabled=data.enabled};
            var today=data.days.FirstOrDefault(d=>d.date==snapshot.date);
            if(today!=null)
            {
                snapshot.total=today.total;
                snapshot.keys=new Dictionary<string,int>(today.keys);
                snapshot.hours=(int[])today.hours.Clone();
            }
            for(int i=6;i>=0;i--)
            {
                DateTime date=now.Date.AddDays(-i);
                string key=date.ToString("yyyy-MM-dd");
                var day=data.days.FirstOrDefault(d=>d.date==key);
                snapshot.history.Add(new KeyStatsDaySummary{date=key,total=day==null?0:day.total});
            }
            return snapshot;
        }
    }
    internal void Clear()
    {
        lock(gate)
        {
            data.days.Clear();data.minutes.Clear();
            data.allTime=new KeyStatsTotals{since=DateTime.Today.ToString("yyyy-MM-dd")};
            lastMinuteKey="";dirty=true;SaveLocked();
        }
    }
    internal void Flush(){lock(gate){if(dirty)SaveLocked();}}
    private void SaveLocked()
    {
        try
        {
            string directory=Path.GetDirectoryName(file);
            if(!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))Directory.CreateDirectory(directory);
            string temporary=file+".tmp";
            File.WriteAllText(temporary,Wire.Json().Serialize(data),new UTF8Encoding(false));
            if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);
            dirty=false;
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
}

internal sealed class KeyInfo
{
    internal string Id="",Label="",Short="";
}
internal sealed class KeySpec
{
    internal string Id="";
    internal float Width=1f,Gap=.25f;
}
internal sealed class KeyRow
{
    internal float Offset;
    internal List<KeySpec> Keys=new List<KeySpec>();
}
internal static class KeyMap
{
    private static readonly Dictionary<int,KeyInfo> byVk=new Dictionary<int,KeyInfo>();
    private static readonly Dictionary<string,KeyInfo> byId=new Dictionary<string,KeyInfo>();
    private static readonly List<KeyRow> rows=new List<KeyRow>();
    internal static float TotalUnits=18.5f;

    static KeyMap()
    {
        Add(Keys.LButton,"MouseLeft","鼠标左键","左键");
        Add(Keys.RButton,"MouseRight","鼠标右键","右键");
        Add(Keys.MButton,"MouseMiddle","鼠标中键","中键");
        Add(Keys.XButton1,"MouseSide1","鼠标侧键 1","侧键1");
        Add(Keys.XButton2,"MouseSide2","鼠标侧键 2","侧键2");
        byId["MouseWheelUp"]=new KeyInfo{Id="MouseWheelUp",Label="滚轮向上",Short="上滚"};
        byId["MouseWheelDown"]=new KeyInfo{Id="MouseWheelDown",Label="滚轮向下",Short="下滚"};
        for(int i=0;i<26;i++)
        {
            char letter=(char)('A'+i);
            Add((Keys)((int)Keys.A+i),letter.ToString(),letter.ToString(),letter.ToString());
        }
        for(int i=0;i<10;i++)
        {
            string digit=i.ToString();
            Add((Keys)((int)Keys.D0+i),digit,digit,digit);
            Add((Keys)((int)Keys.NumPad0+i),"Num"+digit,"Num "+digit,"N"+digit);
        }
        for(int i=1;i<=24;i++)Add((Keys)((int)Keys.F1+i-1),"F"+i,"F"+i,"F"+i);
        Add(Keys.Oemtilde,"`","`","`");
        Add(Keys.OemMinus,"-","-","-");
        Add(Keys.Oemplus,"=","=","=");
        Add(Keys.OemOpenBrackets,"[","[","[");
        Add(Keys.OemCloseBrackets,"]","]","]");
        Add(Keys.OemPipe,"\\","\\","\\");
        Add(Keys.OemSemicolon,";",";",";");
        Add(Keys.OemQuotes,"'","'","'");
        Add(Keys.Oemcomma,",",",",",");
        Add(Keys.OemPeriod,".",".",".");
        Add(Keys.OemQuestion,"/","/","/");
        Add(Keys.Escape,"Esc","Esc","Esc");
        Add(Keys.Back,"Backspace","Backspace","Bksp");
        Add(Keys.Tab,"Tab","Tab","Tab");
        Add(Keys.Return,"Enter","Enter","Enter");
        Add(Keys.Space,"Space","Space","Space");
        Add(Keys.Capital,"Caps","Caps Lock","Caps");
        Add(Keys.LControlKey,"LCtrl","Left Ctrl","LCtrl");
        Add(Keys.RControlKey,"RCtrl","Right Ctrl","RCtrl");
        Add(Keys.LShiftKey,"LShift","Left Shift","LShft");
        Add(Keys.RShiftKey,"RShift","Right Shift","RShft");
        Add(Keys.LMenu,"LAlt","Left Alt","LAlt");
        Add(Keys.RMenu,"RAlt","Right Alt","RAlt");
        Add(Keys.LWin,"LWin","Left Win","LWin");
        Add(Keys.RWin,"RWin","Right Win","RWin");
        Add(Keys.Apps,"Menu","Menu","Menu");
        Add(Keys.NumLock,"NumLock","Num Lock","NumLk");
        Add(Keys.Scroll,"ScrollLock","Scroll Lock","ScrLk");
        Add(Keys.PrintScreen,"PrintScreen","Print Screen","PrtSc");
        Add(Keys.Pause,"Pause","Pause","Pause");
        Add(Keys.Insert,"Insert","Insert","Ins");
        Add(Keys.Delete,"Delete","Delete","Del");
        Add(Keys.Home,"Home","Home","Home");
        Add(Keys.End,"End","End","End");
        Add(Keys.PageUp,"PageUp","Page Up","PgUp");
        Add(Keys.PageDown,"PageDown","Page Down","PgDn");
        Add(Keys.Up,"Up","Up","↑");
        Add(Keys.Down,"Down","Down","↓");
        Add(Keys.Left,"Left","Left","←");
        Add(Keys.Right,"Right","Right","→");
        Add(Keys.Add,"Num+","Numpad +","N+");
        Add(Keys.Subtract,"Num-","Numpad -","N-");
        Add(Keys.Multiply,"Num*","Numpad *","N*");
        Add(Keys.Divide,"Num/","Numpad /","N/");
        Add(Keys.Decimal,"Num.","Numpad .","N.");
        BuildRows();
    }
    private static void Add(Keys key,string id,string label,string shortName)
    {
        var info=new KeyInfo{Id=id,Label=label,Short=shortName};
        byVk[(int)key]=info;byId[id]=info;
    }
    internal static KeyInfo Info(uint vk)
    {
        KeyInfo info;
        if(byVk.TryGetValue((int)vk,out info))return info;
        string name=((Keys)vk).ToString();
        if(name=="None" || String.IsNullOrEmpty(name))name="VK_"+vk.ToString("X2");
        return new KeyInfo{Id=name,Label=name,Short=name};
    }
    internal static string Id(uint vk){return Info(vk).Id;}
    internal static string Label(string id)
    {
        KeyInfo info;return byId.TryGetValue(id,out info)?info.Label:id;
    }
    internal static string Short(string id)
    {
        KeyInfo info;return byId.TryGetValue(id,out info)?info.Short:id;
    }
    internal static List<KeyRow> Rows{get{return rows;}}
    private static KeySpec Spec(string id,float width,float gap){return new KeySpec{Id=id,Width=width,Gap=gap};}
    private static void AddRow(float offset,params KeySpec[] keys)
    {
        var row=new KeyRow{Offset=offset};row.Keys.AddRange(keys);rows.Add(row);
    }
    private static void BuildRows()
    {
        var frow=new List<KeySpec>();frow.Add(Spec("Esc",1.25f,.25f));
        for(int i=1;i<=12;i++)frow.Add(Spec("F"+i,1f,i==12?0:.25f));
        AddRow(1.125f,frow.ToArray());

        AddRow(0f,Spec("`",1,.25f),Spec("1",1,.25f),Spec("2",1,.25f),Spec("3",1,.25f),Spec("4",1,.25f),Spec("5",1,.25f),Spec("6",1,.25f),Spec("7",1,.25f),Spec("8",1,.25f),Spec("9",1,.25f),Spec("0",1,.25f),Spec("-",1,.25f),Spec("=",1,.25f),Spec("Backspace",2,0));

        AddRow(.25f,Spec("Tab",1.5f,.25f),Spec("Q",1,.25f),Spec("W",1,.25f),Spec("E",1,.25f),Spec("R",1,.25f),Spec("T",1,.25f),Spec("Y",1,.25f),Spec("U",1,.25f),Spec("I",1,.25f),Spec("O",1,.25f),Spec("P",1,.25f),Spec("[",1,.25f),Spec("]",1,.25f),Spec("\\",1,0));

        AddRow(.125f,Spec("Caps",1.75f,.25f),Spec("A",1,.25f),Spec("S",1,.25f),Spec("D",1,.25f),Spec("F",1,.25f),Spec("G",1,.25f),Spec("H",1,.25f),Spec("J",1,.25f),Spec("K",1,.25f),Spec("L",1,.25f),Spec(";",1,.25f),Spec("'",1,.25f),Spec("Enter",2.25f,0));

        AddRow(.25f,Spec("LShift",2.25f,.25f),Spec("Z",1,.25f),Spec("X",1,.25f),Spec("C",1,.25f),Spec("V",1,.25f),Spec("B",1,.25f),Spec("N",1,.25f),Spec("M",1,.25f),Spec(",",1,.25f),Spec(".",1,.25f),Spec("/",1,.25f),Spec("RShift",2.75f,0));

        AddRow(.875f,Spec("LCtrl",1.25f,.25f),Spec("LWin",1.25f,.25f),Spec("LAlt",1.25f,.25f),Spec("Space",6.25f,.25f),Spec("RAlt",1.25f,.25f),Spec("RWin",1.25f,.25f),Spec("Menu",1.25f,.25f),Spec("RCtrl",1.25f,0));
    }
}

internal sealed class LowLevelKeyboardHook:IDisposable
{
    private const int WH_KEYBOARD_LL=13;
    private delegate IntPtr HookProc(int nCode,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT{public uint vkCode,scanCode,flags,time;public IntPtr extra;}
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int hook,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string module);
    [DllImport("user32.dll")]internal static extern short GetAsyncKeyState(int key);
    private readonly HookProc proc;
    private readonly Action<uint,uint,bool> down;
    private readonly Action<uint> up;
    private IntPtr handle;
    internal string LastError="";
    internal bool Installed{get{return handle!=IntPtr.Zero;}}
    internal LowLevelKeyboardHook(Action<uint,uint,bool> down,Action<uint> up)
    {
        this.down=down;this.up=up;proc=OnHook;
    }
    internal bool Install()
    {
        if(handle!=IntPtr.Zero)return true;
        handle=SetWindowsHookEx(WH_KEYBOARD_LL,proc,GetModuleHandle(null),0);
        if(handle==IntPtr.Zero){LastError="SetWindowsHookEx 失败，错误码 "+Marshal.GetLastWin32Error();return false;}
        LastError="";return true;
    }
    internal void Uninstall()
    {
        if(handle!=IntPtr.Zero){UnhookWindowsHookEx(handle);handle=IntPtr.Zero;}
    }
    private IntPtr OnHook(int code,IntPtr wParam,IntPtr lParam)
    {
        if(code>=0)
        {
            try
            {
                var info=(KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam,typeof(KBDLLHOOKSTRUCT));
                int message=wParam.ToInt32();
                bool injected=(info.flags&0x10)!=0;
                if(message==0x100 || message==0x104)down(info.vkCode,info.scanCode,injected);
                else if(message==0x101 || message==0x105)up(info.vkCode);
            }
            catch(Exception){}
        }
        return CallNextHookEx(handle,code,wParam,lParam);
    }
    public void Dispose(){Uninstall();}
}

internal sealed class LowLevelMouseHook:IDisposable
{
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInfo{public int x,y;public uint mouseData,flags,time;public IntPtr extra;}
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int hook,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string module);
    private readonly HookProc proc;
    private readonly Action<string,DateTime> click;
    private IntPtr handle;
    internal string LastError="";
    internal bool Installed{get{return handle!=IntPtr.Zero;}}
    internal LowLevelMouseHook(Action<string,DateTime> click){this.click=click;proc=OnHook;}
    internal bool Install()
    {
        if(Installed)return true;
        handle=SetWindowsHookEx(14,proc,GetModuleHandle(null),0);
        if(!Installed){LastError="鼠标钩子启动失败，错误码 "+Marshal.GetLastWin32Error();return false;}
        LastError="";return true;
    }
    internal void Uninstall(){if(Installed){UnhookWindowsHookEx(handle);handle=IntPtr.Zero;}}
    private IntPtr OnHook(int code,IntPtr message,IntPtr data)
    {
        int m=message.ToInt32();
        if(code>=0 && (m==0x201 || m==0x204 || m==0x207 || m==0x20B || m==0x20A))
        {
            try
            {
                var info=(MouseInfo)Marshal.PtrToStructure(data,typeof(MouseInfo));
                if((info.flags&1)==0)
                {
                    string id=null;
                    if(m==0x201)id="MouseLeft";
                    else if(m==0x204)id="MouseRight";
                    else if(m==0x207)id="MouseMiddle";
                    else if(m==0x20A)
                    {
                        short delta=unchecked((short)(info.mouseData>>16));
                        if(delta>0)id="MouseWheelUp";
                        else if(delta<0)id="MouseWheelDown";
                    }
                    else if((info.mouseData>>16)==1)id="MouseSide1";
                    else if((info.mouseData>>16)==2)id="MouseSide2";
                    if(id!=null)click(id,DateTime.Now);
                }
            }
            catch(Exception){}
        }
        return CallNextHookEx(handle,code,message,data);
    }
    public void Dispose(){Uninstall();}
}

internal sealed class KeyStatsService:IDisposable
{
    internal static KeyStatsService Current;
    private readonly KeyStatsStore store;
    private readonly LowLevelKeyboardHook hook;
    private readonly LowLevelMouseHook mouseHook;
    private readonly ConcurrentQueue<KeyValuePair<string,DateTime>> mouseClicks=new ConcurrentQueue<KeyValuePair<string,DateTime>>();
    private readonly System.Threading.Timer mouseTimer;
    private readonly System.Threading.Timer flush;
    private readonly object gate=new object();
    private readonly HashSet<uint> held=new HashSet<uint>();
    private readonly List<string> recent=new List<string>();
    private const int RecentLimit=16;
    internal bool Enabled{get{return store.Enabled && hook.Installed && mouseHook.Installed;}}
    internal bool HookReady{get{return hook.Installed && mouseHook.Installed;}}
    internal string LastError{get{return !String.IsNullOrEmpty(mouseHook.LastError)?mouseHook.LastError:hook.LastError;}}
    internal int TodayTotal{get{return store.TodayTotal();}}
    internal KeyStatsService(string file)
    {
        store=new KeyStatsStore(file);
        hook=new LowLevelKeyboardHook(OnDown,OnUp);
        mouseHook=new LowLevelMouseHook((id,at)=>mouseClicks.Enqueue(new KeyValuePair<string,DateTime>(id,at)));
        if(store.Enabled && hook.Install() && !mouseHook.Install())hook.Uninstall();
        mouseTimer=new System.Threading.Timer(_=>{try{DrainMouse();}catch(Exception){}},null,100,100);
        flush=new System.Threading.Timer(_=>{try{store.Flush();}catch(Exception){}},null,5000,5000);
        Current=this;
    }
    private void OnDown(uint vk,uint scan,bool injected)
    {
        if(injected || !store.Enabled)return;
        string id=null;
        lock(gate)
        {
            bool physicallyDown=(LowLevelKeyboardHook.GetAsyncKeyState((int)vk)&0x8000)!=0;
            if(held.Contains(vk) && physicallyDown)return;
            held.Remove(vk);held.Add(vk);
            id=KeyMap.Id(vk);
            recent.Add(id);if(recent.Count>RecentLimit)recent.RemoveAt(0);
        }
        store.Count(id,DateTime.Now);
    }
    private void OnUp(uint vk){lock(gate)held.Remove(vk);}
    private void DrainMouse()
    {
        lock(gate)
        {
            KeyValuePair<string,DateTime> item;
            while(mouseClicks.TryDequeue(out item))
            {
                recent.Add(item.Key);if(recent.Count>RecentLimit)recent.RemoveAt(0);
                store.Count(item.Key,item.Value);
            }
        }
    }
    internal KeyStatsSnapshot Snapshot()
    {
        var snapshot=store.Snapshot(DateTime.Now);
        lock(gate)snapshot.recent=recent.ToArray();
        snapshot.hookReady=HookReady;snapshot.enabled=Enabled;snapshot.lastError=LastError;
        return snapshot;
    }
    internal KeyStatsPeriod Period(KeyStatsRange range){return store.Period(range,DateTime.Now);}
    internal bool SetEnabled(bool enabled)
    {
        if(enabled)
        {
            if(!hook.Install()){store.SetEnabled(false);return false;}
            if(!mouseHook.Install()){hook.Uninstall();store.SetEnabled(false);return false;}
            store.SetEnabled(true);return true;
        }
        mouseHook.Uninstall();hook.Uninstall();DrainMouse();store.SetEnabled(false);return true;
    }
    internal void Clear()
    {
        lock(gate){DrainMouse();store.Clear();recent.Clear();}
    }
    public void Dispose()
    {
        try{hook.Uninstall();}catch(Exception){}
        try{mouseHook.Uninstall();}catch(Exception){}
        try{mouseTimer.Dispose();}catch(Exception){}
        DrainMouse();
        try{flush.Dispose();}catch(Exception){}
        store.Flush();if(Current==this)Current=null;
    }
}

internal sealed class KeyStatsForm:Form
{
    private const int PageWidth=980,PageHeight=790;
    private static readonly Color Paper=Color.FromArgb(241,240,233);
    private static readonly Color Card=Color.FromArgb(252,251,246);
    private static readonly Color Edge=Color.FromArgb(218,220,208);
    private static readonly Color Ink=Color.FromArgb(27,46,39);
    private static readonly Color Muted=Color.FromArgb(77,93,84);
    private static readonly Color Pine=Color.FromArgb(28,64,52);
    private static readonly Color Green=Color.FromArgb(36,106,76);
    private static readonly Color Tint=Color.FromArgb(229,238,226);
    private static readonly Color Sand=Color.FromArgb(227,210,150);
    private static readonly Color Cream=Color.FromArgb(250,251,241);
    private static readonly Color Danger=Color.FromArgb(144,63,46);
    private static readonly string[] RangeNames={"今日","近 5 小时","本周","本月","累计"};
    private static readonly string[] MouseIds={"MouseLeft","MouseRight","MouseMiddle","MouseSide1","MouseSide2","MouseWheelUp","MouseWheelDown"};
    private static KeyStatsForm instance;

    private readonly KeyStatsService service;
    private readonly System.Windows.Forms.Timer timer;
    private readonly Font titleFont=new Font("Microsoft YaHei UI",24,FontStyle.Bold,GraphicsUnit.Pixel);
    private readonly Font textFont=new Font("Microsoft YaHei UI",14,FontStyle.Regular,GraphicsUnit.Pixel);
    private readonly Font sectionFont=new Font("Microsoft YaHei UI",15,FontStyle.Bold,GraphicsUnit.Pixel);
    private readonly Font smallFont=new Font("Microsoft YaHei UI",12,FontStyle.Regular,GraphicsUnit.Pixel);
    private readonly Font keyFont=new Font("Microsoft YaHei UI",11,FontStyle.Regular,GraphicsUnit.Pixel);
    private readonly Font numberFont=new Font("Segoe UI",29,FontStyle.Bold,GraphicsUnit.Pixel);
    private readonly Button[] rangeControls=new Button[5];
    private readonly Button pauseButton;
    private readonly List<HeatPoint> heatPoints=new List<HeatPoint>();
    private HeatPoint hovered;
    private KeyStatsRange range=KeyStatsRange.Today;

    private sealed class HeatPoint
    {
        internal readonly RectangleF Area;
        internal readonly string Name;
        internal readonly int Count;
        internal readonly bool Mouse;
        internal HeatPoint(RectangleF area,string name,int count,bool mouse)
        {
            Area=area;Name=name;Count=count;Mouse=mouse;
        }
    }

    private KeyStatsForm(KeyStatsService service)
    {
        this.service=service;
        Text="Minecraft · 按键统计";
        StartPosition=FormStartPosition.CenterScreen;
        FormBorderStyle=FormBorderStyle.Sizable;
        AutoScaleMode=AutoScaleMode.None;
        BackColor=Paper;
        AutoScroll=true;
        AutoScrollMinSize=new Size(PageWidth,PageHeight);
        Rectangle work=Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize=new Size(Math.Min(PageWidth,Math.Max(1,work.Width-48)),
                            Math.Min(PageHeight,Math.Max(1,work.Height-64)));
        MinimumSize=new Size(Math.Min(520,ClientSize.Width),Math.Min(380,ClientSize.Height));
        DoubleBuffered=true;
        SetStyle(ControlStyles.ResizeRedraw,true);

        for(int i=0;i<rangeControls.Length;i++)
        {
            int index=i;
            var button=MakeButton(RangeNames[i],new Rectangle(30+i*94,109,88,32));
            button.Click+=(s,e)=>SelectRange(index);
            button.PreviewKeyDown+=(s,e)=>{if(e.KeyCode==Keys.Left || e.KeyCode==Keys.Right)e.IsInputKey=true;};
            button.KeyDown+=(s,e)=>
            {
                if(e.KeyCode!=Keys.Left && e.KeyCode!=Keys.Right)return;
                int next=(index+(e.KeyCode==Keys.Left?4:1))%rangeControls.Length;
                SelectRange(next);
                rangeControls[next].Focus();
                e.Handled=true;
            };
            rangeControls[i]=button;
        }
        pauseButton=MakeButton("继续记录",new Rectangle(748,35,106,34));
        pauseButton.Click+=(s,e)=>ToggleRecording();
        var close=MakeButton("关闭",new Rectangle(862,35,72,34));
        close.Click+=(s,e)=>Close();
        CancelButton=close;
        var clear=MakeButton("清空统计",new Rectangle(844,735,102,30));
        clear.ForeColor=Danger;
        clear.Click+=(s,e)=>
        {
            if(MessageBox.Show(this,"要清空键盘和鼠标的历史次数吗？\n清空后无法恢复。","清空统计",MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            service.Clear();
            Invalidate();
        };
        SelectRange(0);
        UpdateRecordingButton(service.Enabled);
        timer=new System.Windows.Forms.Timer{Interval=1000};
        timer.Tick+=(s,e)=>{if(Visible && WindowState!=FormWindowState.Minimized)Invalidate();};
        timer.Start();
    }

    internal static void OpenShared()
    {
        if(KeyStatsService.Current==null)
        {
            MessageBox.Show("按键统计服务未启动。","按键统计",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return;
        }
        if(instance==null || instance.IsDisposed)instance=new KeyStatsForm(KeyStatsService.Current);
        if(!instance.Visible)instance.Show();
        if(instance.WindowState==FormWindowState.Minimized)instance.WindowState=FormWindowState.Normal;
        Native.ShowWindow(instance.Handle,5);
        instance.Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            if(timer!=null)timer.Dispose();
            if(instance==this)instance=null;
            titleFont.Dispose();textFont.Dispose();sectionFont.Dispose();smallFont.Dispose();keyFont.Dispose();numberFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private Button MakeButton(string label,Rectangle bounds)
    {
        var button=new Button{Text=label,AccessibleName=label,Bounds=bounds,Font=textFont,
            FlatStyle=FlatStyle.Flat,BackColor=Card,ForeColor=Ink,TabStop=true,UseVisualStyleBackColor=false};
        button.FlatAppearance.BorderColor=Edge;
        button.FlatAppearance.MouseOverBackColor=Tint;
        button.FlatAppearance.MouseDownBackColor=Blend(Tint,Green,.14);
        Controls.Add(button);
        return button;
    }

    private void SelectRange(int index)
    {
        range=(KeyStatsRange)index;
        for(int i=0;i<rangeControls.Length;i++)
        {
            Button button=rangeControls[i];
            bool selected=i==index;
            button.BackColor=selected?Pine:Card;
            button.ForeColor=selected?Cream:Ink;
            button.FlatAppearance.BorderColor=selected?Pine:Edge;
            button.FlatAppearance.MouseOverBackColor=selected?Green:Tint;
            button.FlatAppearance.MouseDownBackColor=selected?Pine:Blend(Tint,Green,.14);
            button.AccessibleDescription=selected?"当前统计时间范围":"切换统计时间范围";
        }
        hovered=null;
        Invalidate();
    }

    private void ToggleRecording()
    {
        if(!service.SetEnabled(!service.Enabled))
        {
            MessageBox.Show(this,"无法启动记录。\n"+service.LastError,"按键统计",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
        UpdateRecordingButton(service.Enabled);
        Invalidate();
    }

    private void UpdateRecordingButton(bool enabled)
    {
        string label=enabled?"暂停记录":"继续记录";
        if(pauseButton.Text!=label)
        {
            pauseButton.Text=label;
            pauseButton.AccessibleName=label;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point=new Point(e.X-AutoScrollPosition.X,e.Y-AutoScrollPosition.Y);
        HeatPoint next=heatPoints.FirstOrDefault(item=>item.Area.Contains(point));
        if(ReferenceEquals(hovered,next))return;
        hovered=next;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if(hovered==null)return;
        hovered=null;
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs e)
    {
        base.OnScroll(e);
        hovered=null;
        Invalidate();
    }

    private static Color Blend(Color from,Color to,double amount)
    {
        return Color.FromArgb((int)(from.R+(to.R-from.R)*amount),
                              (int)(from.G+(to.G-from.G)*amount),
                              (int)(from.B+(to.B-from.B)*amount));
    }

    private static string Number(long count){return count.ToString("N0",CultureInfo.InvariantCulture);}

    private static double Luminance(Color color)
    {
        Func<byte,double> channel=value=>
        {
            double s=value/255d;
            return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);
        };
        return .2126*channel(color.R)+.7152*channel(color.G)+.0722*channel(color.B);
    }

    private static string Compact(int count)
    {
        if(count>=1000000)return (count/1000000d).ToString("0.#",CultureInfo.InvariantCulture)+"M";
        if(count>=1000)return (count/1000d).ToString("0.#",CultureInfo.InvariantCulture)+"K";
        return Number(count);
    }

    private static void PaintText(Graphics g,string text,Font font,Color color,RectangleF bounds,StringAlignment align=StringAlignment.Near)
    {
        using(var format=new StringFormat{Alignment=align,LineAlignment=StringAlignment.Center,
              Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
        using(var brush=new SolidBrush(color))
            g.DrawString(text,font,brush,bounds,format);
    }

    private static void Fill(Graphics g,Color color,RectangleF rect)
    {
        using(var brush=new SolidBrush(color))g.FillRectangle(brush,rect);
    }

    private static void Line(Graphics g,Color color,float x1,float y1,float x2,float y2)
    {
        using(var pen=new Pen(color))g.DrawLine(pen,x1,y1,x2,y2);
    }

    private static void Panel(Graphics g,RectangleF rect,Color color,Color border,float radius=10)
    {
        float diameter=Math.Min(radius*2,Math.Min(rect.Width,rect.Height));
        using(var path=new GraphicsPath())
        {
            path.AddArc(rect.Left,rect.Top,diameter,diameter,180,90);
            path.AddArc(rect.Right-diameter,rect.Top,diameter,diameter,270,90);
            path.AddArc(rect.Right-diameter,rect.Bottom-diameter,diameter,diameter,0,90);
            path.AddArc(rect.Left,rect.Bottom-diameter,diameter,diameter,90,90);
            path.CloseFigure();
            using(var brush=new SolidBrush(color))g.FillPath(brush,path);
            using(var pen=new Pen(border))g.DrawPath(pen,path);
        }
    }

    private void DrawHeader(Graphics g,KeyStatsSnapshot snap,KeyStatsPeriod period)
    {
        Panel(g,new RectangleF(22,16,936,72),Pine,Pine);
        Fill(g,Sand,new RectangleF(40,36,8,8));
        Fill(g,Color.FromArgb(158,186,151),new RectangleF(48,44,8,8));
        Fill(g,Sand,new RectangleF(40,52,8,8));
        PaintText(g,"按键统计",titleFont,Cream,new RectangleF(68,23,250,37));
        PaintText(g,"键盘与鼠标 · 计数仅保存在本机",smallFont,Color.FromArgb(205,222,204),new RectangleF(69,59,350,18));
        bool failed=!snap.enabled && !String.IsNullOrEmpty(snap.lastError);
        string status=snap.enabled?"正在记录":failed?"记录未启动":"已暂停";
        Panel(g,new RectangleF(632,37,100,30),Color.FromArgb(42,83,67),Color.FromArgb(80,115,94),15);
        Fill(g,snap.enabled?Color.FromArgb(173,214,158):Sand,new RectangleF(644,50,6,6));
        PaintText(g,status,smallFont,Cream,new RectangleF(655,38,72,28));
        Panel(g,new RectangleF(22,101,936,48),Card,Edge);
        PaintText(g,period.detail,smallFont,Muted,new RectangleF(540,109,390,32),StringAlignment.Far);
    }

    private void DrawSummary(Graphics g,KeyStatsPeriod period)
    {
        long mouse=period.keys.Where(pair=>pair.Key.StartsWith("Mouse",StringComparison.Ordinal)).Sum(pair=>(long)pair.Value);
        Panel(g,new RectangleF(22,160,936,78),Card,Edge);
        Fill(g,Sand,new RectangleF(38,178,3,42));
        PaintText(g,"输入合计",smallFont,Muted,new RectangleF(51,169,190,24));
        PaintText(g,Number(period.total),numberFont,Ink,new RectangleF(50,191,256,38));
        Line(g,Edge,320,176,320,222);
        PaintText(g,"键盘按键",smallFont,Muted,new RectangleF(342,169,244,24));
        PaintText(g,Number((long)period.total-mouse),numberFont,Ink,new RectangleF(341,191,253,38));
        Line(g,Edge,627,176,627,222);
        PaintText(g,"鼠标点击与滚动",smallFont,Muted,new RectangleF(650,169,244,24));
        PaintText(g,Number(mouse),numberFont,Ink,new RectangleF(649,191,274,38));
    }

    private void DrawKey(Graphics g,RectangleF rect,string id,int count,int max,bool mouse)
    {
        var point=new HeatPoint(rect,KeyMap.Label(id),count,mouse);
        heatPoints.Add(point);
        bool selected=hovered!=null && hovered.Area==rect;
        double heat=max<=0?0:Math.Pow(count/(double)max,.65);
        Color surface=Blend(Tint,Green,heat);
        bool lightText=Luminance(surface)<.184;
        Color keyInk=lightText?Color.White:Color.FromArgb(8,13,11);
        Fill(g,Edge,new RectangleF(rect.X,rect.Y+2,rect.Width,rect.Height));
        Panel(g,rect,surface,selected?Pine:Blend(Edge,Green,heat*.55),4);
        if(selected)
        {
            using(var outline=new Pen(lightText?Sand:Pine,1.5f))
                g.DrawRectangle(outline,rect.X+2,rect.Y+2,rect.Width-4,rect.Height-4);
        }
        PaintText(g,KeyMap.Short(id),keyFont,keyInk,
            new RectangleF(rect.X+2,rect.Y+3,rect.Width-4,count>0?13:rect.Height-6),StringAlignment.Center);
        if(count>0)
            PaintText(g,Compact(count),keyFont,keyInk,
                new RectangleF(rect.X+1,rect.Bottom-16,rect.Width-2,13),StringAlignment.Center);
    }

    private void DrawKeyboard(Graphics g,Dictionary<string,int> keys)
    {
        var rows=KeyMap.Rows;
        int max=rows.SelectMany(row=>row.Keys).Select(key=>Count(keys,key.Id)).DefaultIfEmpty(0).Max();
        float unit=651/KeyMap.TotalUnits;
        for(int row=0;row<rows.Count;row++)
        {
            float x=38+rows[row].Offset*unit;
            float y=293+row*34;
            foreach(var key in rows[row].Keys)
            {
                DrawKey(g,new RectangleF(x,y,key.Width*unit-2,29),key.Id,Count(keys,key.Id),max,false);
                x+=(key.Width+key.Gap)*unit;
            }
        }
    }

    private static int Count(Dictionary<string,int> keys,string id)
    {
        int count;
        return keys.TryGetValue(id,out count)?count:0;
    }

    private void DrawMouse(Graphics g,Dictionary<string,int> keys)
    {
        int max=MouseIds.Select(id=>Count(keys,id)).Max();
        Panel(g,new RectangleF(770,297,157,181),Color.FromArgb(221,231,218),Edge,46);
        Line(g,Edge,784,418,914,418);
        RectangleF[] parts={
            new RectangleF(783,325,40,85),new RectangleF(874,325,40,85),
            new RectangleF(827,344,43,29),new RectangleF(728,346,39,33),new RectangleF(728,385,39,33),
            new RectangleF(827,310,43,29),new RectangleF(827,378,43,29)
        };
        for(int i=0;i<MouseIds.Length;i++)
            DrawKey(g,parts[i],MouseIds[i],Count(keys,MouseIds[i]),max,true);
        PaintText(g,"滚轮按事件计数",keyFont,Muted,new RectangleF(780,435,137,24),StringAlignment.Center);
    }

    private void DrawHeatmaps(Graphics g,KeyStatsPeriod period)
    {
        Panel(g,new RectangleF(22,250,678,271),Card,Edge);
        Panel(g,new RectangleF(712,250,246,271),Card,Edge);
        PaintText(g,"键盘热力图",sectionFont,Ink,new RectangleF(38,259,190,25));
        string peak=period.total>0?"高峰 "+period.peakHour.ToString("00")+":00   "+Number(period.peakValue)+" 次":"";
        PaintText(g,peak,smallFont,Muted,new RectangleF(225,259,318,25),StringAlignment.Far);
        PaintText(g,"少",keyFont,Muted,new RectangleF(557,261,21,23));
        for(int i=0;i<5;i++)Fill(g,Blend(Tint,Green,i/4d),new RectangleF(578+i*15,268,12,8));
        PaintText(g,"多",keyFont,Muted,new RectangleF(656,261,24,23),StringAlignment.Far);
        PaintText(g,"鼠标热力图",sectionFont,Ink,new RectangleF(728,259,188,25));
        DrawKeyboard(g,period.keys);
        DrawMouse(g,period.keys);
        if(hovered!=null)hovered=heatPoints.FirstOrDefault(point=>point.Area==hovered.Area);
        DrawHeatDetail(g,new RectangleF(38,497,646,20),false,"悬停按键查看完整次数");
        DrawHeatDetail(g,new RectangleF(728,497,214,20),true,"悬停查看完整次数");
    }

    private void DrawHeatDetail(Graphics g,RectangleF area,bool mouse,string hint)
    {
        bool active=hovered!=null && hovered.Mouse==mouse;
        if(active)Panel(g,area,Tint,Tint,4);
        string text=active?hovered.Name+"   "+Number(hovered.Count)+" 次":hint;
        PaintText(g,text,smallFont,active?Pine:Muted,RectangleF.Inflate(area,-4,0));
    }

    private void DrawHistory(Graphics g,KeyStatsSnapshot snap)
    {
        Panel(g,new RectangleF(22,533,449,180),Card,Edge);
        PaintText(g,"最近 7 天",sectionFont,Ink,new RectangleF(38,543,180,23));
        PaintText(g,"每日键鼠合计",smallFont,Muted,new RectangleF(255,543,198,23),StringAlignment.Far);
        if(snap.history.Count==0 || snap.history.All(day=>day.total==0))
        {
            PaintText(g,"这 7 天还没有输入记录",textFont,Muted,new RectangleF(38,579,416,84),StringAlignment.Center);
            return;
        }
        int max=snap.history.Max(day=>day.total);
        const float bottom=678,step=59;
        Line(g,Edge,40,bottom,454,bottom);
        Line(g,Edge,40,624,454,624);
        for(int i=0;i<snap.history.Count;i++)
        {
            KeyStatsDaySummary day=snap.history[i];
            float x=43+i*step;
            float height=day.total*76f/max;
            if(height>0)
            {
                Fill(g,i==snap.history.Count-1?Green:Color.FromArgb(117,163,132),
                    new RectangleF(x+11,bottom-Math.Max(1,height),32,Math.Max(1,height)));
                PaintText(g,Compact(day.total),keyFont,Ink,new RectangleF(x,bottom-height-19,54,17),StringAlignment.Center);
            }
            string date=day.date.Length>=10?day.date.Substring(5,5):day.date;
            PaintText(g,date,keyFont,Muted,new RectangleF(x,bottom+4,54,20),StringAlignment.Center);
        }
    }

    private void DrawTopKeys(Graphics g,KeyStatsPeriod period)
    {
        Panel(g,new RectangleF(483,533,475,180),Card,Edge);
        PaintText(g,"最常用输入",sectionFont,Ink,new RectangleF(499,543,230,23));
        PaintText(g,period.label,smallFont,Muted,new RectangleF(768,543,174,23),StringAlignment.Far);
        var top=period.keys.OrderByDescending(pair=>pair.Value).ThenBy(pair=>pair.Key).Take(8).ToArray();
        if(top.Length==0)
        {
            PaintText(g,"这个时间范围还没有输入记录",textFont,Muted,new RectangleF(499,579,443,84),StringAlignment.Center);
            return;
        }
        int max=Math.Max(1,top[0].Value);
        for(int i=0;i<top.Length;i++)
        {
            float y=572+i*16;
            PaintText(g,KeyMap.Label(top[i].Key),keyFont,Ink,new RectangleF(499,y,104,16));
            Fill(g,Tint,new RectangleF(610,y+6,217,4));
            Fill(g,Green,new RectangleF(610,y+6,217f*top[i].Value/max,4));
            PaintText(g,Number(top[i].Value),keyFont,Ink,new RectangleF(834,y,108,16),StringAlignment.Far);
        }
    }

    private void DrawFooter(Graphics g,KeyStatsSnapshot snap)
    {
        PaintText(g,"最近输入",sectionFont,Ink,new RectangleF(24,731,118,20));
        PaintText(g,"仅当前运行期间",keyFont,Muted,new RectangleF(24,752,120,20));
        string[] recent=snap.recent.Skip(Math.Max(0,snap.recent.Length-16)).ToArray();
        if(recent.Length==0)
            PaintText(g,snap.enabled?"等待键盘或鼠标输入":"继续记录后显示新输入",smallFont,Muted,new RectangleF(150,737,676,28));
        for(int i=0;i<recent.Length;i++)
        {
            var rect=new RectangleF(150+i*42,738,39,25);
            Panel(g,rect,Tint,Edge,4);
            PaintText(g,KeyMap.Short(recent[i]),keyFont,Ink,rect,StringAlignment.Center);
        }
    }

    internal Bitmap RenderPreview()
    {
        var bitmap=new Bitmap(PageWidth,PageHeight);
        using(var g=Graphics.FromImage(bitmap))
        {
            DrawDashboard(g);
            foreach(Control control in Controls)
            {
                using(var child=new Bitmap(control.Width,control.Height))
                {
                    control.DrawToBitmap(child,new Rectangle(Point.Empty,child.Size));
                    g.DrawImageUnscaled(child,control.Left-AutoScrollPosition.X,control.Top-AutoScrollPosition.Y);
                }
            }
        }
        return bitmap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        GraphicsState state=e.Graphics.Save();
        e.Graphics.TranslateTransform(AutoScrollPosition.X,AutoScrollPosition.Y);
        DrawDashboard(e.Graphics);
        e.Graphics.Restore(state);
    }

    private void DrawDashboard(Graphics g)
    {
        g.Clear(Paper);
        g.SmoothingMode=SmoothingMode.AntiAlias;
        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        heatPoints.Clear();
        var snap=service.Snapshot();
        var period=service.Period(range);
        UpdateRecordingButton(snap.enabled);
        DrawHeader(g,snap,period);
        DrawSummary(g,period);
        DrawHeatmaps(g,period);
        DrawHistory(g,snap);
        DrawTopKeys(g,period);
        DrawFooter(g,snap);
    }
}
