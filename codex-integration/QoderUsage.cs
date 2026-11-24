using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Qoder bills in Credits. Its local usage metadata exposes per-request credits and
// context usage, but token counts and cache-hit tokens are not recorded for dfmodel.
// A small local ledger keeps today's request ids/credits so transcript compaction
// cannot make the displayed totals go backwards.
internal sealed class QoderUsageSnapshot
{
    public string session="",source="local-qoder-usage";
    public DateTime observedUtc;
    public double? dailyCredits,turnCredits,contextPercent;
    public DateTime? turnStartedUtc;
    internal static string Credits(double? value)
    {return value.HasValue?value.Value.ToString("0.##",CultureInfo.InvariantCulture):"--";}
    internal static string Percent(double? value)
    {return value.HasValue?value.Value.ToString("0.#",CultureInfo.InvariantCulture)+"%":"--";}
    internal static string Speed(double? credits,DateTime? startedUtc,DateTime now)
    {
        if(!credits.HasValue || !startedUtc.HasValue)return "--";
        double minutes=(now-startedUtc.Value).TotalMinutes;
        if(minutes<0.05)return "--";
        return (credits.Value/minutes).ToString("0.##",CultureInfo.InvariantCulture)+"/分";
    }
}

internal sealed class QoderUsageEntry
{
    public double credits;
    public string session="";
    public DateTime utc;
}

internal sealed class QoderUsageIndex
{
    public string date="";
    public Dictionary<string,QoderUsageEntry> requests=new Dictionary<string,QoderUsageEntry>();
    public Dictionary<string,DateTime> turns=new Dictionary<string,DateTime>();
    public Dictionary<string,double> contexts=new Dictionary<string,double>();
    public Dictionary<string,DateTime> contextTimes=new Dictionary<string,DateTime>();
}

internal sealed class QoderUsageReader
{
    private sealed class UsageEvent
    {
        internal string id="";
        internal DateTime utc;
        internal double? credits,ratio;
    }
    private sealed class FileCache
    {
        internal DateTime lastWrite;
        internal long length;
        internal DateTime? human;
        internal List<UsageEvent> events=new List<UsageEvent>();
    }
    private static readonly string Projects=Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".qoder-cn","projects");
    private readonly string indexFile;
    private readonly Dictionary<string,FileCache> files=new Dictionary<string,FileCache>();
    private QoderUsageIndex index=new QoderUsageIndex();
    private DateTime indexDate=DateTime.MinValue,lastGlobalScan=DateTime.MinValue,lastSave=DateTime.MinValue;
    private bool dirty;
    private string lastSession="",lastSessionFile;
    private DateTime lastFind=DateTime.MinValue;

    internal QoderUsageReader():this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qoder-usage-index.json")){}
    internal QoderUsageReader(string indexFile){this.indexFile=indexFile;}

    internal QoderUsageSnapshot Read(string session,DateTime now)
    {
        EnsureIndex(now);
        string file=null;
        if(!String.IsNullOrEmpty(session))file=FindSession(session,now);
        if(file!=null)ScanFile(file,now);
        if(now-lastGlobalScan>TimeSpan.FromSeconds(15))
        {
            ScanAll(now);lastGlobalScan=now;
        }
        Save(now);
        var snapshot=new QoderUsageSnapshot{observedUtc=now,session=session??""};
        snapshot.dailyCredits=Daily(now);
        if(file!=null)
        {
            DateTime turn;
            if(index.turns.TryGetValue(session,out turn))
            {
                snapshot.turnStartedUtc=turn;double sum=0;bool any=false;
                foreach(var entry in index.requests.Values)
                    if(entry.session==session && entry.utc>=turn){sum+=entry.credits;any=true;}
                snapshot.turnCredits=any?sum:(double?)null;
            }
            double context;
            if(index.contexts.TryGetValue(session,out context))snapshot.contextPercent=context;
        }
        return snapshot;
    }
    private void EnsureIndex(DateTime now)
    {
        DateTime today=now.ToLocalTime().Date;
        if(indexDate==today)return;
        indexDate=today;files.Clear();
        index=new QoderUsageIndex{date=today.ToString("yyyy-MM-dd")};
        if(!File.Exists(indexFile))return;
        try
        {
            var loaded=Wire.Json().Deserialize<QoderUsageIndex>(File.ReadAllText(indexFile));
            if(loaded==null || loaded.date!=index.date)return;
            if(loaded.requests!=null)index.requests=loaded.requests;
            if(loaded.turns!=null)index.turns=loaded.turns;
            if(loaded.contexts!=null)index.contexts=loaded.contexts;
            if(loaded.contextTimes!=null)index.contextTimes=loaded.contextTimes;
        }
        catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(FormatException){}catch(OverflowException){}
    }
    private double? Daily(DateTime now)
    {
        DateTime today=now.ToLocalTime().Date;double sum=0;bool any=false;
        foreach(var entry in index.requests.Values)
            if(entry.utc.ToLocalTime().Date==today){sum+=entry.credits;any=true;}
        return any?sum:(double?)null;
    }
    private void ScanAll(DateTime now)
    {
        if(!Directory.Exists(Projects))return;
        DateTime today=now.ToLocalTime().Date;
        try
        {
            foreach(string file in Directory.EnumerateFiles(Projects,"*.jsonl",SearchOption.AllDirectories))
            {
                if(new FileInfo(file).LastWriteTime.Date!=today)continue;
                ScanFile(file,now);
            }
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private void ScanFile(string file,DateTime now)
    {
        var info=new FileInfo(file);FileCache cache;
        if(!files.TryGetValue(file,out cache) || cache.lastWrite!=info.LastWriteTimeUtc || cache.length!=info.Length)
        {
            DateTime? human;var events=ReadEvents(file,out human);
            cache=new FileCache{lastWrite=info.LastWriteTimeUtc,length=info.Length,human=human,events=events};
            files[file]=cache;
        }
        string session=Path.GetFileNameWithoutExtension(file);
        if(cache.human.HasValue)
        {
            DateTime prior;
            if(!index.turns.TryGetValue(session,out prior) || cache.human.Value>prior)
            {index.turns[session]=cache.human.Value;dirty=true;}
        }
        double? latestRatio=null;DateTime latest=DateTime.MinValue;
        foreach(var item in cache.events)
        {
            if(item.credits.HasValue && item.utc.ToLocalTime().Date==now.ToLocalTime().Date)
            {
                QoderUsageEntry prior;
                if(!index.requests.TryGetValue(item.id,out prior) || item.utc>prior.utc || prior.credits!=item.credits.Value)
                {index.requests[item.id]=new QoderUsageEntry{credits=item.credits.Value,session=session,utc=item.utc};dirty=true;}
            }
            if(item.ratio.HasValue && item.utc>=latest){latest=item.utc;latestRatio=item.ratio;}
        }
        if(latestRatio.HasValue)
        {
            DateTime prior;
            if(!index.contextTimes.TryGetValue(session,out prior) || latest>prior)
            {
                index.contexts[session]=latestRatio.Value*100;
                index.contextTimes[session]=latest;dirty=true;
            }
        }
    }
    private void Save(DateTime now)
    {
        if(!dirty || now-lastSave<TimeSpan.FromSeconds(3))return;
        try
        {
            string temporary=indexFile+".tmp";
            File.WriteAllText(temporary,Wire.Json().Serialize(index),new UTF8Encoding(false));
            if(File.Exists(indexFile))File.Replace(temporary,indexFile,null);else File.Move(temporary,indexFile);
            dirty=false;lastSave=now;
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private string FindSession(string session,DateTime now)
    {
        if(session==lastSession && now-lastFind<TimeSpan.FromSeconds(30))return lastSessionFile;
        lastSession=session;lastFind=now;lastSessionFile=null;
        if(!Directory.Exists(Projects))return null;
        try
        {
            foreach(string file in Directory.EnumerateFiles(Projects,"*.jsonl",SearchOption.AllDirectories))
                if(String.Equals(Path.GetFileNameWithoutExtension(file),session,StringComparison.OrdinalIgnoreCase))
                {lastSessionFile=file;break;}
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
        return lastSessionFile;
    }
    private static List<UsageEvent> ReadEvents(string file,out DateTime? turnStart)
    {
        turnStart=null;var byId=new Dictionary<string,UsageEvent>();
        using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var reader=new StreamReader(stream,Encoding.UTF8))
        {
            long start=Math.Max(0,stream.Length-16*1024*1024);
            if(start>0){stream.Seek(start,SeekOrigin.Begin);reader.ReadLine();}
            string line;
            while((line=reader.ReadLine())!=null)
            {
                if(line.Length==0)continue;
                if(line[0]=='\uFEFF')line=line.Substring(1);
                if(line.StartsWith("{\"type\":\"assistant\"",StringComparison.Ordinal))
                {
                    var usage=Usage(line);if(usage==null)continue;
                    var timestamp=Timestamp(line);if(!timestamp.HasValue)continue;
                    var item=new UsageEvent{utc=timestamp.Value,id=StringOf(usage,"request_id")};
                    double value;
                    if(TryNumber(usage,"credits",out value))item.credits=value;
                    if(TryNumber(usage,"context_usage_ratio",out value))item.ratio=value;
                    if(item.credits.HasValue || item.ratio.HasValue)
                    {
                        string key=item.id.Length>0?item.id:"line:"+byId.Count;
                        item.id=key;
                        UsageEvent prior;
                        if(byId.TryGetValue(key,out prior) && prior.utc>item.utc)continue;
                        byId[key]=item;
                    }
                }
                else if(line.StartsWith("{\"type\":\"user\"",StringComparison.Ordinal)
                    && line.IndexOf("\"humanInput\":",StringComparison.Ordinal)>=0
                    && line.IndexOf("\"toolUseResult\":",StringComparison.Ordinal)<0)
                {
                    var timestamp=Timestamp(line);
                    if(timestamp.HasValue && (!turnStart.HasValue || timestamp.Value>turnStart.Value))turnStart=timestamp;
                }
            }
        }
        return byId.Values.OrderBy(x=>x.utc).ToList();
    }
    private static string StringOf(Dictionary<string,object> data,string key)
    {
        object value;return data.TryGetValue(key,out value)&&value!=null?Convert.ToString(value):"";
    }
    private static bool TryNumber(Dictionary<string,object> data,string key,out double value)
    {
        value=0;object raw;
        if(!data.TryGetValue(key,out raw) || raw==null)return false;
        try
        {
            value=Convert.ToDouble(raw,CultureInfo.InvariantCulture);
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
        catch(FormatException){return false;}catch(InvalidCastException){return false;}catch(OverflowException){return false;}catch(ArgumentException){return false;}
    }
    private static DateTime? Timestamp(string line)
    {
        int start=line.IndexOf("\"timestamp\":\"",StringComparison.Ordinal);
        if(start<0)return null;
        start+=13;int end=line.IndexOf('"',start);
        if(end<=start)return null;
        DateTimeOffset timestamp;
        if(!DateTimeOffset.TryParse(line.Substring(start,end-start),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out timestamp))return null;
        return timestamp.UtcDateTime;
    }
    private static Dictionary<string,object> Usage(string line)
    {
        // Usage is the last field of an assistant record; content before it may contain similar text.
        int marker=line.LastIndexOf("\"usage\":",StringComparison.Ordinal);
        if(marker<0)return null;
        int start=line.IndexOf('{',marker);
        if(start<0)return null;
        int depth=0;bool quoted=false,escaped=false;
        for(int i=start;i<line.Length;i++)
        {
            char c=line[i];
            if(quoted)
            {
                if(escaped)escaped=false;
                else if(c=='\\')escaped=true;
                else if(c=='"')quoted=false;
            }
            else
            {
                if(c=='"')quoted=true;
                else if(c=='{')depth++;
                else if(c=='}')
                {
                    depth--;
                    if(depth==0)
                    {
                        try{return Wire.Json().Deserialize<Dictionary<string,object>>(line.Substring(start,i-start+1));}
                        catch(ArgumentException){return null;}catch(FormatException){return null;}catch(OverflowException){return null;}
                    }
                }
            }
        }
        return null;
    }
}
