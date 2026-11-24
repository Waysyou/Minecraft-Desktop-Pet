// Usage metadata only. No prompts, responses, credentials or keyboard data are retained.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class DailyTokenSnapshot
{
    public string date,source,status;
    public long? tokens;
    public DateTime observedUtc;
    public DateTime? lastEventUtc;
    public bool partial;
    internal string Label { get { return (source=="official"?date.Substring(5)+" token":"今日 token")+"  "+(tokens.HasValue?Format(tokens.Value):"未知"); } }
    internal string Caption { get { return (source=="official"?"官方统计":source=="local"?"本机统计 · 含缓存输入":"用量统计暂不可用")+(partial?" · 部分记录":""); } }
    internal static string Format(long value)
    {
        if(value>=1000000)return (value/1000000d).ToString("0.##",CultureInfo.InvariantCulture)+"M";
        if(value>=1000)return (value/1000d).ToString("0.##",CultureInfo.InvariantCulture)+"K";
        return value.ToString(CultureInfo.InvariantCulture);
    }
}

internal sealed class TokenCursor
{
    public long offset,previous=-1,previousOutput=-1;
    public string session="",turn="";
}
internal sealed class TokenEntry
{
    public DateTime utc;
    public long tokens;
    public string session,turn;
    public long? output,input,cachedInput;
}
internal sealed class TokenTurn
{
    public string session,turn;
    public DateTime startedUtc;
}
internal sealed class RequestMetrics
{
    public string session,turn,source="local-usage-metadata";
    public DateTime? startedUtc;
    public DateTime observedUtc;
    public long? outputTokens,inputTokens,cachedInputTokens;
    internal static string Key(string session,string turn){return session+"|"+turn;}
    internal double? Speed(DateTime now)
    {
        if(!startedUtc.HasValue || !outputTokens.HasValue || observedUtc>now.AddSeconds(1))return null;
        double seconds=(now-startedUtc.Value).TotalSeconds;
        return seconds>=1?(double?)(outputTokens.Value/seconds):null;
    }
    internal string SpeedLabel(DateTime now)
    {double? speed=Speed(now);return "本轮均速约 "+(speed.HasValue?speed.Value.ToString("0.0",CultureInfo.InvariantCulture):"--")+" tok/s · 输出 "+(outputTokens.HasValue?DailyTokenSnapshot.Format(outputTokens.Value):"--");}
    internal string CacheLabel
    {
        get
        {
            bool valid=inputTokens.HasValue && inputTokens.Value>0 && cachedInputTokens.HasValue && cachedInputTokens.Value>=0 && cachedInputTokens.Value<=inputTokens.Value;
            string rate=valid?(100d*cachedInputTokens.Value/inputTokens.Value).ToString("0.#",CultureInfo.InvariantCulture)+"%":"--";
            return "最近请求缓存 "+(cachedInputTokens.HasValue?DailyTokenSnapshot.Format(cachedInputTokens.Value):"--")+" / "+(inputTokens.HasValue?DailyTokenSnapshot.Format(inputTokens.Value):"--")+" · "+rate;
        }
    }
}
internal sealed class TokenIndex
{
    public int version=2;
    public string root;
    public Dictionary<string,TokenCursor> files=new Dictionary<string,TokenCursor>();
    public Dictionary<string,TokenEntry> events=new Dictionary<string,TokenEntry>();
    public Dictionary<string,TokenTurn> turns=new Dictionary<string,TokenTurn>();
}

internal sealed class LocalDailyTokenReader
{
    private readonly string root,cache;
    private TokenIndex index;
    internal LocalDailyTokenReader(string codexHome,string cacheFile)
    {
        root=Path.GetFullPath(codexHome);cache=cacheFile;
        try{if(File.Exists(cache) && new FileInfo(cache).Length<32*1024*1024)index=Json().Deserialize<TokenIndex>(File.ReadAllText(cache));}
        catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
        if(index==null || index.version!=2 || index.root!=root || index.files==null || index.events==null || index.turns==null)index=new TokenIndex{root=root};
    }
    private static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024};}
    private static Dictionary<string,object> Object(Dictionary<string,object> obj,string key)
    {object v;return obj!=null && obj.TryGetValue(key,out v)?v as Dictionary<string,object>:null;}
    private static long? Number(Dictionary<string,object> obj,string key)
    {object v;long n;return obj!=null && obj.TryGetValue(key,out v) && v!=null && Int64.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),out n) && n>=0?(long?)n:null;}
    internal static string Home {get{return Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");}}
    internal Dictionary<string,RequestMetrics> Metrics()
    {
        var result=new Dictionary<string,RequestMetrics>();
        foreach(var group in index.events.Values.Where(x=>!String.IsNullOrEmpty(x.session) && !String.IsNullOrEmpty(x.turn)).GroupBy(x=>RequestMetrics.Key(x.session,x.turn)))
        {
            var last=group.OrderByDescending(x=>x.utc).First();TokenTurn turn;index.turns.TryGetValue(group.Key,out turn);
            result[group.Key]=new RequestMetrics{session=last.session,turn=last.turn,observedUtc=last.utc,
                startedUtc=turn==null?(DateTime?)null:turn.startedUtc,
                outputTokens=group.All(x=>x.output.HasValue)?(long?)group.Sum(x=>x.output.Value):null,inputTokens=last.input,cachedInputTokens=last.cachedInput};
        }
        return result;
    }
    internal DailyTokenSnapshot Read(DateTime now)
    {
        bool partial=false,changed=false;int available=0;
        foreach(string folder in new[]{"sessions","archived_sessions"})
        {
            string dir=Path.Combine(root,folder);if(!Directory.Exists(dir))continue;available++;
            try{foreach(string file in Directory.EnumerateFiles(dir,"*.jsonl",SearchOption.AllDirectories))
                try{TokenCursor before;long offset=index.files.TryGetValue(file,out before)?before.offset:-1;Scan(file,now);if(index.files[file].offset!=offset)changed=true;}catch(IOException){partial=true;}catch(UnauthorizedAccessException){partial=true;}}
            catch(IOException){partial=true;}catch(UnauthorizedAccessException){partial=true;}
        }
        DateTime cutoff=now.ToLocalTime().Date.AddDays(-7).ToUniversalTime();
        foreach(string key in index.events.Where(x=>x.Value.utc<cutoff).Select(x=>x.Key).ToArray()){index.events.Remove(key);changed=true;}
        foreach(string key in index.turns.Where(x=>x.Value.startedUtc<cutoff).Select(x=>x.Key).ToArray()){index.turns.Remove(key);changed=true;}
        DateTime day=now.ToLocalTime().Date;
        var today=index.events.Values.Where(x=>x.utc.ToLocalTime().Date==day && x.utc<=now.AddMinutes(1)).ToArray();
        var result=new DailyTokenSnapshot{date=day.ToString("yyyy-MM-dd"),source="local",status=available==0?"no-local-records":"ok",
            tokens=available==0?(long?)null:today.Sum(x=>x.tokens),observedUtc=now,partial=partial,
            lastEventUtc=today.Length==0?(DateTime?)null:today.Max(x=>x.utc)};
        // Store only cursors, dates, hashed event identities and token counts; replace atomically.
        try{if(changed || !File.Exists(cache)){string temp=cache+".tmp";File.WriteAllText(temp,Json().Serialize(index),new UTF8Encoding(false));
            if(File.Exists(cache))File.Replace(temp,cache,null);else File.Move(temp,cache);}}
        catch(IOException){}catch(UnauthorizedAccessException){}
        return result;
    }
    private void Scan(string file,DateTime now)
    {
        TokenCursor cursor;if(!index.files.TryGetValue(file,out cursor)){cursor=new TokenCursor();index.files.Add(file,cursor);}
        using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            long end=stream.Length;if(cursor.offset>end){cursor.offset=0;cursor.previous=-1;cursor.previousOutput=-1;cursor.session="";cursor.turn="";}
            stream.Position=cursor.offset;long position=cursor.offset;
            var buffer=new byte[65536];var line=new MemoryStream();bool oversize=false;
            while(position<end)
            {
                int read=stream.Read(buffer,0,(int)Math.Min(buffer.Length,end-position));if(read==0)break;
                int start=0;
                while(start<read)
                {
                    int newline=Array.IndexOf(buffer,(byte)10,start,read-start);
                    int count=(newline<0?read:newline)-start;
                    if(!oversize){if(line.Length+count<=128*1024)line.Write(buffer,start,count);else oversize=true;}
                    position+=count;
                    if(newline>=0)
                    {
                        position++;
                        if(!oversize)Consume(Encoding.UTF8.GetString(line.GetBuffer(),0,(int)line.Length),cursor,now);
                        line.SetLength(0);oversize=false;cursor.offset=position;
                    }
                    start=newline<0?read:newline+1;
                }
            }
            // Leave a trailing incomplete JSON line for the next read.
            line.Dispose();
        }
    }
    private void Consume(string line,TokenCursor cursor,DateTime now)
    {
        string prefix=line.Substring(0,Math.Min(256,line.Length));
        if(!new[]{"\"token_count\"","\"session_meta\"","\"task_started\""}.Any(prefix.Contains))return;
        Dictionary<string,object> obj;
        try{obj=Wire.Json().Deserialize<Dictionary<string,object>>(line);}catch(ArgumentException){return;}catch(InvalidOperationException){return;}
        if(obj==null)return;var payload=Object(obj,"payload");
        if(Wire.Str(obj,"type",30)=="session_meta") {if(payload!=null)cursor.session=Wire.Str(payload,"id",100);return;}
        if(payload==null)return;
        string type=Wire.Str(obj,"type",30),kind=Wire.Str(payload,"type",30);
        if(type!="event_msg")return;
        DateTimeOffset time;
        if(!DateTimeOffset.TryParse(Wire.Str(obj,"timestamp",80),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out time))return;
        if(kind=="task_started")
        {
            string turnId=Wire.Str(payload,"turn_id",100);if(turnId.Length==0)return;
            string turnKey=RequestMetrics.Key(cursor.session,turnId);cursor.turn=turnId;
            if(!index.turns.ContainsKey(turnKey))index.turns[turnKey]=new TokenTurn{session=cursor.session,turn=turnId,startedUtc=time.UtcDateTime};
            return;
        }
        if(kind!="token_count")return;
        var info=Object(payload,"info");long? total=Number(Object(info,"total_token_usage"),"total_tokens"),last=Number(Object(info,"last_token_usage"),"total_tokens");
        if(!total.HasValue)return;
        var lastUsage=Object(info,"last_token_usage");long? totalOutput=Number(Object(info,"total_token_usage"),"output_tokens"),lastOutput=Number(lastUsage,"output_tokens");
        long previousOutput=cursor.previousOutput;if(totalOutput.HasValue)cursor.previousOutput=totalOutput.Value;
        long previous=cursor.previous;cursor.previous=total.Value;
        // Repeated token_count quota updates carry the same cumulative count.
        if(previous==total.Value)return;
        if(time.UtcDateTime>now.AddMinutes(1) || time.LocalDateTime.Date<now.ToLocalTime().Date.AddDays(-7))return;
        // A copied/forked log can begin with inherited cumulative usage. Count its last request only.
        long delta=previous>=0 && total.Value>previous?total.Value-previous:Math.Min(total.Value,last??0);
        if(delta<=0)return;
        string identity=cursor.session+"|"+time.UtcDateTime.ToString("o")+"|"+total.Value+"|"+(last??-1);
        string key;using(var hash=SHA256.Create())key=Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)));
        long? outputDelta=totalOutput.HasValue && previousOutput>=0 && totalOutput.Value>=previousOutput?(long?)(totalOutput.Value-previousOutput):lastOutput;
        TokenEntry existing;if(!index.events.TryGetValue(key,out existing))index.events.Add(key,new TokenEntry{utc=time.UtcDateTime,tokens=delta,
            session=cursor.session,turn=cursor.turn,output=outputDelta,input=Number(lastUsage,"input_tokens"),cachedInput=Number(lastUsage,"cached_input_tokens")});
        else if(delta>existing.tokens)existing.tokens=delta;
    }
}

internal static class OfficialDailyTokenReader
{
    internal static DailyTokenSnapshot Parse(Dictionary<string,object> result,DateTime now)
    {
        object buckets;if(result==null || !result.TryGetValue("dailyUsageBuckets",out buckets) || !(buckets is IEnumerable))return null;
        string date=now.ToLocalTime().ToString("yyyy-MM-dd");
        foreach(object row in (IEnumerable)buckets)
        {
            var item=row as Dictionary<string,object>;object value;long tokens;
            if(item!=null && Wire.Str(item,"startDate",30)==date && item.TryGetValue("tokens",out value) && value!=null && Int64.TryParse(Convert.ToString(value),out tokens) && tokens>=0)
                return new DailyTokenSnapshot{date=date,tokens=tokens,source="official",status="ok",observedUtc=now};
        }
        return null; // Missing bucket is unknown, not zero. Official bucket date is displayed explicitly.
    }
    internal static DailyTokenSnapshot Read(DateTime now,out string status)
    {
        status="unavailable";string cli=FindCli();if(cli==null){status="cli-not-found";return null;}
        Process child=null;
        try
        {
            child=Process.Start(new ProcessStartInfo(cli,"app-server"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8});
            child.ErrorDataReceived+=(s,e)=>{};child.BeginErrorReadLine();
            // Only initialization and the official read-only usage RPC. The CLI manages its own login.
            child.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"minecraft_usage\",\"version\":\"1\"},\"capabilities\":{\"experimentalApi\":true}}}");child.StandardInput.Flush();
            DateTime deadline=DateTime.UtcNow.AddSeconds(15);
            while(DateTime.UtcNow<deadline)
            {
                var read=child.StandardOutput.ReadLineAsync();int remaining=Math.Max(1,(int)(deadline-DateTime.UtcNow).TotalMilliseconds);
                if(!read.Wait(remaining)){status="timeout";return null;}
                string line=read.Result;if(line==null)break;if(line.Length>1024*1024)continue;
                var json=new JavaScriptSerializer{MaxJsonLength=1024*1024};var reply=json.Deserialize<Dictionary<string,object>>(line);object id;
                if(reply==null || !reply.TryGetValue("id",out id))continue;
                if(Convert.ToString(id)=="1")
                {
                    if(reply.ContainsKey("error")){status="initialize-error";return null;}
                    child.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                    child.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/usage/read\",\"params\":{}}");child.StandardInput.Flush();
                }
                else if(Convert.ToString(id)=="2")
                {
                    if(reply.ContainsKey("error")){status="official-unavailable";return null;}
                    object value;var snapshot=Parse(reply.TryGetValue("result",out value)?value as Dictionary<string,object>:null,now);
                    status=snapshot==null?"bucket-unavailable":"ok";return snapshot;
                }
            }
        }
        catch(IOException){}catch(InvalidOperationException){}catch(ArgumentException){}catch(System.ComponentModel.Win32Exception){}catch(AggregateException){}
        finally
        {
            if(child!=null){try{child.StandardInput.Close();if(!child.WaitForExit(500))child.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}finally{child.Dispose();}}
        }
        return null;
    }
    private static string FindCli()
    {
        string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
        try{if(Directory.Exists(dir))return Directory.EnumerateFiles(dir,"codex.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();}
        catch(IOException){}catch(UnauthorizedAccessException){}
        foreach(string folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
            try{string file=Path.Combine(folder,"codex.exe");if(File.Exists(file))return file;}catch(ArgumentException){}
        return null;
    }
}
