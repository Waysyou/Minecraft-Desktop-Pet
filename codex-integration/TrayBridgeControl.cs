using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Hidden local command window: the native pet owns the one visible tray icon.
internal sealed class TrayBridgeControl:Form
{
    internal const string WindowTitle="Waysyou.MinecraftCodexBridge.Control.v1";
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string name);
    private static readonly uint ControlMessage=RegisterWindowMessage(WindowTitle);
    internal static bool RequestAppearance()
    {IntPtr window=Native.FindWindow(null,WindowTitle),result;return window!=IntPtr.Zero && Native.SendMessageTimeout(window,ControlMessage,new IntPtr(5),IntPtr.Zero,2,500,out result)!=IntPtr.Zero;}
    internal static bool RequestKeyStats()
    {IntPtr window=Native.FindWindow(null,WindowTitle),result;return window!=IntPtr.Zero && Native.SendMessageTimeout(window,ControlMessage,new IntPtr(6),IntPtr.Zero,2,500,out result)!=IntPtr.Zero;}
    internal Func<bool> BubbleEnabled;
    internal Action<int> Command;
    internal TrayBridgeControl(){Text=WindowTitle;ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;}
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x80|0x08000000;return p;}}
    protected override void WndProc(ref Message m)
    {
        if((uint)m.Msg==ControlMessage)
        {
            int command=m.WParam.ToInt32();
            if(command==0)m.Result=new IntPtr(1 | (BubbleEnabled!=null && BubbleEnabled()?2:0));
            else {if(command>=1 && command<=6 && Command!=null)Command(command);m.Result=new IntPtr(1);}
            return;
        }
        base.WndProc(ref m);
    }
}
