using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.IO;

using System.Windows.Forms;

// Per-pixel alpha avoids the colored fringes of a chroma-keyed window.
internal sealed class StatusBubble:Form
{
    internal List<WorkItem> Rows=new List<WorkItem>();
    internal QuotaSnapshot Quota;
    internal DailyTokenSnapshot Daily;
    internal QoderUsageSnapshot Qoder;
    internal RequestMetrics Metrics;
    internal DateTime Now;
    internal byte PresentedAlpha=255;
    private string signature="";
    private IntPtr bitmapHandle;
    private readonly string settingsFile;
    private double requestedZoom=1,effectiveZoom=1;
    private int logicalHeight=330;
    private Size viewport=new Size(3840,2160);
    private bool sizing,dragging,overGrip;
    private Point gestureOrigin;
    private double gestureZoom;
    internal Func<int,bool> PetDrag;
    internal bool DiagnosticWindow;
    internal double Zoom{get{return effectiveZoom;}}
    internal bool Interacting{get{return sizing || dragging;}}
    internal BubbleAppearance Appearance{get;private set;}
    private Font heading,stateFont,detail,quotaFont,small;
    private BubbleAppearanceDialog appearanceDialog;

    internal string BackdropMode{get{return "inventory-tinted";}}
    private bool HasCodexUsage{get{return Rows.Any(x=>x.source=="codex");}}
    private bool HasQoderUsage{get{return Rows.Any(x=>x.source=="qoder");}}
    private int titleExtra,identityExtra,rowStep=44,usageY,quotaY,dailyY,qoderY,speedY,cacheY,qoderTurnY,quotaHeight=20,metaHeight=16;
    internal StatusBubble(string preferences=null)
    {
        settingsFile=preferences??Path.Combine(Wire.Root,"bubble-ui.json");Appearance=new BubbleAppearance();
        try{if(settingsFile.Length>0 && File.Exists(settingsFile)){var data=Wire.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(settingsFile));object value;
            if(data!=null && data.TryGetValue("zoom",out value))requestedZoom=Convert.ToDouble(value);
            if(data!=null && data.TryGetValue("appearance",out value) && value!=null)Appearance=Wire.Json().Deserialize<BubbleAppearance>(Wire.Json().Serialize(value));}}
        catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(FormatException){}catch(OverflowException){}
        if(Double.IsNaN(requestedZoom) || Double.IsInfinity(requestedZoom))requestedZoom=1;
        requestedZoom=Math.Max(.75,Math.Min(2,requestedZoom));
        ApplyAppearance(Appearance);
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        AutoScaleMode=AutoScaleMode.None;ApplyDimensions();
        Text="Minecraft · Codex 思考气泡";
        var menu=new ContextMenuStrip();menu.Items.Add("按键统计…",null,(s,e)=>KeyStatsForm.OpenShared());menu.Items.Add("外观设置…",null,(s,e)=>OpenAppearance());menu.Items.Add(new ToolStripSeparator());menu.Items.Add("放大",null,(s,e)=>SetZoom(requestedZoom*1.1,true));
        menu.Items.Add("缩小",null,(s,e)=>SetZoom(requestedZoom/1.1,true));menu.Items.Add("恢复 100%",null,(s,e)=>SetZoom(1,true));ContextMenuStrip=menu;
    }
    protected override bool ShowWithoutActivation{get{return true;}}
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80000|(DiagnosticWindow?0:0x80);return p;}}
    protected override void OnPaint(PaintEventArgs e){}
    internal void UpdateRows(List<WorkItem> rows,DateTime now)
    {
        Rows=rows;Now=now;int count=Math.Max(1,Math.Min(3,rows.Count));
        UpdateLayout();ApplyDimensions();
        string next=String.Join("|",rows.Take(3).Select(x=>x.session+":"+x.source+":"+x.workspace+":"+x.state+":"+((int)(now-x.updated).TotalMinutes)).ToArray())
            +rows.Count+":"+(Quota==null?"none":Wire.Json().Serialize(Quota))+":"+(Daily==null?"pending":Wire.Json().Serialize(Daily))+":"+(Qoder==null?"none":Wire.Json().Serialize(Qoder))+":"+(Metrics==null?"pending":Wire.Json().Serialize(Metrics))
            +":"+now.ToString(Metrics==null && !HasQoderUsage?"yyyyMMddHHmm":"yyyyMMddHHmmss");
        if(next!=signature){signature=next;if(bitmapHandle!=IntPtr.Zero){Native.DeleteObject(bitmapHandle);bitmapHandle=IntPtr.Zero;}}
    }
    private void InvalidateBitmap(){signature="";if(bitmapHandle!=IntPtr.Zero){Native.DeleteObject(bitmapHandle);bitmapHandle=IntPtr.Zero;}}
    private void UpdateLayout()
    {
        int count=Math.Max(1,Math.Min(3,Rows.Count));titleExtra=Math.Max(0,(int)Math.Ceiling(39*Appearance.title.size/17)-39);
        identityExtra=Math.Max(0,(int)Math.Ceiling(21*Appearance.secondary.size/8)-21);quotaHeight=Math.Max(20,(int)Math.Ceiling(20*Appearance.data.size/9));metaHeight=Math.Max(16,(int)Math.Ceiling(16*Appearance.secondary.size/8));
        rowStep=Math.Max(44,quotaHeight+metaHeight+8);int rowsEnd=105+titleExtra+identityExtra+rowStep*(count-1);
        usageY=rowsEnd+8;int y=usageY;
        quotaY=dailyY=qoderY=speedY=cacheY=qoderTurnY=-1;
        if(HasCodexUsage)
        {
            quotaY=y;y+=quotaHeight+14+metaHeight+8;
            dailyY=y;y+=quotaHeight+2;
            speedY=y;y+=quotaHeight+1;
            cacheY=y;y+=metaHeight+4;
        }
        if(HasQoderUsage)
        {
            if(HasCodexUsage)y+=8;
            qoderY=y;y+=quotaHeight+2;
            qoderTurnY=y;y+=quotaHeight;
        }
        logicalHeight=y+22+56;
    }
    internal void ApplyAppearance(BubbleAppearance appearance)
    {
        Appearance=(appearance??new BubbleAppearance()).Normalize();
        if(heading!=null){heading.Dispose();stateFont.Dispose();detail.Dispose();quotaFont.Dispose();small.Dispose();}
        heading=BubbleAppearance.MakeFont(Appearance.title);stateFont=BubbleAppearance.MakeFont(Appearance.data,1);detail=BubbleAppearance.MakeFont(Appearance.secondary);quotaFont=BubbleAppearance.MakeFont(Appearance.data);small=BubbleAppearance.MakeFont(Appearance.secondary);
        UpdateLayout();ApplyDimensions();InvalidateBitmap();
    }
    internal bool SaveAppearance(BubbleAppearance appearance)
    {
        var clean=(appearance??new BubbleAppearance()).Normalize();if(!SavePreferences(clean))return false;ApplyAppearance(clean);return true;
    }
    internal void OpenAppearance()
    {
        if(appearanceDialog!=null && !appearanceDialog.IsDisposed){appearanceDialog.Activate();return;}
        appearanceDialog=new BubbleAppearanceDialog(this);appearanceDialog.FormClosed+=(s,e)=>appearanceDialog=null;appearanceDialog.Show();Native.ShowWindow(appearanceDialog.Handle,5);appearanceDialog.Activate();
    }
    private void ApplyDimensions()
    {
        effectiveZoom=Math.Min(requestedZoom,Math.Min(Math.Max(200,viewport.Width-12)/430d,Math.Max(180,viewport.Height-12)/(double)logicalHeight));
        int w=(int)Math.Round(430*effectiveZoom),h=(int)Math.Round(logicalHeight*effectiveZoom);
        if(Width!=w || Height!=h){Size=new Size(w,h);InvalidateBitmap();}
    }
    internal void SetViewport(Size size){viewport=size;ApplyDimensions();}
    internal void SetZoom(double zoom,bool save)
    {
        if(Double.IsNaN(zoom) || Double.IsInfinity(zoom))return;
        requestedZoom=Math.Max(.75,Math.Min(2,zoom));ApplyDimensions();if(save)SaveZoom();
    }
    private void SaveZoom()
    {
        SavePreferences(Appearance);
    }
    private bool SavePreferences(BubbleAppearance appearance)
    {
        if(settingsFile.Length==0)return true;
        string temporary=settingsFile+".tmp";
        try{File.WriteAllText(temporary,Wire.Json().Serialize(new{version=2,zoom=requestedZoom,appearance=appearance}),new System.Text.UTF8Encoding(false));if(File.Exists(settingsFile))File.Replace(temporary,settingsFile,null);else File.Move(temporary,settingsFile);return true;}
        catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
    }
    internal RectangleF Grip{get{return new RectangleF(362,logicalHeight-56-96,21,21);}}
    private PointF PointerInCanvas(MouseEventArgs e)
    {return new PointF(e.X/(float)effectiveZoom,e.Y/(float)effectiveZoom);}
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x21){m.Result=new IntPtr(3);return;} // Keep keyboard focus in the user's current app.
        if(m.Msg==0x20a && (m.WParam.ToInt64()&8)!=0)
        {int delta=(short)((m.WParam.ToInt64()>>16)&65535);SetZoom(requestedZoom*Math.Pow(1.1,delta/120d),true);m.Result=IntPtr.Zero;return;}
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;
        gestureOrigin=System.Windows.Forms.Cursor.Position;gestureZoom=effectiveZoom;
        if(Grip.Contains(PointerInCanvas(e)))sizing=true;
        else if(PetDrag!=null)dragging=PetDrag(0);
        if(sizing || dragging){Capture=true;InvalidateBitmap();}
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(sizing){Point p=System.Windows.Forms.Cursor.Position;double delta=((p.X-gestureOrigin.X)/360d+(p.Y-gestureOrigin.Y)/(double)logicalHeight)/2;SetZoom(gestureZoom+delta,false);}
        else if(dragging && PetDrag!=null)PetDrag(1);
        bool grip=Grip.Contains(PointerInCanvas(e));
        if(grip!=overGrip){overGrip=grip;InvalidateBitmap();}
        Cursor=sizing || grip?Cursors.SizeNWSE:dragging?Cursors.SizeAll:Cursors.Hand;
    }
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(overGrip){overGrip=false;InvalidateBitmap();}}
    private void EndGesture()
    {
        bool save=sizing,move=dragging;sizing=false;dragging=false;
        if(move && PetDrag!=null)PetDrag(2);if(save)SaveZoom();if(Capture)Capture=false;InvalidateBitmap();
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left)EndGesture();}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture && Interacting)EndGesture();}
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(!Visible && Interacting)EndGesture();}
    private static void DrawText(Graphics g,string text,Font font,Color color,RectangleF area,bool fitAll=false,int shadow=1)
    {
        using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,
            Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
        {
            var measured=g.MeasureString(text,font,new SizeF(10000,1000),format);
            float fit=Math.Min(1,Math.Min((area.Width-4)/Math.Max(1,measured.Width),area.Height/Math.Max(1,measured.Height)));
            Font drawFont=font;bool fitted=false;
            if(fit<.99f){drawFont=new Font(font.FontFamily,Math.Max(4,font.Size*(fitAll?fit*.97f:Math.Max(.65f,fit*.97f))),font.Style);fitted=true;}
            if(shadow>0)using(var dark=new SolidBrush(Color.FromArgb(110,95,95,95)))
                g.DrawString(text,drawFont,dark,new RectangleF(area.X+shadow,area.Y+shadow,area.Width,area.Height),format);
            using(var brush=new SolidBrush(color))g.DrawString(text,drawFont,brush,area,format);
            if(fitted)drawFont.Dispose();
        }
    }
    private static void DrawSlot(Graphics g,RectangleF r)
    {
        using(var fill=new SolidBrush(Color.FromArgb(255,139,139,139)))g.FillRectangle(fill,r);
        using(var dark=new Pen(Color.FromArgb(255,55,55,55),2f))
        {
            g.DrawLine(dark,r.Left,r.Top,r.Right,r.Top);
            g.DrawLine(dark,r.Left,r.Top,r.Left,r.Bottom);
        }
        using(var light=new Pen(Color.FromArgb(255,255,255,255),2f))
        {
            g.DrawLine(light,r.Left,r.Bottom,r.Right,r.Bottom);
            g.DrawLine(light,r.Right,r.Top,r.Right,r.Bottom);
        }
    }
    private static void DrawSeparator(Graphics g,float y,float width)
    {
        using(var dark=new Pen(Color.FromArgb(190,55,55,55),2f))g.DrawLine(dark,18,y,width-18,y);
        using(var light=new Pen(Color.FromArgb(220,255,255,255),1f))g.DrawLine(light,18,y+2,width-18,y+2);
    }
    internal Bitmap RenderFrame()
    {
        var bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(bitmap))
        {
            g.ScaleTransform((float)effectiveZoom,(float)effectiveZoom);
            int width=430,height=logicalHeight;
            int body=height-56;
            g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.None;g.TextRenderingHint=TextRenderingHint.SingleBitPerPixelGridFit;
            Color background=BubbleAppearance.ColorOf(Appearance.background),outline=BubbleAppearance.ColorOf(Appearance.outline),accent=BubbleAppearance.ColorOf(Appearance.accent),titleColor=BubbleAppearance.ColorOf(Appearance.title.color),dataColor=BubbleAppearance.ColorOf(Appearance.data.color),secondaryColor=BubbleAppearance.ColorOf(Appearance.secondary.color);
            DrawInventoryPanel(g,width,body,background,outline);
            DrawSeparator(g,usageY-12,width);
            DrawDot(g,new RectangleF(width*.68f,body+5,25,19));
            DrawDot(g,new RectangleF(width*.75f,body+34,12,10));
            var primary=Rows.FirstOrDefault();
            string title=primary==null?"正在处理任务…":primary.state=="waiting"?"等你回复或确认…":WorkState.Label(primary.state)+"…";
            DrawText(g,title,heading,titleColor,new RectangleF(38,26,width-76,39+titleExtra),true);
            if(primary!=null)
            {
                string identity=WorkState.Identity(primary);
                if(Now-primary.updated>TimeSpan.FromMinutes(15))identity=((int)(Now-primary.updated).TotalMinutes)+" 分钟无新事件 · "+identity;
                DrawText(g,identity,detail,secondaryColor,new RectangleF(46,70+titleExtra,width-92,21+identityExtra));
            }
            for(int i=1;i<Math.Min(3,Rows.Count);i++)
            {
                int y=94+titleExtra+identityExtra+(i-1)*rowStep;
                DrawText(g,WorkState.Label(Rows[i].state),stateFont,dataColor,new RectangleF(46,y,width-92,quotaHeight+2));
                DrawText(g,WorkState.Identity(Rows[i]),small,secondaryColor,new RectangleF(50,y+quotaHeight+3,width-100,metaHeight+1));
            }
            if(HasCodexUsage)
            {
                DrawQuota(g,"五小时",Quota==null?null:Quota.fiveHour,40,quotaY);
                DrawQuota(g,"一周",Quota==null?null:Quota.weekly,235,quotaY);
                DrawText(g,Daily==null?"Codex · 今日 token 读取中…":"Codex · "+Daily.Label,quotaFont,dataColor,new RectangleF(49,dailyY,width-98,quotaHeight+1),true);
                DrawText(g,"Codex · "+(Metrics==null?"均速 -- tok/s · 输出 --":Metrics.SpeedLabel(Now).Replace("本轮均速约","均速")),quotaFont,dataColor,new RectangleF(43,speedY,width-86,quotaHeight),true);
                DrawText(g,Metrics==null?"缓存命中 -- / -- · --":Metrics.CacheLabel.Replace("最近请求缓存","缓存命中"),small,secondaryColor,new RectangleF(48,cacheY,width-96,metaHeight+4),true);
            }
            if(HasQoderUsage)
            {
                DrawText(g,Qoder==null?"Qoder · Credits 读取中…":"Qoder · 今日 Credits "+QoderUsageSnapshot.Credits(Qoder.dailyCredits)+" · 上下文 "+QoderUsageSnapshot.Percent(Qoder.contextPercent),quotaFont,dataColor,new RectangleF(49,qoderY,width-98,quotaHeight+1),true);
                DrawText(g,Qoder==null?"Qoder · 本轮 Credits 读取中…":"Qoder · 本轮 "+QoderUsageSnapshot.Credits(Qoder.turnCredits)+" Credits · "+QoderUsageSnapshot.Speed(Qoder.turnCredits,Qoder.turnStartedUtc,Now),quotaFont,dataColor,new RectangleF(43,qoderTurnY,width-86,quotaHeight),true);
            }
            if(overGrip || sizing)using(var pen=new Pen(Color.FromArgb(200,outline),1.5f))
            {var r=Grip;g.DrawLine(pen,r.Left+5,r.Bottom-5,r.Right-5,r.Top+5);g.DrawLine(pen,r.Left+11,r.Bottom-5,r.Right-5,r.Top+11);}
        }
        return bitmap;
    }
    private void DrawInventoryPanel(Graphics g,int width,int body,Color background,Color outline)
    {
        var panel=new RectangleF(6,6,width-12,body-9);
        using(var shadow=new SolidBrush(Color.FromArgb(75,0,0,0)))g.FillRectangle(shadow,panel.X+3,panel.Y+4,panel.Width,panel.Height);
        using(var fill=new SolidBrush(Color.FromArgb(Appearance.backgroundAlpha,background)))g.FillRectangle(fill,panel);
        var glassState=g.Save();
        g.SetClip(panel);
        using(var grain=new SolidBrush(Color.FromArgb(10,255,255,255)))
        {
            int grainRow=0;
            for(float y=panel.Top+7;y<panel.Bottom-7;y+=2,grainRow++)
                for(float x=panel.Left+7+(grainRow%2);x<panel.Right-7;x+=2)
                    g.FillRectangle(grain,x,y,1,1);
        }
        using(var sheen=new LinearGradientBrush(new RectangleF(panel.Left,panel.Top,panel.Width,panel.Height*.52f),Color.FromArgb(85,255,255,255),Color.FromArgb(0,255,255,255),90f))g.FillRectangle(sheen,panel.Left+2,panel.Top+2,panel.Width-4,panel.Height*.52f);
        g.Restore(glassState);
        using(var light=new SolidBrush(Color.FromArgb(18,255,255,255)))
        {
            int row=0;
            for(float y=panel.Top+8;y<panel.Bottom-8;y+=6,row++)
                for(float x=panel.Left+8+(row%2)*3;x<panel.Right-8;x+=6)g.FillRectangle(light,x,y,3,3);
        }
        using(var dark=new SolidBrush(Color.FromArgb(9,0,0,0)))
            for(float y=panel.Top+12;y<panel.Bottom-12;y+=24)
                for(float x=panel.Left+18;x<panel.Right-18;x+=30)g.FillRectangle(dark,x,y,2,2);
        using(var pen=new Pen(outline,Appearance.borderWidth))g.DrawRectangle(pen,panel.X,panel.Y,panel.Width,panel.Height);
        using(var white=new Pen(Color.FromArgb(235,255,255,255),2f))
        {
            g.DrawLine(white,panel.Left+2,panel.Top+2,panel.Right-2,panel.Top+2);
            g.DrawLine(white,panel.Left+2,panel.Top+2,panel.Left+2,panel.Bottom-2);
        }
        using(var shade=new Pen(Color.FromArgb(235,139,139,139),2f))
        {
            g.DrawLine(shade,panel.Left+2,panel.Bottom-2,panel.Right-2,panel.Bottom-2);
            g.DrawLine(shade,panel.Right-2,panel.Top+2,panel.Right-2,panel.Bottom-2);
        }
        using(var inner=new Pen(Color.FromArgb(70,55,55,55),1f))g.DrawRectangle(inner,panel.X+5,panel.Y+5,panel.Width-10,panel.Height-10);
    }
    private void DrawDot(Graphics g,RectangleF rect)
    {
        using(var shadow=new SolidBrush(Color.FromArgb(80,0,0,0)))g.FillRectangle(shadow,rect.X+1.5f,rect.Y+2f,rect.Width,rect.Height);
        using(var fill=new SolidBrush(Color.FromArgb(Math.Min(255,Appearance.backgroundAlpha+25),BubbleAppearance.ColorOf(Appearance.background))))g.FillRectangle(fill,rect);
        using(var pen=new Pen(BubbleAppearance.ColorOf(Appearance.outline),2f))g.DrawRectangle(pen,rect.X,rect.Y,rect.Width,rect.Height);
        using(var white=new Pen(Color.FromArgb(235,255,255,255),1f))
        {
            g.DrawLine(white,rect.Left+1,rect.Top+1,rect.Right-1,rect.Top+1);
            g.DrawLine(white,rect.Left+1,rect.Top+1,rect.Left+1,rect.Bottom-1);
        }
        using(var shade=new Pen(Color.FromArgb(235,139,139,139),1f))
        {
            g.DrawLine(shade,rect.Left+1,rect.Bottom-1,rect.Right-1,rect.Bottom-1);
            g.DrawLine(shade,rect.Right-1,rect.Top+1,rect.Right-1,rect.Bottom-1);
        }
    }
    private void DrawQuota(Graphics g,string label,QuotaWindow window,int x,int y)
    {
        Color dataColor=BubbleAppearance.ColorOf(Appearance.data.color),accent=BubbleAppearance.ColorOf(Appearance.accent);
        DrawText(g,label+"剩余 "+(window==null?"未知":window.Percent(Now)),quotaFont,dataColor,new RectangleF(x,y,150,quotaHeight),true);
        float trackX=x+16,trackY=y+quotaHeight+3,trackW=112,trackH=10;
        DrawSlot(g,new RectangleF(trackX-2,trackY-2,trackW+4,trackH+4));
        if(window!=null && !window.Expired(Now))
        {
            double remaining=window.Remaining;
            Color fillColor=remaining<=0?Color.FromArgb(180,169,68,68):remaining<25?Color.FromArgb(200,169,68,68):accent;
            float fillWidth=Math.Max(0,(float)((trackW-4)*remaining/100));
            if(fillWidth>0)
            {
                using(var brush=new SolidBrush(fillColor))g.FillRectangle(brush,trackX+2,trackY+2,fillWidth,trackH-4);
                using(var shine=new SolidBrush(Color.FromArgb(80,255,255,255)))g.FillRectangle(shine,trackX+2,trackY+2,fillWidth,2);
            }
        }
        using(var tick=new Pen(Color.FromArgb(60,0,0,0),1f))
            for(int i=1;i<6;i++){float tx=trackX+trackW*i/6f;g.DrawLine(tick,tx,trackY+1,tx,trackY+trackH-1);}
        DrawText(g,window==null?"暂无数据":window.ResetLabel(Now),small,BubbleAppearance.ColorOf(Appearance.secondary.color),new RectangleF(x-1,y+quotaHeight+16,152,metaHeight));
    }
    internal void Present(Point position,byte alpha)
    {
        if(bitmapHandle==IntPtr.Zero)using(var bitmap=RenderFrame())bitmapHandle=bitmap.GetHbitmap(Color.FromArgb(0));
        if(!Visible)Show();Native.PresentBitmap(Handle,bitmapHandle,position,Size,alpha);PresentedAlpha=alpha;
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){EndGesture();if(appearanceDialog!=null)appearanceDialog.Close();if(ContextMenuStrip!=null)ContextMenuStrip.Dispose();if(bitmapHandle!=IntPtr.Zero)Native.DeleteObject(bitmapHandle);bitmapHandle=IntPtr.Zero;
            heading.Dispose();stateFont.Dispose();detail.Dispose();quotaFont.Dispose();small.Dispose();}
        base.Dispose(disposing);
    }
}
