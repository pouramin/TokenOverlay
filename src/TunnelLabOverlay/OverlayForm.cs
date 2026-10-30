using System.Drawing.Drawing2D;

namespace TunnelLab.TokenOverlay;

/// <summary>Fresh WinForms overlay; separate from all three editor processes.</summary>
internal sealed class OverlayForm : Form
{
    private readonly Panel _header = new() { BackColor = Color.FromArgb(30, 34, 45), Cursor = Cursors.Hand };
    private readonly Label _summary = new() { ForeColor = Color.White, BackColor = Color.Transparent,
        Font = new Font("Segoe UI Semibold", 11f, FontStyle.Regular), AutoEllipsis = true, Cursor=Cursors.Hand };
    private readonly Button _close = new() { Text = "×", Font = new Font("Segoe UI", 16f, FontStyle.Regular),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30,34,45),
        ForeColor = Color.FromArgb(200,205,222), TabStop=false, Cursor=Cursors.Hand };
    private readonly Label _caption = new() { ForeColor=Color.FromArgb(157,168,192),
        Font=new Font("Segoe UI",9f), BackColor=Color.Transparent };
    private readonly Panel _rows = new() { BackColor = Color.FromArgb(30, 34, 45) };
    private readonly Label _footer = new() { BackColor=Color.Transparent, ForeColor=Color.FromArgb(161,166,188),
        Font=new Font("Segoe UI",8f), AutoEllipsis=true };
    private readonly List<(Label Key,Label Value)> _fields = new();
    private readonly ToolTip _tip = new();
    private UsageSnapshot? _snapshot;
    private bool _expanded;
    public event EventHandler? ExitClicked;
    public bool Expanded => _expanded;

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p=base.CreateParams;
            p.ExStyle |= 0x08000000 | 0x00000080; // no-activate, tool-window
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
        Opacity=0.98;
        Width=378;Height=48;
        _header.Left=0;_header.Top=0;_header.Width=Width;_header.Height=48;
        _header.Click+=(_,_)=>Toggle();
        _summary.Left=13;_summary.Top=12;_summary.Width=322;_summary.Height=27;
        _summary.Click+=(_,_)=>Toggle();
        _close.Left=Width-38;_close.Top=8;_close.Width=27;_close.Height=31;
        _close.FlatAppearance.BorderSize=0;
        _close.FlatAppearance.MouseOverBackColor=Color.FromArgb(92,41,49);
        _close.Click+=(_,_)=>ExitClicked?.Invoke(this,EventArgs.Empty);
        _caption.Left=15;_caption.Top=54;_caption.Width=320;_caption.Height=24;
        _rows.Left=15;_rows.Top=88;_rows.Width=Width-30;_rows.Height=340;
        _footer.Left=15;_footer.Top=425;_footer.Width=Width-30;_footer.Height=40;
        Controls.Add(_header);
        _header.Controls.Add(_summary);
        _header.Controls.Add(_close);
        Controls.Add(_caption);
        Controls.Add(_rows);
        Controls.Add(_footer);
        for(var i=0;i<11;i++)
        {
            var k=new Label { ForeColor=Color.FromArgb(147,157,181),
                Font=new Font("Segoe UI",9.5f),Left=0,Top=i*28,Width=154,Height=24,AutoEllipsis=true};
            var v=new Label { ForeColor=Color.White,Font=new Font("Segoe UI Semibold",9.5f),
                TextAlign=ContentAlignment.MiddleRight,Left=157,Top=i*28,Width=190,Height=24,AutoEllipsis=true};
            _fields.Add((k,v));_rows.Controls.Add(k);_rows.Controls.Add(v);
        }
        _tip.SetToolTip(_close, "Exit TokenOverlay");
        _tip.SetToolTip(_summary,"Click to expand / collapse");
        UpdateLayout();
    }
    private static string Count(long count)=>UsageSnapshot.FormatCount(count);
    public void SetData(UsageSnapshot? snapshot,SourceKind source)
    {
        _snapshot=snapshot;
        var sourceName=source.ToString();
        _summary.Text=snapshot is null
            ? $"{sourceName}  |  No local session data  ▾"
            : $"{sourceName}  {Count(snapshot.Total)} tokens  ·  {snapshot.TimeText}  ▾";
        _caption.Text=snapshot is null ? $"{sourceName} — waiting for local data" :
            $"{sourceName}  /  {Truncate(snapshot.SessionName,39)}";
        if(snapshot is not null)
        {
            var rows=new (string,string)[] {
                ("Total consumed",Count(snapshot.Total)),
                ("Main session",snapshot.MainSessionTokens>0?Count(snapshot.MainSessionTokens):Count(snapshot.Total)),
                ("Child sessions",snapshot.ChildSessions.ToString()),
                ("Input (uncached)",Count(snapshot.Input)),
                ("Cache read",Count(snapshot.CacheRead)),
                ("Cache write",Count(snapshot.CacheWrite)),
                ("Output",Count(snapshot.Output)),
                ("Reasoning",Count(snapshot.Reasoning)),
                ("Context used",snapshot.ContextLimit>0?$"{Count(snapshot.ContextUsed)} / {Count(snapshot.ContextLimit)}":"Not reported"),
                ("Worked for",snapshot.TimeText),
                ("Recorded cost",snapshot.Cost is { } cost?"$"+cost.ToString("0.0000",System.Globalization.CultureInfo.InvariantCulture):"N/A")
            };
            for(var i=0;i<rows.Length;i++)SetRow(i,rows[i].Item1,rows[i].Item2);
            _footer.Text = snapshot.Note ?? "";
        }
        else
        {
            for(var i=0;i<_fields.Count;i++)SetRow(i,"","—");
            _footer.Text="Local-only. No network requests.";
        }
    }
    private void SetRow(int i,string key,string value){_fields[i].Key.Text=key;_fields[i].Value.Text=value;}
    private static string Truncate(string value,int max)=>value.Length>max?value[..(max-1)]+"…":value;
    public void Toggle(){_expanded=!_expanded;UpdateLayout();}
    private void UpdateLayout()
    {
        Height=_expanded?478:48;
        _rows.Visible=_expanded;
        _caption.Visible=_expanded;
        _footer.Visible=_expanded;
        using var path=new GraphicsPath();
        var radius=14;var d=radius*2;
        var area=new Rectangle(0,0,Width,Height);
        path.AddArc(0,0,d,d,180,90);
        path.AddArc(area.Right-d,0,d,d,270,90);
        path.AddArc(area.Right-d,area.Bottom-d,d,d,0,90);
        path.AddArc(0,area.Bottom-d,d,d,90,90);
        path.CloseFigure();
        Region?.Dispose();Region=new Region(path);
    }
}
