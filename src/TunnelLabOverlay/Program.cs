using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TunnelLab.TokenOverlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var mutex=new Mutex(true,"Local\\TunnelLabTokenOverlayV1",out var first);
        if(!first)return;
        Application.Run(new OverlayController());
        GC.KeepAlive(mutex);
    }
}

internal sealed class OverlayController : ApplicationContext
{
    private readonly OverlayForm _form=new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ContextMenuStrip _menu=new();
    private readonly Dictionary<string,ToolStripMenuItem> _choices=new();
    private SourceKind? _override;
    private SourceKind? _active;
    private UsageSnapshot? _snapshot;
    private DateTime _lastRead=DateTime.MinValue;
    private int _busy;
    private bool _hidden;
    private bool _bottom;

    public OverlayController()
    {
        _form.ExitClicked+=(_,_)=>Quit();
        var source=new ToolStripMenuItem("Source");
        foreach(var name in new[]{"Auto","Codex","Claude","OpenCode"})
        {
            var item=new ToolStripMenuItem(name){CheckOnClick=false};
            item.Click+=(_,_)=>{
                _override=name=="Auto"?null:Enum.Parse<SourceKind>(name);
                _active=null;_snapshot=null;_lastRead=DateTime.MinValue;
                SyncMenu();};
            _choices[name]=item;source.DropDownItems.Add(item);
        }
        _menu.Items.Add(source);
        var position=new ToolStripMenuItem("Position");
        var top=new ToolStripMenuItem("Top-right") {Checked=true};
        var bottom=new ToolStripMenuItem("Bottom-right");
        top.Click+=(_,_)=>{_bottom=false;top.Checked=true;bottom.Checked=false;};
        bottom.Click+=(_,_)=>{_bottom=true;top.Checked=false;bottom.Checked=true;};
        position.DropDownItems.Add(top);position.DropDownItems.Add(bottom);
        _menu.Items.Add(position);
        var toggle=new ToolStripMenuItem("Hide / Show");
        toggle.Click+=(_,_)=>{_hidden=!_hidden;if(_hidden)_form.Hide();};
        _menu.Items.Add(toggle);
        _menu.Items.Add(new ToolStripSeparator());
        var quit=new ToolStripMenuItem("Exit TokenOverlay");
        quit.Click+=(_,_)=>Quit();
        _menu.Items.Add(quit);
        _tray=new NotifyIcon{
            Text="TunnelLab TokenOverlay (local only)",
            Icon=SystemIcons.Application,
            ContextMenuStrip=_menu,
            Visible=true
        };
        _tray.DoubleClick+=(_,_)=>{_hidden=!_hidden;if(_hidden)_form.Hide();};
        SyncMenu();
        _timer=new System.Windows.Forms.Timer {Interval=700};
        _timer.Tick+=(_,_)=>Tick();
        _timer.Start();
        Tick();
    }
    private void SyncMenu()
    {
        foreach(var (name,item) in _choices)
            item.Checked=name==(_override?.ToString()??"Auto");
    }
    private void Tick()
    {
        if(_hidden)return;
        var hwnd=Native.GetForegroundWindow();
        var kind=Native.SourceForWindow(hwnd);
        var source=_override??kind;
        if(source is null || !Native.TryBounds(hwnd,out var rect))
        {
            _form.Hide();
            return;
        }
        if(source!=_active)
        {
            _active=source;
            _snapshot=null;
            _lastRead=DateTime.MinValue;
        }
        var right=rect.Right;
        var top=_bottom?rect.Bottom-_form.Height-18:rect.Top+55;
        var left=right-_form.Width-22;
        var display=Screen.FromHandle(hwnd).WorkingArea;
        _form.Location=new Point(
            Math.Clamp(left,display.Left,Math.Max(display.Left,display.Right-_form.Width)),
            Math.Clamp(top,display.Top,Math.Max(display.Top,display.Bottom-_form.Height)));
        _form.SetData(_snapshot,source.Value);
        if(!_form.Visible)_form.Show();
        if(DateTime.UtcNow-_lastRead>TimeSpan.FromSeconds(2.5)
           &&Interlocked.CompareExchange(ref _busy,1,0)==0)
        {
            _lastRead=DateTime.UtcNow;
            var polling=source.Value;
            _=Task.Run(()=>
            {
                UsageSnapshot? snap=null;
                try { snap=polling==SourceKind.OpenCode?OpenCodeReader.Read():TranscriptReader.Read(polling); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException){}
                return snap;
            }).ContinueWith(task=>
            {
                try
                {
                    if(task.IsCompletedSuccessfully&&_active==polling&&!_form.IsDisposed)
                    { _snapshot=task.Result;_form.SetData(_snapshot,polling); }
                }
                finally { Interlocked.Exchange(ref _busy,0); }
            },TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
    private void Quit()
    {
        _timer.Stop();
        _tray.Visible=false;
        _tray.Dispose();
        _menu.Dispose();
        _form.Close();_form.Dispose();
        ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){_timer.Dispose();_tray.Dispose();_menu.Dispose();_form.Dispose();}
        base.Dispose(disposing);
    }
}

internal static class Native
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect {public int Left,Top,Right,Bottom;}
    internal static bool TryBounds(IntPtr hwnd,out Rectangle bounds)
    {
        bounds=default;
        if(hwnd==IntPtr.Zero||IsIconic(hwnd)||!GetWindowRect(hwnd,out var r))return false;
        var w=r.Right-r.Left;var h=r.Bottom-r.Top;
        if(w<400||h<240)return false;
        bounds=Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
        return true;
    }
    internal static SourceKind? SourceForWindow(IntPtr hwnd)
    {
        if(!TryBounds(hwnd,out _))return null;
        GetWindowThreadProcessId(hwnd,out var id);
        if(id==0)return null;
        try
        {
            using var process=Process.GetProcessById((int)id);
            var name=process.ProcessName.ToLowerInvariant();
            var title=process.MainWindowTitle.ToLowerInvariant();
            if(name.Contains("opencode"))return SourceKind.OpenCode;
            if(name=="claude"||name.Contains("claude-desktop"))return SourceKind.Claude;
            if(name.Contains("codex"))return SourceKind.Codex;
            // Store-installed Codex builds can run inside a ChatGPT-named executable.
            if(name=="chatgpt")
            {
                try
                {
                    var filename=process.MainModule?.FileName?.ToLowerInvariant()??"";
                    if(filename.Contains("codex"))return SourceKind.Codex;
                }
                catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){}
                if(title.Contains("codex"))return SourceKind.Codex;
            }
            return null;
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception){return null;}
    }
}
