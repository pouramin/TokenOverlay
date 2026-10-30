using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TunnelLab.TokenOverlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var single=new Mutex(true,"Local\\TunnelLabTokenOverlayV1",out var created);
        if(!created)return;
        Application.Run(new OverlayController());
        GC.KeepAlive(single);
    }
}

internal sealed class OverlayController:ApplicationContext
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
    private bool _bottom=true; // Default away from app title bars and toolbar icons.
    private int _offsetX,_offsetY;
    private Rectangle _lastWindow;
    private string? _windowTitle;
    private string? _selectedOpenCodeSession;
    private readonly ToolStripMenuItem _sessionChooser = new("OpenCode session");

    private sealed class Preferences
    {
        public bool Bottom {get;set;}=true;
        public int OffsetX {get;set;}
        public int OffsetY {get;set;}
    }
    private static string PreferencesFile=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelLab","TokenOverlay","settings.json");
    private void LoadPreferences()
    {
        try
        {
            if(!File.Exists(PreferencesFile))return;
            var p=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesFile));
            if(p is null)return;
            _bottom=p.Bottom;
            _offsetX=Math.Clamp(p.OffsetX,-2400,2400);
            _offsetY=Math.Clamp(p.OffsetY,-1800,1800);
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException){}
    }
    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFile)!);
            File.WriteAllText(PreferencesFile,JsonSerializer.Serialize(new Preferences{
                Bottom=_bottom,OffsetX=_offsetX,OffsetY=_offsetY
            }));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
    }

    public OverlayController()
    {
        LoadPreferences();
        _form.ExitClicked+=(_,_)=>Quit();
        _form.DragCompleted+=(_,_)=>SaveDraggedPosition();
        _form.ExpandedChanged+=(_,_)=>PositionOverlay();
        var sources=new ToolStripMenuItem("Source");
        foreach(var name in new[]{"Auto","Codex","Claude","OpenCode"})
        {
            var item=new ToolStripMenuItem(name);
            item.Click+=(_,_)=>{
                _override=name=="Auto"?null:Enum.Parse<SourceKind>(name);
                _active=null;_snapshot=null;_lastRead=DateTime.MinValue;
                SyncMenu();
            };
            _choices[name]=item;
            sources.DropDownItems.Add(item);
        }
        _menu.Items.Add(sources);
        _sessionChooser.DropDownOpening+=(_,_)=>RefreshOpenCodeSessions();
        _menu.Items.Add(_sessionChooser);
        var pos=new ToolStripMenuItem("Position");
        var top=new ToolStripMenuItem("Top-right");
        var bottom=new ToolStripMenuItem("Bottom-right (default)");
        top.Checked=!_bottom;
        bottom.Checked=_bottom;
        top.Click+=(_,_)=>{_bottom=false;_offsetX=0;_offsetY=0;top.Checked=true;bottom.Checked=false;SavePreferences();};
        bottom.Click+=(_,_)=>{_bottom=true;_offsetX=0;_offsetY=0;bottom.Checked=true;top.Checked=false;SavePreferences();};
        pos.DropDownItems.Add(top);
        pos.DropDownItems.Add(bottom);
        var reset=new ToolStripMenuItem("Reset floating position");
        reset.Click+=(_,_)=>{_offsetX=0;_offsetY=0;SavePreferences();};
        pos.DropDownItems.Add(reset);
        _menu.Items.Add(pos);
        _menu.Items.Add(new ToolStripMenuItem("Show / hide",null,(_,_)=>{
            _hidden=!_hidden;if(_hidden)_form.Hide();
        }));
        _menu.Items.Add(new ToolStripMenuItem("Claude diagnostics",null,(_,_)=>
        {
            var report=ClaudeDesktopReader.Diagnostics();
            MessageBox.Show(report,"Claude local session diagnostics",
                MessageBoxButtons.OK,MessageBoxIcon.Information);
        }));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit TokenOverlay",null,(_,_)=>Quit()));
        _tray=new NotifyIcon{
            Text="TunnelLab TokenOverlay",
            Icon=SystemIcons.Application,
            Visible=true,ContextMenuStrip=_menu
        };
        _tray.DoubleClick+=(_,_)=>{_hidden=!_hidden;if(_hidden)_form.Hide();};
        SyncMenu();
        _timer=new System.Windows.Forms.Timer{Interval=600};
        _timer.Tick+=(_,_)=>Tick();
        _timer.Start();
        Tick();
    }

    private void RefreshOpenCodeSessions()
    {
        _sessionChooser.DropDownItems.Clear();
        var automatic=new ToolStripMenuItem("Auto (latest / title match)")
            {Checked=_selectedOpenCodeSession is null};
        automatic.Click+=(_,_)=>{
            _selectedOpenCodeSession=null;
            _snapshot=null;
            _lastRead=DateTime.MinValue;
        };
        _sessionChooser.DropDownItems.Add(automatic);
        foreach(var (id,title) in OpenCodeReader.RecentSessions())
        {
            var display=title.Length>52?title[..49]+"...":title;
            var captured=id;
            var choice=new ToolStripMenuItem(display){Checked=_selectedOpenCodeSession==id};
            choice.Click+=(_,_)=>{
                _selectedOpenCodeSession=captured;
                _snapshot=null;
                _lastRead=DateTime.MinValue;
            };
            _sessionChooser.DropDownItems.Add(choice);
        }
        if(_sessionChooser.DropDownItems.Count==1)
            _sessionChooser.DropDownItems.Add(new ToolStripMenuItem("No local sessions found"){Enabled=false});
    }

    private void SyncMenu()
    {
        foreach(var (name,menu) in _choices)
            menu.Checked=name==(_override?.ToString()??"Auto");
    }
    private Point DefaultPosition(Rectangle host)
    {
        var monitor=Screen.FromRectangle(host).WorkingArea;
        var x=host.Right-_form.Width-15;
        var y=_bottom?host.Bottom-_form.Height-15:host.Top+55;
        x=Math.Clamp(x,monitor.Left,Math.Max(monitor.Left,monitor.Right-_form.Width));
        y=Math.Clamp(y,monitor.Top,Math.Max(monitor.Top,monitor.Bottom-_form.Height));
        return new Point(x,y);
    }
    private void PositionOverlay()
    {
        if(_lastWindow.IsEmpty || _form.IsDragging || _form.IsPointerDown)return;
        var p=DefaultPosition(_lastWindow);
        var monitor=Screen.FromRectangle(_lastWindow).WorkingArea;
        _form.Location=new Point(
            Math.Clamp(p.X+_offsetX,monitor.Left,Math.Max(monitor.Left,monitor.Right-_form.Width)),
            Math.Clamp(p.Y+_offsetY,monitor.Top,Math.Max(monitor.Top,monitor.Bottom-_form.Height)));
    }

    private void SaveDraggedPosition()
    {
        if(_lastWindow.IsEmpty)return;
        var normal=DefaultPosition(_lastWindow);
        _offsetX=Math.Clamp(_form.Left-normal.X,-2400,2400);
        _offsetY=Math.Clamp(_form.Top-normal.Y,-1800,1800);
        SavePreferences();
    }

    private void Tick()
    {
        if(_hidden)return;
        var hwnd=Native.GetForegroundWindow();
        var actualSource=Native.SourceForWindow(hwnd);
        var source=_override??actualSource;
        if(source is null || !Native.TryBounds(hwnd,out var host))
        {
            _form.Hide();
            return;
        }
        // In auto mode only show over a recognized app.
        if(_override is null && actualSource!=source)
        {
            _form.Hide();
            return;
        }
        _lastWindow=host;
        _windowTitle=Native.WindowTitle(hwnd);
        if(source!=_active)
        {
            _active=source;_snapshot=null;_lastRead=DateTime.MinValue;
        }
        PositionOverlay();
        _form.SetData(_snapshot,source.Value);
        if(!_form.Visible)_form.Show();
        if(DateTime.UtcNow-_lastRead<TimeSpan.FromSeconds(2.5)
            || Interlocked.CompareExchange(ref _busy,1,0)!=0)return;
        _lastRead=DateTime.UtcNow;
        var capturedSource=source.Value;
        var capturedTitle=_windowTitle;
        _=Task.Run(()=>{
            try
            {
                return capturedSource==SourceKind.OpenCode
                    ?OpenCodeReader.Read(windowTitle:capturedTitle,
                        selectedSessionId:_selectedOpenCodeSession)
                    :TranscriptReader.Read(capturedSource,capturedTitle);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
            {return null;}
        }).ContinueWith(task=>{
            try
            {
                if(task.IsCompletedSuccessfully && _active==capturedSource && !_form.IsDisposed)
                {
                    _snapshot=task.Result;
                    _form.SetData(_snapshot,capturedSource);
                }
            }
            finally{Interlocked.Exchange(ref _busy,0);}
        },TaskScheduler.FromCurrentSynchronizationContext());
    }
    private void Quit()
    {
        _timer.Stop();
        _tray.Visible=false;
        _tray.Dispose();
        _menu.Dispose();
        _form.Close();
        _form.Dispose();
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
    [DllImport("user32.dll")]internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out Rect bounds);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(
        IntPtr h,System.Text.StringBuilder text,int max);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect{public int Left,Top,Right,Bottom;}
    internal static string WindowTitle(IntPtr h)
    {
        var value=new System.Text.StringBuilder(512);
        return GetWindowText(h,value,value.Capacity)>0?value.ToString():"";
    }
    internal static bool TryBounds(IntPtr h,out Rectangle result)
    {
        result=default;
        if(h==IntPtr.Zero||IsIconic(h)||!GetWindowRect(h,out var b))return false;
        if(b.Right-b.Left<350||b.Bottom-b.Top<220)return false;
        result=Rectangle.FromLTRB(b.Left,b.Top,b.Right,b.Bottom);
        return true;
    }
    internal static SourceKind? SourceForWindow(IntPtr h)
    {
        if(!TryBounds(h,out _))return null;
        GetWindowThreadProcessId(h,out var pid);
        if(pid==0)return null;
        try
        {
            using var process=Process.GetProcessById((int)pid);
            var name=process.ProcessName.ToLowerInvariant();
            var title=WindowTitle(h).ToLowerInvariant();
            if(name.Contains("opencode"))return SourceKind.OpenCode;
            if(name=="claude"||name.Contains("claude-desktop"))return SourceKind.Claude;
            if(name.Contains("codex"))return SourceKind.Codex;
            if(name=="chatgpt")
            {
                try
                {
                    var filename=process.MainModule?.FileName?.ToLowerInvariant()??"";
                    if(filename.Contains("codex"))return SourceKind.Codex;
                }
                catch(Exception ex) when(ex is System.ComponentModel.Win32Exception
                    or InvalidOperationException){}
                if(title.Contains("codex"))return SourceKind.Codex;
            }
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception){}
        return null;
    }
}
