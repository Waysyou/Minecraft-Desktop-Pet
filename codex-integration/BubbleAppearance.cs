using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal sealed class BubbleTextStyle
{
    public string family;
    public float size;
    public bool bold;
    public string color;
}
internal sealed class BubbleAppearance
{
    private static readonly PrivateFontCollection bundledFonts=LoadBundledFonts();
    private static readonly FontFamily bundledFamily=SelectBundledFamily(bundledFonts);
    internal static readonly string PixelFamilyName=bundledFamily!=null?bundledFamily.Name:"Noto Sans SC";
    public BubbleTextStyle title=new BubbleTextStyle{family=PixelFamilyName,size=18,bold=false,color="#2F2F2F"};
    public BubbleTextStyle data=new BubbleTextStyle{family=PixelFamilyName,size=12,bold=false,color="#3F3F3F"};
    public BubbleTextStyle secondary=new BubbleTextStyle{family=PixelFamilyName,size=12,bold=false,color="#5F5F5F"};
    public string background="#C6C6C6",outline="#373737",accent="#3E8E3E";
    public float borderWidth=3;
    public int backgroundAlpha=175;
    private static readonly string[] fonts=LoadFonts();
    private static FontFamily SelectBundledFamily(PrivateFontCollection collection)
    {
        try
        {
            foreach(var family in collection.Families)
                if(family.Name.IndexOf("Unifont",StringComparison.OrdinalIgnoreCase)>=0)return family;
            return collection.Families.Length>0?collection.Families[0]:null;
        }
        catch(ArgumentException){return null;}
    }
    internal static FontFamily FindBundled(string name)
    {
        try{foreach(var family in bundledFonts.Families)if(String.Equals(family.Name,name,StringComparison.Ordinal))return family;}catch(ArgumentException){}
        return null;
    }
    private static PrivateFontCollection LoadBundledFonts()
    {
        var collection=new PrivateFontCollection();
        try
        {
            string root=Path.GetDirectoryName(typeof(BubbleAppearance).Assembly.Location);
            if(String.IsNullOrEmpty(root))root=AppDomain.CurrentDomain.BaseDirectory;
            string directory=Path.Combine(root,"fonts");
            if(Directory.Exists(directory))
                foreach(string file in Directory.GetFiles(directory,"*.*"))
                {
                    string extension=Path.GetExtension(file).ToLowerInvariant();
                    if(extension!=".ttf" && extension!=".otf")continue;
                    try{collection.AddFontFile(file);}catch(ArgumentException){}catch(IOException){}
                }
        }
        catch(Exception){}
        return collection;
    }
    private static string[] LoadFonts()
    {
        var names=new List<string>();
        using(var installed=new InstalledFontCollection())names.AddRange(installed.Families.Select(x=>x.Name));
        try{foreach(var family in bundledFonts.Families)names.Add(family.Name);}catch(ArgumentException){}
        return names.Distinct().OrderBy(x=>x).ToArray();
    }
    internal static string[] Fonts{get{return fonts;}}
    internal static Color ColorOf(string text){return ColorTranslator.FromHtml(text);}
    private static string ValidColor(string value,string fallback){return value!=null && Regex.IsMatch(value,"^#[0-9A-Fa-f]{6}$")?value.ToUpperInvariant():fallback;}
    private static float Limit(float value,float min,float max,float fallback){return Single.IsNaN(value)||Single.IsInfinity(value)?fallback:Math.Max(min,Math.Min(max,value));}
    private static BubbleTextStyle Clean(BubbleTextStyle value,BubbleTextStyle fallback,float min,float max)
    {
        value=value??fallback;return new BubbleTextStyle{family=fonts.Contains(value.family)?value.family:"Microsoft YaHei UI",size=Limit(value.size,min,max,fallback.size),bold=value.bold,color=ValidColor(value.color,fallback.color)};
    }
    internal BubbleAppearance Normalize()
    {
        var d=new BubbleAppearance();return new BubbleAppearance{title=Clean(title,d.title,16,32),data=Clean(data,d.data,10,18),secondary=Clean(secondary,d.secondary,9,14),background=ValidColor(background,d.background),outline=ValidColor(outline,d.outline),accent=ValidColor(accent,d.accent),borderWidth=Limit(borderWidth,1,7,5),backgroundAlpha=Math.Max(100,Math.Min(255,backgroundAlpha))};
    }
    internal BubbleAppearance Copy(){return Wire.Json().Deserialize<BubbleAppearance>(Wire.Json().Serialize(this));}
    internal static Font MakeFont(BubbleTextStyle style,float offset=0)
    {
        FontStyle weight=style.bold?FontStyle.Bold:FontStyle.Regular;
        FontFamily bundled=FindBundled(style.family);
        if(bundled!=null)
        {
            if(!bundled.IsStyleAvailable(weight))weight=FontStyle.Regular;
            return new Font(bundled,style.size+offset,weight,GraphicsUnit.Point);
        }
        using(var family=new FontFamily(style.family))
        {
            if(!family.IsStyleAvailable(weight))weight=family.IsStyleAvailable(FontStyle.Regular)?FontStyle.Regular:FontStyle.Bold;
            return new Font(family,style.size+offset,weight,GraphicsUnit.Point);
        }
    }
}

internal sealed class BubbleAppearanceDialog:Form
{
    private readonly StatusBubble target,preview;
    private BubbleAppearance draft;
    private readonly Panel canvas=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(235,238,236)};
    private readonly List<Action> refreshers=new List<Action>();
    private bool refreshing;
    internal BubbleAppearanceDialog(StatusBubble target)
    {
        this.target=target;draft=target.Appearance.Copy();preview=new StatusBubble("");
        Text="Minecraft · 气泡与按键设置";Font=new Font("Microsoft YaHei UI",9);AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(890,620);MinimumSize=new Size(850,650);StartPosition=FormStartPosition.CenterScreen;BackColor=Color.White;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Padding=new Padding(20)};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,57));root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,43));root.RowStyles.Add(new RowStyle(SizeType.Absolute,62));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,48));Controls.Add(root);
        var header=new Label{Text="气泡外观",Dock=DockStyle.Fill,Font=new Font(Font,FontStyle.Bold),AutoSize=false};header.Font=new Font("Microsoft YaHei UI",17,FontStyle.Bold);root.Controls.Add(header,0,0);
        root.Controls.Add(new Label{Text="预览 · 示例数据",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Color.DimGray},1,0);
        var controls=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(0,0,12,0)};root.Controls.Add(controls,0,1);
        AddTextGroup(controls,"标题",()=>draft.title,16,32);AddTextGroup(controls,"数据",()=>draft.data,10,18);AddTextGroup(controls,"辅助文字",()=>draft.secondary,9,14);
        var colors=new GroupBox{Text="气泡",Size=new Size(450,112),Margin=new Padding(0,5,0,0)};controls.Controls.Add(colors);
        AddColor(colors,"底色",12,24,()=>draft.background,x=>draft.background=x);AddColor(colors,"描边",155,24,()=>draft.outline,x=>draft.outline=x);AddColor(colors,"进度条",298,24,()=>draft.accent,x=>draft.accent=x);
        colors.Controls.Add(new Label{Text="描边粗细",Location=new Point(12,76),AutoSize=true});var border=new NumericUpDown{Minimum=1,Maximum=7,DecimalPlaces=1,Increment=.5m,Location=new Point(85,72),Width=65};colors.Controls.Add(border);
        border.ValueChanged+=(s,e)=>{if(!refreshing){draft.borderWidth=(float)border.Value;RefreshPreview();}};refreshers.Add(()=>border.Value=(decimal)draft.borderWidth);
        colors.Controls.Add(new Label{Text="不透明度",Location=new Point(180,76),AutoSize=true});var alpha=new NumericUpDown{Minimum=100,Maximum=255,Increment=5,Location=new Point(252,72),Width=65};colors.Controls.Add(alpha);
        alpha.ValueChanged+=(s,e)=>{if(!refreshing){draft.backgroundAlpha=(int)alpha.Value;RefreshPreview();}};refreshers.Add(()=>alpha.Value=draft.backgroundAlpha);
        canvas.Paint+=(s,e)=>{using(var bitmap=preview.RenderFrame()){float scale=Math.Min((canvas.Width-24)/(float)bitmap.Width,(canvas.Height-24)/(float)bitmap.Height);int w=(int)(bitmap.Width*scale),h=(int)(bitmap.Height*scale);e.Graphics.DrawImage(bitmap,(canvas.Width-w)/2,(canvas.Height-h)/2,w,h);}};
        canvas.Resize+=(s,e)=>canvas.Invalidate();root.Controls.Add(canvas,1,1);
        var restore=new Button{Text="恢复默认",Size=new Size(110,32),Anchor=AnchorStyles.Left};restore.Click+=(s,e)=>{draft=new BubbleAppearance().Normalize();RefreshControls();};
        var stats=new Button{Text="按键统计…",Size=new Size(118,32),Margin=new Padding(8,0,0,0)};stats.Click+=(s,e)=>KeyStatsForm.OpenShared();
        var leftActions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.LeftToRight,Padding=new Padding(0,8,0,0)};leftActions.Controls.Add(restore);leftActions.Controls.Add(stats);root.Controls.Add(leftActions,0,2);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,8,0,0)};root.Controls.Add(actions,1,2);
        var save=new Button{Text="保存并应用",Width=118,Height=32};var cancel=new Button{Text="取消",Width=80,Height=32,DialogResult=DialogResult.Cancel};actions.Controls.Add(save);actions.Controls.Add(cancel);AcceptButton=save;CancelButton=cancel;
        save.Click+=(s,e)=>{if(target.SaveAppearance(draft)){DialogResult=DialogResult.OK;Close();}else MessageBox.Show(this,"设置未能保存，请检查配置文件是否被其他程序占用。","保存失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);};
        var now=DateTime.UtcNow;preview.Quota=new QuotaSnapshot{observedUtc=now,fiveHour=new QuotaWindow{usedPercent=22,windowMinutes=300},weekly=new QuotaWindow{usedPercent=4,windowMinutes=10080}};
        preview.Daily=new DailyTokenSnapshot{date=now.ToLocalTime().ToString("yyyy-MM-dd"),source="local",tokens=18785860,observedUtc=now};preview.Metrics=new RequestMetrics{startedUtc=now.AddMinutes(-3),observedUtc=now,outputTokens=15670,inputTokens=142570,cachedInputTokens=142080};
        preview.Qoder=new QoderUsageSnapshot{observedUtc=now,dailyCredits=12.84,turnCredits=3.62,contextPercent=28.4,session="preview",turnStartedUtc=now.AddMinutes(-12)};
        preview.UpdateRows(new List<WorkItem>{new WorkItem{session="583575",workspace="我的软件项目",state="working",source="qoder",updated=now}},now);RefreshControls();
    }
    private void AddTextGroup(Control parent,string title,Func<BubbleTextStyle> style,int min,int max)
    {
        var group=new GroupBox{Text=title,Size=new Size(450,105),Margin=new Padding(0,0,0,6)};parent.Controls.Add(group);
        var family=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(12,24),Width=422,MaxDropDownItems=12};family.Items.AddRange(BubbleAppearance.Fonts);group.Controls.Add(family);
        group.Controls.Add(new Label{Text="字号",Location=new Point(12,69),AutoSize=true});var size=new NumericUpDown{Minimum=min,Maximum=max,DecimalPlaces=1,Increment=.5m,Location=new Point(50,65),Width=65};group.Controls.Add(size);
        var bold=new CheckBox{Text="加粗",Location=new Point(136,65),AutoSize=true};group.Controls.Add(bold);AddColor(group,"字色",236,65,()=>style().color,x=>style().color=x);
        family.SelectedIndexChanged+=(s,e)=>{if(!refreshing && family.SelectedItem!=null){style().family=(string)family.SelectedItem;RefreshPreview();}};size.ValueChanged+=(s,e)=>{if(!refreshing){style().size=(float)size.Value;RefreshPreview();}};bold.CheckedChanged+=(s,e)=>{if(!refreshing){style().bold=bold.Checked;RefreshPreview();}};
        refreshers.Add(()=>{family.SelectedItem=style().family;size.Value=(decimal)style().size;bold.Checked=style().bold;});
    }
    private void AddColor(Control parent,string label,int x,int y,Func<string> get,Action<string> set)
    {
        parent.Controls.Add(new Label{Text=label,Location=new Point(x,y+5),AutoSize=true});var button=new Button{Location=new Point(x+50,y),Size=new Size(86,27),FlatStyle=FlatStyle.Flat};parent.Controls.Add(button);
        Action update=()=>{button.Text=get();button.BackColor=BubbleAppearance.ColorOf(get());button.ForeColor=button.BackColor.GetBrightness()>.5?Color.Black:Color.White;};refreshers.Add(update);
        button.Click+=(s,e)=>{using(var dialog=new ColorDialog{Color=BubbleAppearance.ColorOf(get()),FullOpen=true})if(dialog.ShowDialog(this)==DialogResult.OK){set("#"+dialog.Color.R.ToString("X2")+dialog.Color.G.ToString("X2")+dialog.Color.B.ToString("X2"));update();RefreshPreview();}};
    }
    private void RefreshControls(){refreshing=true;foreach(var refresh in refreshers)refresh();refreshing=false;RefreshPreview();}
    private void RefreshPreview(){preview.ApplyAppearance(draft);canvas.Invalidate();}
    protected override void Dispose(bool disposing){if(disposing){preview.Dispose();Font.Dispose();}base.Dispose(disposing);}
}
