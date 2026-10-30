using System.Drawing.Drawing2D;

namespace TunnelLab.TokenOverlay;

/// <summary>Small independent overlay. Drag its header to float above any supported app.</summary>
internal sealed class OverlayForm : Form
{
    private const int CompactHeight=44;
    private const int ExpandedHeight=450;
    private readonly Panel _header=new(){BackColor=Color.FromArgb(30,34,45)};
    private readonly Label _summary=new(){ForeColor=Color.White,BackColor=Color.Transparent,
        Font=new Font("Segoe UI Semibold",9.7f),AutoEllipsis=true};
    private readonly Button _close=new(){Text="×",Font=new Font("Segoe UI",15f),
        FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(30,34,45),
        ForeColor=Color.FromArgb(212,217,227),TabStop=false,Cursor=Cursors.Hand};
    private readonly Label _caption=new(){ForeColor=Color.FromArgb(163,172,190),
        Font=new Font("Segoe UI",8.6f),AutoEllipsis=true,BackColor=Color.Transparent};
    private readonly Panel _rows=new(){BackColor=Color.FromArgb(30,34,45)};
    private readonly Label _footer=new(){BackColor=Color.Transparent,
        ForeColor=Color.FromArgb(164,173,192),Font=new Font("Segoe UI",8f),
        AutoEllipsis=true};
    private readonly List<(Label Key,Label Value)> _fields=new();
    private readonly ToolTip _tip=new();
    private bool _expanded;
    private bool _dragging;
    private Point _dragStart;
    private Point _formStart;
    public event EventHandler? ExitClicked;
    public event EventHandler? DragCompleted;
    public bool Expanded=>_expanded;

    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p=base.CreateParams;
            p.ExStyle|=0x08000000|0x00000080;
            return p;
        }
    }

    public OverlayForm()
    {
        AutoScaleMode=AutoScaleMode.None;
        FormBorderStyle=FormBorderStyle.None;
        StartPosition=FormStartPosition.Manual;
        ShowInTaskbar=false;
        TopMost=true;
        DoubleBuffered=true;
        BackColor=Color.FromArgb(30,34,45);
        ForeColor=Color.White;
        Opacity=0.97;
        Width=302;
        Height=CompactHeight;

        _header.SetBounds(0,0,302,CompactHeight);
        _header.Cursor=Cursors.SizeAll;
        _summary.SetBounds(12,10,253,25);
        _summary.Cursor=Cursors.SizeAll;
        _close.SetBounds(270,6,27,30);
        _close.FlatAppearance.BorderSize=0;
        _close.FlatAppearance.MouseOverBackColor=Color.FromArgb(100,44,51);
        _close.Click+=(_,_)=>ExitClicked?.Invoke(this,EventArgs.Empty);

        _caption.SetBounds(13,53,275,24);
        _rows.SetBounds(13,85,276,316);
        _footer.SetBounds(13,404,276,39);

        Controls.Add(_header);
        _header.Controls.Add(_summary);
        _header.Controls.Add(_close);
        Controls.Add(_caption);
        Controls.Add(_rows);
        Controls.Add(_footer);

        for(var i=0;i<11;i++)
        {
            var key=new Label{ForeColor=Color.FromArgb(151,161,181),
                Font=new Font("Segoe UI",8.7f),Left=0,Top=i*28,
                Width=126,Height=24,AutoEllipsis=true};
            var value=new Label{ForeColor=Color.White,
                Font=new Font("Segoe UI Semibold",9.1f),
                TextAlign=ContentAlignment.MiddleRight,Left=131,Top=i*28,
                Width=143,Height=24,AutoEllipsis=true};
            _fields.Add((key,value));
            _rows.Controls.Add(key);_rows.Controls.Add(value);
        }
        foreach(Control control in new Control[]{_header,_summary})
        {
            control.MouseDown+=HeaderDown;
            control.MouseMove+=HeaderMove;
            control.MouseUp+=HeaderUp;
        }
        _tip.SetToolTip(_close,"Close TokenOverlay");
        _tip.SetToolTip(_summary,"Drag to reposition · click for details");
        _tip.SetToolTip(_header,"Drag to reposition · click for details");
        RefreshLayout();
    }

    private void HeaderDown(object? sender,MouseEventArgs e)
    {
        if(e.Button!=MouseButtons.Left)return;
        _dragStart=Control.MousePosition;
        _formStart=Location;
        _dragging=false;
    }
    private void HeaderMove(object? sender,MouseEventArgs e)
    {
        if((Control.MouseButtons&MouseButtons.Left)==0)return;
        var delta=new Size(Control.MousePosition.X-_dragStart.X,
            Control.MousePosition.Y-_dragStart.Y);
        if(!_dragging && (Math.Abs(delta.Width)>6 || Math.Abs(delta.Height)>6))
            _dragging=true;
        if(_dragging)
            Location=_formStart+delta;
    }
    private void HeaderUp(object? sender,MouseEventArgs e)
    {
        if(e.Button!=MouseButtons.Left)return;
        if(_dragging)
        {
            _dragging=false;
            DragCompleted?.Invoke(this,EventArgs.Empty);
        }
        else Toggle();
    }

    public void SetData(UsageSnapshot? data,SourceKind source)
    {
        var name=source.ToString();
        _summary.Text=data is null
            ? $"{name}  ·  No data"
            : $"{name}  ·  {UsageSnapshot.FormatCount(data.Total)} tokens  ·  {data.TimeText}";
        _caption.Text=data is null
            ? "No matching local session — open a project"
            : $"{name}  /  {Ellipsis(data.SessionName,37)}";
        if(data is null)
        {
            foreach(var (key,value) in _fields){key.Text="";value.Text="";}
            _footer.Text=source==SourceKind.Claude
                ? "Claude Desktop cloud chats are not local Claude Code logs."
                : "Source not found or no usable token records.";
            return;
        }

        var fields=new (string,string)[]
        {
            ("Total consumed",UsageSnapshot.FormatCount(data.Total)),
            ("Main session",data.Source==SourceKind.OpenCode?
                UsageSnapshot.FormatCount(data.MainSessionTokens):"—"),
            ("Child agents",data.Source==SourceKind.OpenCode?data.ChildSessions.ToString():"—"),
            ("Input (uncached)",UsageSnapshot.FormatCount(data.Input)),
            ("Cache read",UsageSnapshot.FormatCount(data.CacheRead)),
            ("Cache write",UsageSnapshot.FormatCount(data.CacheWrite)),
            ("Output",UsageSnapshot.FormatCount(data.Output)),
            ("Reasoning",data.Source==SourceKind.Claude?"Included in output":UsageSnapshot.FormatCount(data.Reasoning)),
            ("Context",data.ContextLimit>0?
                $"{UsageSnapshot.FormatCount(data.ContextUsed)} / {UsageSnapshot.FormatCount(data.ContextLimit)}":"Unavailable"),
            ("Last turn time",data.TimeText),
            ("Cost",data.Cost is { } cost?
                "$"+cost.ToString("0.0000",System.Globalization.CultureInfo.InvariantCulture):"Unavailable")
        };
        for(var i=0;i<fields.Length;i++)
        {
            _fields[i].Key.Text=fields[i].Item1;
            _fields[i].Value.Text=fields[i].Item2;
        }
        _footer.Text=data.Note??"Local-only · read-only";
    }

    private static string Ellipsis(string value,int length)=>
        value.Length<=length?value:value[..(length-1)]+"…";
    private void Toggle(){_expanded=!_expanded;RefreshLayout();}
    private void RefreshLayout()
    {
        Height=_expanded?ExpandedHeight:CompactHeight;
        _rows.Visible=_expanded;
        _caption.Visible=_expanded;
        _footer.Visible=_expanded;
        using var path=new GraphicsPath();
        var box=new Rectangle(0,0,Width,Height);
        var d=22;
        path.AddArc(0,0,d,d,180,90);
        path.AddArc(box.Right-d,0,d,d,270,90);
        path.AddArc(box.Right-d,box.Bottom-d,d,d,0,90);
        path.AddArc(0,box.Bottom-d,d,d,90,90);
        path.CloseFigure();
        Region?.Dispose();
        Region=new Region(path);
    }
}
