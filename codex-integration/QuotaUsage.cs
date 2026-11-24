// Read only Codex token_count usage-event records. Never deserialize message records.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

internal sealed class QuotaWindow
{
    public double usedPercent;
    public int windowMinutes;
    public long resetsAt;
    internal double Remaining{get{return Math.Max(0,Math.Min(100,100-usedPercent));}}
    internal bool Expired(DateTime now){return resetsAt>0 && now>=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(resetsAt);}
    internal string Percent(DateTime now){return Expired(now)?"待刷新":Remaining.ToString("0.#",CultureInfo.InvariantCulture)+"%";}
    internal string ResetLabel(DateTime now)
    {
        if(resetsAt<=0)return "重置时间未知";
        var reset=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(resetsAt).ToLocalTime();
        return Expired(now)?"等待新用量事件":reset.ToString(windowMinutes==300?"HH:mm":"MM-dd HH:mm")+" 重置";
    }
}
internal sealed class QuotaSnapshot
{
    public QuotaWindow fiveHour,weekly;
    public DateTime observedUtc;
    public string source="local-codex-usage-event";
    internal bool Stale(DateTime now){return now-observedUtc>TimeSpan.FromMinutes(10);}
}
internal static class QuotaReader
{
    internal static QuotaSnapshot Parse(string line)
    {
        // Usage events contain token totals and quota windows, not conversation content.
        if(line.Length>128*1024 || !line.Contains("\"token_count\"") || !line.Contains("\"rate_limits\""))return null;
        var root=Wire.Json().Deserialize<Dictionary<string,object>>(line);
        if(root==null || Wire.Str(root,"type",30)!="event_msg")return null;
        object value;Dictionary<string,object> payload,limits;
        if(!root.TryGetValue("payload",out value) || (payload=value as Dictionary<string,object>)==null || Wire.Str(payload,"type",30)!="token_count")return null;
        if(!payload.TryGetValue("rate_limits",out value) || (limits=value as Dictionary<string,object>)==null)return null;
        if(Wire.Str(limits,"limit_id",100)!="codex")return null;
        DateTimeOffset observed;
        if(!DateTimeOffset.TryParse(Wire.Str(root,"timestamp",80),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out observed))return null;
        var snapshot=new QuotaSnapshot{observedUtc=observed.UtcDateTime};
        foreach(string key in new[]{"primary","secondary"})
        {
            Dictionary<string,object> window;if(!limits.TryGetValue(key,out value) || (window=value as Dictionary<string,object>)==null)continue;
            object used,minutes,reset;
            if(!window.TryGetValue("used_percent",out used) || used==null || !window.TryGetValue("window_minutes",out minutes) || minutes==null)continue;
            double percent=Convert.ToDouble(used,CultureInfo.InvariantCulture);if(Double.IsNaN(percent) || Double.IsInfinity(percent))continue;
            var w=new QuotaWindow{usedPercent=Math.Max(0,Math.Min(100,percent)),windowMinutes=Convert.ToInt32(minutes)};
            if(window.TryGetValue("resets_at",out reset) && reset!=null)w.resetsAt=Convert.ToInt64(reset);
            if(w.windowMinutes==300)snapshot.fiveHour=w;
            else if(w.windowMinutes==10080)snapshot.weekly=w;
        }
        return snapshot.fiveHour!=null || snapshot.weekly!=null?snapshot:null;
    }
    internal static QuotaSnapshot Read(string root)
    {
        if(!Directory.Exists(root))return null;QuotaSnapshot newest=null;
        // Bounded tail reads from the eight latest session logs; no full transcript parsing.
        foreach(var file in Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories)
            .Select(p=>new FileInfo(p)).OrderByDescending(f=>f.LastWriteTimeUtc).Take(8))
        {
            try
            {
                using(var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    long offset=Math.Max(0,stream.Length-512*1024);stream.Seek(offset,SeekOrigin.Begin);
                    using(var reader=new StreamReader(stream,Encoding.UTF8))
                    {
                        if(offset>0)reader.ReadLine();string line;
                        while((line=reader.ReadLine())!=null)
                        {
                            QuotaSnapshot s;try{s=Parse(line);}catch(ArgumentException){continue;}catch(FormatException){continue;}catch(OverflowException){continue;}
                            if(s!=null && s.observedUtc<=DateTime.UtcNow.AddMinutes(1) && (newest==null || s.observedUtc>newest.observedUtc))newest=s;
                        }
                    }
                }
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
        return newest;
    }
}
