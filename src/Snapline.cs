using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Snapline {
static class Program {
    [STAThread] static void Main(string[] args) {
        if (args.Contains("--self-test")) { SelfTest.Run(); return; }
        bool first;
        using (var mutex = new Mutex(true, "Local\\Snapline.Desktop", out first)) {
            if (!first) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ApplicationContext(new Clothesline()));
        }
    }
}
static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("ole32.dll")] public static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
sealed class Store {
    public readonly string Root;
    public string Inbox { get { return Path.Combine(Root, "Inbox"); } }
    public Store(string root) { Root = root; Directory.CreateDirectory(Inbox); }
    public string Save(Image image) {
        string path = Path.Combine(Inbox, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N").Substring(0,8) + ".png");
        image.Save(path, ImageFormat.Png); return path;
    }
    public static Bitmap Load(string path) { using (var image = Image.FromFile(path)) return new Bitmap(image); }
    public static string Fingerprint(Image image) { using(var bitmap=new Bitmap(image)) using(var stream=new MemoryStream()) using(var hash=System.Security.Cryptography.SHA256.Create()) { bitmap.Save(stream,ImageFormat.Png); return Convert.ToBase64String(hash.ComputeHash(stream.ToArray())); } }
    public static bool Supported(string path) { return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(Path.GetExtension(path).ToLowerInvariant()); }
}
sealed class Shot : IDisposable {
    public string Path; public Bitmap Image; public string Fingerprint; public double? RopeX;
    public Shot(string path) { Path = path; Image = Store.Load(path); Fingerprint = Store.Fingerprint(Image); }
    public void Dispose() { Image.Dispose(); }
}
sealed class Clothesline : Form {
    readonly Store store = new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snapline"));
    readonly List<Shot> shots = new List<Shot>();
    readonly List<Rectangle> cards = new List<Rectangle>();
    readonly Dictionary<string, DateTime> pending = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer clickTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer animationTimer = new System.Windows.Forms.Timer();
    readonly DropMotion motion = new DropMotion(244);
    Rectangle revealArea;
    NotifyIcon tray; ContextMenuStrip menu; ToolStripMenuItem clipboardItem, startupItem, folderItem;
    Icon ownIcon; bool hanging = true, revealed, pinned, dragging, quitting, capturing;
    uint clipboardSequence; DateTime lastInside = DateTime.UtcNow;
    Point down; Shot pressed, clicked; DateTime pressTime;
    Shot hovered; string notice; DateTime noticeUntil;
    Rectangle snipButton, hideButton;
    bool ropeDragging; int dragLeft,grabOffset;
    string watchFolder; int offset;
    const int CardWidth = 196, CardHeight = 144, Gap = 28;
    public Clothesline() {
        AutoScaleMode=AutoScaleMode.None;
        Text = "Snapline"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        TopMost = true; DoubleBuffered = true; BackColor = Color.FromArgb(1,1,1); TransparencyKey = BackColor;
        AllowDrop = true; Height = 244;
        var screen = Screen.PrimaryScreen; Bounds = new Rectangle(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, Height);
        LoadSettings(); CreateTray();
        foreach (string path in Directory.GetFiles(store.Inbox).Where(Store.Supported).OrderBy(File.GetCreationTimeUtc)) TryAdd(path);
        LoadLayout();
        timer.Interval = 100; timer.Tick += Tick; timer.Start();
        animationTimer.Interval=16; animationTimer.Tick+=Animate; animationTimer.Start();
        clickTimer.Interval = SystemInformation.DoubleClickTime; clickTimer.Tick += delegate { clickTimer.Stop(); if (clicked != null && shots.Contains(clicked)) Copy(clicked); clicked = null; };
        clipboardSequence = Native.GetClipboardSequenceNumber();
        MouseDown += OnDown; MouseMove += OnMove; MouseUp += OnUp;
        MouseLeave += delegate { hovered=null; Invalidate(); };
        MouseWheel += delegate(object s, MouseEventArgs e) { if(pressed!=null)return; offset = Math.Max(0, Math.Min(Math.Max(0, shots.Count - Capacity), offset - Math.Sign(e.Delta))); Invalidate(); };
        MouseCaptureChanged+=delegate { if(!Capture && pressed!=null) { if(ropeDragging)SaveLayout(); pressed=null; ropeDragging=false; Invalidate(); } };
        DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += delegate(object s, DragEventArgs e) { foreach (string p in (string[])e.Data.GetData(DataFormats.FileDrop)) Import(p); Reveal(false); };
        KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Tuck(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        // Services must start even when Windows suppresses the first Show call.
        // Creating the handle here makes hotkeys independent of the Shown event.
        IntPtr serviceHandle = Handle;
        Watch();
        bool toggleReady=Native.RegisterHotKey(serviceHandle,1,0x4003,(uint)Keys.T);
        bool captureReady=Native.RegisterHotKey(serviceHandle,2,0x4006,(uint)Keys.D);
        bool windowsCaptureReady=Native.RegisterHotKey(serviceHandle,3,0x400C,(uint)Keys.S);
        menu.Items.Insert(0,new ToolStripMenuItem(captureReady ? "Capture shortcut: Ctrl+Shift+D (ready)" : "Ctrl+Shift+D is in use - use Take a snip") { Enabled=false });
        Safe(delegate { File.WriteAllLines(Path.Combine(store.Root,"status.txt"),new[] { "Started="+DateTime.Now.ToString("O"), "Process="+System.Diagnostics.Process.GetCurrentProcess().Id, "Ctrl+Shift+D="+captureReady, "Ctrl+Alt+T="+toggleReady, "Win+Shift+S="+windowsCaptureReady, "Folder monitoring="+(watchers.Count>0) }); });
        if(!captureReady) Notify("Ctrl+Shift+D is in use. Use Take a snip from the tray menu.");
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x312) { if (m.WParam.ToInt32() == 1) { if (revealed) Tuck(); else Reveal(true); } else if (m.WParam.ToInt32() == 2 || m.WParam.ToInt32() == 3) BeginInvoke((Action)TakeSnip); } base.WndProc(ref m); }
    int Capacity { get { return Math.Max(1, (Width - 60) / (CardWidth + Gap)); } }
    void DisplaysChanged(object sender, EventArgs e) { if (!quitting && IsHandleCreated) BeginInvoke((Action)delegate { Tuck(true); }); }
    void CreateTray() {
        using (var bitmap = new Bitmap(32,32)) {
            using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Color.FromArgb(42,139,116))) g.FillEllipse(b,1,1,30,30); g.DrawLine(Pens.White,5,10,27,10); g.FillRectangle(Brushes.White,9,13,14,12); g.DrawLine(Pens.White,12,8,12,15); }
            IntPtr h = bitmap.GetHicon(); using (var temporary = Icon.FromHandle(h)) ownIcon = (Icon)temporary.Clone(); DestroyIcon(h);
        }
        menu = new ContextMenuStrip();
        menu.Items.Add("Show / hide   Ctrl+Alt+T", null, delegate { if (revealed) Tuck(); else Reveal(true); });
        menu.Items.Add("Take a snip   Ctrl+Shift+D", null, delegate { BeginInvoke((Action)TakeSnip); });
        menu.Items.Add("Add images…", null, delegate { using (var d = new OpenFileDialog { Multiselect = true, Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif" }) if (d.ShowDialog() == DialogResult.OK) { foreach (string p in d.FileNames) Import(p); Reveal(true); } });
        clipboardItem = new ToolStripMenuItem("Hang copied images") { Checked = hanging, CheckOnClick = true };
        clipboardItem.CheckedChanged += delegate { hanging = clipboardItem.Checked; clipboardSequence = Native.GetClipboardSequenceNumber(); SaveSettings(); }; menu.Items.Add(clipboardItem);
        folderItem = new ToolStripMenuItem("Screenshot folder: " + watchFolder);
        folderItem.Click += delegate { using (var d = new FolderBrowserDialog { SelectedPath = watchFolder, Description = "Watch this folder for new screenshots (original files are kept)." }) if (d.ShowDialog() == DialogResult.OK) { watchFolder = d.SelectedPath; folderItem.Text = "Screenshot folder: " + watchFolder; SaveSettings(); Watch(); } }; menu.Items.Add(folderItem);
        startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = File.Exists(StartupPath) };
        startupItem.Click += delegate { Safe(delegate { if (startupItem.Checked) File.WriteAllText(StartupPath, "@start \"\" \"" + Application.ExecutablePath + "\"\r\n"); else File.Delete(StartupPath); }); startupItem.Checked = File.Exists(StartupPath); }; menu.Items.Add(startupItem);
        menu.Items.Add("Open saved images", null, delegate { Run(store.Inbox); });
        menu.Items.Add("About", null, delegate { MessageBox.Show("Snapline — screenshots within reach.\n\nHover at the top edge or press Ctrl+Alt+T.\nClick to copy · Double-click to open · Hold to draw\nDrag to share · Right-click for more actions\nScroll for older images · Escape to hide\n\nInspired by Alejandro Buján's Tendedero.\nLocal only. No accounts, analytics, or network.", "Snapline"); });
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Quit", null, delegate { Close(); });
        tray = new NotifyIcon { Icon = ownIcon, Text = "Snapline — screenshots within reach", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { Reveal(true); };
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    string StartupPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Snapline.cmd"); } }
    void LoadSettings() {
        watchFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        string path = Path.Combine(store.Root, "settings.txt");
        if (File.Exists(path)) { var lines = File.ReadAllLines(path); if (lines.Length > 0) hanging = lines[0] != "false"; if (lines.Length > 1 && !String.IsNullOrWhiteSpace(lines[1])) watchFolder = lines[1]; }
    }
    void SaveSettings() { Safe(delegate { File.WriteAllLines(Path.Combine(store.Root, "settings.txt"), new[] { hanging ? "true" : "false", watchFolder }); }); }
    void LoadLayout() {
        string path=Path.Combine(store.Root,"layout.txt"); if(!File.Exists(path))return;
        Safe(delegate {
            var ordered=new List<Shot>();
            foreach(string line in File.ReadAllLines(path)) { var parts=line.Split('|'); double x; if(parts.Length!=2 || !Double.TryParse(parts[1],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out x))continue;
                Shot shot=shots.FirstOrDefault(s=>System.IO.Path.GetFileName(s.Path)==parts[0]);
                if(shot==null||ordered.Contains(shot))continue; shot.RopeX=Math.Max(0,Math.Min(1,x)); ordered.Add(shot);
            }
            ordered.AddRange(shots.Where(s=>!ordered.Contains(s))); shots.Clear(); shots.AddRange(ordered);
        });
    }
    void SaveLayout() { Safe(delegate { File.WriteAllLines(Path.Combine(store.Root,"layout.txt"),shots.Select(s=>System.IO.Path.GetFileName(s.Path)+"|"+(s.RopeX??0.5).ToString("R",System.Globalization.CultureInfo.InvariantCulture)).ToArray()); }); }
    void LayoutCards() {
        cards.Clear(); int count=Math.Min(Capacity,shots.Count-offset);
        int[] positions=RopeLayout.Arrange(shots.Skip(offset).Take(count).Select(s=>s.RopeX).ToArray(),Width,CardWidth,Gap);
        for(int i=0;i<count;i++) { shots[offset+i].RopeX=(positions[i]+CardWidth/2.0)/Width; cards.Add(new Rectangle(positions[i],54,CardWidth,CardHeight)); }
    }
    void MoveOnRope(int x) {
        LayoutCards(); int index=shots.IndexOf(pressed),local=index-offset;
        dragLeft=Math.Max(12,Math.Min(Width-CardWidth-12,x-grabOffset));
        double center=dragLeft+CardWidth/2.0;
        while(local>0 && center<cards[local-1].X+CardWidth/2.0) { SwapRope(index,index-1); index--; local--; LayoutCards(); }
        while(local<cards.Count-1 && center>cards[local+1].X+CardWidth/2.0) { SwapRope(index,index+1); index++; local++; LayoutCards(); }
        int left=local==0 ? 12 : cards[local-1].Right+Gap;
        int right=local==cards.Count-1 ? Width-CardWidth-12 : cards[local+1].Left-CardWidth-Gap;
        pressed.RopeX=(Math.Max(left,Math.Min(right,dragLeft))+CardWidth/2.0)/Width;
        Invalidate();
    }
    void SwapRope(int a,int b) { Shot first=shots[a],second=shots[b]; double? x=first.RopeX;first.RopeX=second.RopeX;second.RopeX=x;shots[a]=second;shots[b]=first; }
    void Watch() {
        foreach (var w in watchers) w.Dispose(); watchers.Clear();
        string existing = watchFolder;
        while (!Directory.Exists(existing)) { existing = Path.GetDirectoryName(existing); if (String.IsNullOrEmpty(existing)) return; }
        Safe(delegate {
            var w = new FileSystemWatcher(existing) { IncludeSubdirectories = existing != watchFolder, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = false };
            FileSystemEventHandler queue = delegate(object s, FileSystemEventArgs e) { Queue(e.FullPath); };
            w.Created += queue; w.Changed += queue; w.Renamed += delegate(object s, RenamedEventArgs e) { Queue(e.FullPath); };
            w.Error += delegate { if (!quitting && IsHandleCreated) BeginInvoke((Action)delegate { tray.ShowBalloonTip(3000, "Snapline", "Folder monitoring was interrupted. Select the screenshot folder again to restart it.", ToolTipIcon.Warning); }); };
            watchers.Add(w); w.EnableRaisingEvents = true;
        });
    }
    readonly Dictionary<string, string> imported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    void Queue(string path) { if (!String.Equals(Path.GetDirectoryName(path),watchFolder,StringComparison.OrdinalIgnoreCase) || !Store.Supported(path) || quitting || !IsHandleCreated) return; try { BeginInvoke((Action)delegate { pending[path] = DateTime.UtcNow; }); } catch (InvalidOperationException) {} }
    void Tick(object sender, EventArgs e) {
        if (capturing) return;
        if(notice!=null && DateTime.UtcNow>noticeUntil) { notice=null; Invalidate(); }
        foreach (var p in pending.ToArray()) {
            if ((DateTime.UtcNow - p.Value).TotalMilliseconds < 500) continue;
            try {
                if (!File.Exists(p.Key)) { pending.Remove(p.Key); continue; }
                string signature = new FileInfo(p.Key).Length + ":" + File.GetLastWriteTimeUtc(p.Key).Ticks;
                string previous; if (!imported.TryGetValue(p.Key, out previous) || previous != signature) { using (var image = Store.Load(p.Key)) AddImage(image); imported[p.Key] = signature; }
                pending.Remove(p.Key);
            } catch (Exception ex) { if (!(ex is IOException || ex is ArgumentException || ex is ExternalException)) pending.Remove(p.Key); else if ((DateTime.UtcNow - p.Value).TotalSeconds > 15) pending.Remove(p.Key); }
        }
        uint seq = Native.GetClipboardSequenceNumber();
        if (seq != clipboardSequence) {
            try {
                if (hanging && Clipboard.ContainsImage() && !Clipboard.ContainsText() && !Clipboard.ContainsFileDropList() && !Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") && !Clipboard.ContainsData("Clipboard Viewer Ignore")) {
                    using (var image = Clipboard.GetImage()) if (image != null) AddImage(image);
                }
                clipboardSequence = seq;
            } catch (ExternalException) {} catch (Exception ex) { clipboardSequence = seq; Notify(ex.Message); }
        }
    }
    readonly System.Diagnostics.Stopwatch animationClock=System.Diagnostics.Stopwatch.StartNew();
    void Animate(object sender,EventArgs e) {
        double dt=Math.Min(0.05,animationClock.Elapsed.TotalSeconds); animationClock.Restart();
        if(!capturing && !dragging && pressed==null && !menu.Visible) CheckHover();
        if(!Visible)return;
        if(motion.Step(dt)) Invalidate();
        if(!revealed && motion.Hidden) Hide();
    }
    void CheckHover() {
        Point pnt = Cursor.Position; var screen = Screen.FromPoint(pnt);
        bool edge = pnt.Y >= screen.Bounds.Top && pnt.Y <= screen.Bounds.Top + 5 && pnt.X >= screen.Bounds.Left && pnt.X < screen.Bounds.Right;
        if (!revealed && edge && !IsFullscreen(screen)) Reveal(false);
        if (revealed) {
            if (revealArea.Contains(pnt)) lastInside = DateTime.UtcNow;
            else if (!pinned && (DateTime.UtcNow - lastInside).TotalMilliseconds > 90) Tuck();
        }
    }
    bool IsFullscreen(Screen screen) {
        IntPtr foreground = Native.GetForegroundWindow(); Native.RECT r;
        if (foreground == Handle || !Native.GetWindowRect(foreground, out r)) return false;
        var name=new System.Text.StringBuilder(256); Native.GetClassName(foreground,name,256);
        if(name.ToString()=="Progman" || name.ToString()=="WorkerW" || name.ToString()=="Shell_TrayWnd") return false;
        var b = screen.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
    void Reveal(bool pin) {
        var screen=Screen.FromPoint(Cursor.Position); var next=new Rectangle(screen.Bounds.Left,screen.Bounds.Top,screen.Bounds.Width,Height);
        if(!Visible || Bounds!=next) motion.Reset();
        Bounds=next; revealArea=next; pinned=pin; revealed=true; motion.Show=true;
        lastInside=DateTime.UtcNow; Show(); Invalidate();
    }
    void Tuck() { Tuck(false); }
    void Tuck(bool immediate) {
        pinned=false; revealed=false; motion.Show=false; hovered=null;
        if(immediate) { motion.Reset(); Hide(); } else Invalidate();
    }
    void TakeSnip() {
        if(capturing) return;
        capturing=true; Tuck(true);
        try {
            // Give the compositor time to remove the line and tray menu before capture.
            var delay=new System.Windows.Forms.Timer { Interval=180 };
                delay.Tick+=delegate { delay.Stop(); delay.Dispose();
                    try {
                        using(var selector=new RegionSelector()) if(selector.ShowDialog()==DialogResult.OK) {
                            AddImage(selector.Result);
                            Clipboard.SetImage(selector.Result); clipboardSequence=Native.GetClipboardSequenceNumber();
                            Reveal(false); lastInside=DateTime.UtcNow.AddSeconds(2);
                        }
                    } catch(Exception ex) { Notify("Capture failed: " + ex.Message); }
                    finally { capturing=false; }
                };
                delay.Start();
        } catch(Exception ex) { capturing=false; Notify("Capture failed: " + ex.Message); }
    }
    void TryAdd(string path) {
        try { if (shots.Any(s => s.Path == path)) return; shots.Insert(0, new Shot(path)); offset = 0; Invalidate(); }
        catch (Exception ex) { if (!(ex is IOException || ex is ArgumentException || ex is ExternalException)) throw; }
    }
    void AddImage(Image image) { string hash=Store.Fingerprint(image); if(shots.Any(s=>s.Fingerprint==hash))return; TryAdd(store.Save(image)); }
    void Import(string path) { if (!Store.Supported(path)) return; Safe(delegate { using (var image = Store.Load(path)) AddImage(image); }); }
    void Copy(Shot shot) { Safe(delegate { using(var transfer=new ImageTransfer(shot.Image,shot.Path,false)) Clipboard.SetDataObject(transfer.Data,true); clipboardSequence = Native.GetClipboardSequenceNumber(); notice="Copied — ready to paste"; noticeUntil=DateTime.UtcNow.AddSeconds(2); Invalidate(); }); }
    void Remove(Shot shot) { Safe(delegate { File.Delete(shot.Path); shots.Remove(shot); shot.Dispose(); offset = Math.Min(offset, Math.Max(0, shots.Count - Capacity)); SaveLayout(); Invalidate(); }); }
    void Run(string target) { Safe(delegate { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }); }
    void Notify(string message) { if (tray != null) tray.ShowBalloonTip(4000, "Snapline", message, ToolTipIcon.Warning); }
    void Safe(Action action) { try { action(); } catch (Exception ex) { Notify(ex.Message); } }
    Point DrawingPoint(Point p) { return new Point(p.X,p.Y-(int)Math.Round(motion.Position)); }
    Shot Hit(Point p) { p=DrawingPoint(p); if(ropeDragging && pressed!=null && new Rectangle(dragLeft,54,CardWidth,CardHeight).Contains(p))return pressed; for (int i=0; i<cards.Count; i++) if (cards[i].Contains(p) && i + offset < shots.Count) return shots[i + offset]; return null; }
    void OnDown(object sender, MouseEventArgs e) {
        Point drawing=DrawingPoint(e.Location);
        if(e.Button==MouseButtons.Left && snipButton.Contains(drawing)) { BeginInvoke((Action)TakeSnip); return; }
        if(e.Button==MouseButtons.Left && hideButton.Contains(drawing)) { Tuck(); return; }
        var shot = Hit(e.Location); if (shot == null) { if (e.Button == MouseButtons.Right) menu.Show(this,e.Location); return; }
        if (e.Button == MouseButtons.Right) { ShowShotMenu(shot,e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        int i = shots.IndexOf(shot) - offset; var rect = cards[i];
        if (drawing.X > rect.Right - 25 && drawing.Y < rect.Top + 24) { Remove(shot); return; }
        if (e.Clicks == 2) { clickTimer.Stop(); clicked = null; Run(shot.Path); return; }
        down = e.Location; pressed = shot; pressTime = DateTime.UtcNow; ropeDragging=false; grabOffset=e.X-rect.X; dragLeft=rect.X; Capture = true;
    }
    void OnMove(object sender, MouseEventArgs e) {
        Shot over=Hit(e.Location); if(hovered!=over) { hovered=over; Invalidate(); }
        Cursor = over != null || snipButton.Contains(DrawingPoint(e.Location)) || hideButton.Contains(DrawingPoint(e.Location)) ? Cursors.Hand : Cursors.Default;
        if (pressed == null || dragging || (Math.Abs(e.X-down.X) < SystemInformation.DragSize.Width / 2 && Math.Abs(e.Y-down.Y) < SystemInformation.DragSize.Height / 2)) return;
        clickTimer.Stop(); clicked=null;
        if(Math.Abs(e.Y-down.Y)<52) { ropeDragging=true; Cursor=Cursors.SizeWE; MoveOnRope(e.X); return; }
        Shot shot = pressed; pressed = null; ropeDragging=false; SaveLayout(); Capture = false; dragging = true;
        try {
            // Leave a normal paste available even when the target has no drop support.
            Copy(shot);
            using(var transfer=new ImageTransfer(shot.Image,shot.Path,true)) {
                Tuck(true);
                DragDropEffects result=DoDragDrop(transfer.Data,DragDropEffects.Copy);
                if(result==DragDropEffects.None) Notify("Image copied. This target did not accept the drop; focus its editor and press Ctrl+V.");
            }
        } catch(Exception ex) { Notify("Could not share image: "+ex.Message); }
        finally { dragging = false; lastInside = DateTime.UtcNow; }
    }
    void OnUp(object sender, MouseEventArgs e) {
        if (pressed == null) return; Shot shot = pressed; bool moved=ropeDragging; pressed = null; ropeDragging=false; Capture = false;
        if(moved) { SaveLayout(); lastInside=DateTime.UtcNow; Invalidate(); return; }
        if (Hit(e.Location) != shot) return;
        if ((DateTime.UtcNow - pressTime).TotalMilliseconds > 550) Edit(shot);
        else { clicked = shot; clickTimer.Stop(); clickTimer.Start(); }
    }
    void ShowShotMenu(Shot shot, Point p) {
        var m = new ContextMenuStrip(); m.Items.Add("Copy image",null,delegate { Copy(shot); }); m.Items.Add("Open",null,delegate { Run(shot.Path); });
        m.Items.Add("Draw / annotate…",null,delegate { Edit(shot); });
        m.Items.Add("Save a copy…",null,delegate { using(var d=new SaveFileDialog { Filter="PNG image|*.png", FileName=Path.GetFileName(shot.Path) }) if(d.ShowDialog()==DialogResult.OK) Safe(delegate { shot.Image.Save(d.FileName,ImageFormat.Png); }); });
        m.Items.Add("Remove from line",null,delegate { Remove(shot); });
        pinned = true; m.Closed += delegate { pinned = false; lastInside = DateTime.UtcNow; m.Dispose(); }; m.Show(this,p);
    }
    void Edit(Shot shot) { Tuck(true); using (var editor = new Editor(shot.Image)) if (editor.ShowDialog() == DialogResult.OK) Safe(delegate { editor.Result.Save(shot.Path,ImageFormat.Png); shot.Image.Dispose(); shot.Image = Store.Load(shot.Path); shot.Fingerprint=Store.Fingerprint(shot.Image); }); Reveal(true); }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
        g.TranslateTransform(0,(float)Math.Round(motion.Position));
        g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        LineArt.DrawRope(g,Width);
        LayoutCards();
        for(int i=0;i<cards.Count;i++) {
            var r=cards[i]; var shot=shots[offset+i];
            if(ropeDragging && shot==pressed)continue;
            LineArt.DrawCard(g,r,shot.Image,hovered==shot);
        }
        if(ropeDragging && pressed!=null)LineArt.DrawCard(g,new Rectangle(dragLeft,54,CardWidth,CardHeight),pressed.Image,true);
        if(shots.Count==0) {
            var empty=new Rectangle(Width/2-210,65,420,118); LineArt.Glass(g,empty,18,false);
            using(var title=new Font("Segoe UI",16,FontStyle.Bold)) using(var body=new Font("Segoe UI",10)) {
                LineArt.Center(g,"Screenshots, within reach.",title,Brushes.White,new Rectangle(empty.X,empty.Y+18,empty.Width,30));
                using(var muted=new SolidBrush(Color.FromArgb(193,189,213))) LineArt.Center(g,"Ctrl+Shift+D to capture · or drop an image here",body,muted,new Rectangle(empty.X,empty.Y+58,empty.Width,28));
            }
        }
        string hint=ropeDragging ? "Slide to arrange  ·  Pull down to paste" : notice ?? (shots.Count==0 ? "SNAPLINE" : String.Format("{0} images   ·   Slide to arrange   ·   Pull down to paste",shots.Count));
        using(var font=new Font("Segoe UI",9)) {
            int width=(int)g.MeasureString(hint,font).Width+30; int total=width+164+12;
            int x=(Width-total)/2; var pill=new Rectangle(x,212,width,28); LineArt.Glass(g,pill,14,false);
            LineArt.Center(g,hint,font,Brushes.White,pill);
            snipButton=new Rectangle(pill.Right+8,212,124,28); LineArt.Glass(g,snipButton,14,true); LineArt.Center(g,"+  Capture",font,Brushes.White,snipButton);
            hideButton=new Rectangle(snipButton.Right+8,212,28,28); LineArt.Glass(g,hideButton,14,false); LineArt.Center(g,"×",font,Brushes.White,hideButton);
        }
    }
    protected override void OnFormClosed(FormClosedEventArgs e) {
        quitting=true; timer.Stop(); clickTimer.Stop(); animationTimer.Stop(); Native.UnregisterHotKey(Handle,1); Native.UnregisterHotKey(Handle,2); Native.UnregisterHotKey(Handle,3); Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=DisplaysChanged;
        foreach(var w in watchers) w.Dispose(); foreach(var s in shots) s.Dispose(); tray.Visible=false; tray.Dispose(); menu.Dispose(); ownIcon.Dispose(); timer.Dispose(); clickTimer.Dispose(); animationTimer.Dispose(); base.OnFormClosed(e);
    }
}
static class RopeLayout {
    public static int[] Arrange(double?[] centers,int width,int card,int gap) {
        int count=centers.Length; var result=new int[count]; if(count==0)return result;
        int start=(width-count*(card+gap)+gap)/2;
        for(int i=0;i<count;i++) { result[i]=centers[i].HasValue ? (int)Math.Round(centers[i].Value*width-card/2.0) : start+i*(card+gap); result[i]=Math.Max(12,result[i]); if(i>0)result[i]=Math.Max(result[i],result[i-1]+card+gap); }
        result[count-1]=Math.Min(result[count-1],width-card-12);
        for(int i=count-2;i>=0;i--)result[i]=Math.Min(result[i],result[i+1]-card-gap);
        return result;
    }
}
sealed class DropMotion {
    readonly double height; public double Position,Velocity; public bool Show;
    public DropMotion(double height) { this.height=height; Reset(); }
    public bool Hidden { get { return !Show && Position == -height; } }
    public void Reset() { Position=-height; Velocity=0; Show=false; }
    public bool Step(double seconds) {
        double before=Position,target=Show ? 0 : -height;
        int steps=Math.Max(1,(int)Math.Ceiling(seconds*120)); double dt=seconds/steps;
        for(int i=0;i<steps;i++) { double spring=Show ? 230 : 500, damping=Show ? 23 : 45; Velocity+=((target-Position)*spring-Velocity*damping)*dt; Position+=Velocity*dt; }
        if(Math.Abs(target-Position)<0.4 && Math.Abs(Velocity)<5) { Position=target; Velocity=0; }
        return before!=Position;
    }
}
sealed class ImageTransfer : IDisposable {
    public readonly DataObject Data=new DataObject();
    readonly MemoryStream png=new MemoryStream(), dib; readonly Bitmap bitmap;
    public ImageTransfer(Image image,string path,bool withFile) {
        bitmap=new Bitmap(image); bitmap.Save(png,ImageFormat.Png); png.Position=0;
        Data.SetData("PNG",false,png); Data.SetData("image/png",false,png);
        Data.SetData(DataFormats.Bitmap,true,bitmap);
        using(var rgb=new Bitmap(image.Width,image.Height,PixelFormat.Format24bppRgb)) {
            using(var g=Graphics.FromImage(rgb)) { g.Clear(Color.White); g.DrawImageUnscaled(image,0,0); }
            using(var bmp=new MemoryStream()) { rgb.Save(bmp,ImageFormat.Bmp); byte[] bytes=bmp.ToArray(); dib=new MemoryStream(bytes,14,bytes.Length-14); }
        }
        Data.SetData(DataFormats.Dib,false,dib);
        if(withFile) { Data.SetData(DataFormats.FileDrop,true,new[] { path }); Data.SetData("Preferred DropEffect",false,new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy))); }
    }
    public void Dispose() { png.Dispose(); dib.Dispose(); bitmap.Dispose(); var effect=Data.GetData("Preferred DropEffect",false) as MemoryStream; if(effect!=null)effect.Dispose(); }
}
static class LineArt {
    public static GraphicsPath Rounded(Rectangle r,int radius) {
        int d=Math.Min(radius*2,Math.Min(r.Width,r.Height)); var path=new GraphicsPath();
        path.AddArc(r.X,r.Y,d,d,180,90); path.AddArc(r.Right-d,r.Y,d,d,270,90);
        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); path.AddArc(r.X,r.Bottom-d,d,d,90,90); path.CloseFigure(); return path;
    }
    public static void Glass(Graphics g,Rectangle r,int radius,bool active) {
        using(var shape=Rounded(r,radius)) using(var fill=new LinearGradientBrush(r,active ? Color.FromArgb(77,79,86) : Color.FromArgb(55,57,63),Color.FromArgb(31,33,38),LinearGradientMode.Vertical)) { g.FillPath(fill,shape); }
    }
    public static void DrawRope(Graphics g,int width) {
        using(var rope=new GraphicsPath()) {
            rope.AddBezier(-5,35,width/3,57,width*2/3,57,width+5,35);
            using(var shadow=new Pen(Color.FromArgb(37,31,53),3))g.DrawPath(shadow,rope);
            using(var light=new Pen(Color.FromArgb(188,182,197),1))g.DrawPath(light,rope);
        }
    }
    public static void Center(Graphics g,string text,Font font,Brush brush,Rectangle r) { using(var format=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center })g.DrawString(text,font,brush,r,format); }
    public static void DrawCard(Graphics g,Rectangle r,Image image,bool hover) {
        Glass(g,r,18,hover);
        var inner=Rectangle.Inflate(r,-8,-8);
        using(var mask=Rounded(inner,11)) { var state=g.Save(); g.SetClip(mask);
            using(var background=new SolidBrush(Color.FromArgb(29,28,42)))g.FillRectangle(background,inner);
            float scale=Math.Min((float)inner.Width/image.Width,(float)inner.Height/image.Height);
            int w=Math.Max(1,(int)(image.Width*scale)),h=Math.Max(1,(int)(image.Height*scale));
            g.DrawImage(image,new Rectangle(inner.X+(inner.Width-w)/2,inner.Y+(inner.Height-h)/2,w,h));
            using(var sheen=new LinearGradientBrush(inner,Color.FromArgb(30,255,255,255),Color.Transparent,LinearGradientMode.Vertical))g.FillRectangle(sheen,inner);
            g.Restore(state);
        }
        int x=r.X+r.Width/2;
        var clip=new Rectangle(x-5,r.Y-19,10,34);
        using(var shadow=new SolidBrush(Color.FromArgb(39,35,52)))g.FillRectangle(shadow,clip.X+2,clip.Y+3,clip.Width,clip.Height);
        using(var shape=Rounded(clip,4))using(var silver=new LinearGradientBrush(clip,Color.FromArgb(245,246,239),Color.FromArgb(147,154,153),LinearGradientMode.Horizontal))g.FillPath(silver,shape);
        using(var p=new Pen(Color.FromArgb(104,112,115)))g.DrawLine(p,x,clip.Y+9,x,clip.Bottom-5);
        if(hover) { var close=new Rectangle(r.Right-26,r.Top+7,19,19); using(var b=new SolidBrush(Color.FromArgb(43,36,63)))g.FillEllipse(b,close); using(var f=new Font("Segoe UI",10))Center(g,"×",f,Brushes.White,close); }
    }
    public static void Preview(string path) {
        using(var image=new Bitmap(1248,260)) using(var g=Graphics.FromImage(image)) using(var sample=new Bitmap(320,200)) using(var s=Graphics.FromImage(sample)) {
            g.SmoothingMode=SmoothingMode.AntiAlias; using(var bg=new LinearGradientBrush(new Rectangle(0,0,image.Width,image.Height),Color.FromArgb(57,58,110),Color.FromArgb(76,49,111),0f))g.FillRectangle(bg,0,0,image.Width,image.Height);
            s.Clear(Color.FromArgb(24,27,35)); using(var f=new Font("Segoe UI",14,FontStyle.Bold))s.DrawString("Screenshots this week",f,Brushes.White,18,20);
            using(var f=new Font("Segoe UI",35,FontStyle.Bold))s.DrawString("248",f,Brushes.White,18,58);
            using(var p=new Pen(Color.FromArgb(155,125,224),4))s.DrawBezier(p,18,165,90,110,175,180,300,125);
            DrawRope(g,image.Width); DrawCard(g,new Rectangle(414,54,196,144),sample,false);
            s.Clear(Color.FromArgb(75,54,119)); using(var f=new Font("Segoe UI",23,FontStyle.Bold))Center(s,"Screenshots,\nwithin reach.",f,Brushes.White,new Rectangle(0,0,320,200));
            DrawCard(g,new Rectangle(638,54,196,144),sample,true);
            using(var f=new Font("Segoe UI",9)) { var r=new Rectangle(400,216,335,28);Glass(g,r,14,false);Center(g,"2 images   ·   Click to copy   ·   Drag to paste",f,Brushes.White,r);r=new Rectangle(743,216,105,28);Glass(g,r,14,true);Center(g,"+  Capture",f,Brushes.White,r); }
            image.Save(path,ImageFormat.Png);
        }
    }
}
sealed class RegionSelector : Form {
    readonly Bitmap desktop; Point anchor; Rectangle selection; bool selecting;
    public Bitmap Result;
    public RegionSelector() {
        AutoScaleMode=AutoScaleMode.None;
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true; DoubleBuffered=true;
        Bounds=SystemInformation.VirtualScreen; Cursor=Cursors.Cross; KeyPreview=true;
        desktop=new Bitmap(Width,Height);
        using(var g=Graphics.FromImage(desktop)) g.CopyFromScreen(Bounds.Location,Point.Empty,Bounds.Size);
        KeyDown+=delegate(object s,KeyEventArgs e) { if(e.KeyCode==Keys.Escape) { DialogResult=DialogResult.Cancel; Close(); } };
        MouseDown+=delegate(object s,MouseEventArgs e) { if(e.Button==MouseButtons.Right) { DialogResult=DialogResult.Cancel; Close(); } else if(e.Button==MouseButtons.Left) { anchor=e.Location; selecting=true; Capture=true; } };
        MouseMove+=delegate(object s,MouseEventArgs e) { if(selecting) { selection=Between(anchor,e.Location,ClientRectangle); Invalidate(); } };
        MouseUp+=delegate(object s,MouseEventArgs e) { if(!selecting||e.Button!=MouseButtons.Left)return; selecting=false; Capture=false; selection=Between(anchor,e.Location,ClientRectangle); if(selection.Width<2||selection.Height<2) { Invalidate(); return; } Result=desktop.Clone(selection,PixelFormat.Format32bppArgb); DialogResult=DialogResult.OK; Close(); };
        Shown+=delegate { Activate(); Focus(); };
    }
    internal static Rectangle Between(Point a,Point b,Rectangle bounds) { return Rectangle.Intersect(Rectangle.FromLTRB(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y)),bounds); }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.DrawImageUnscaled(desktop,0,0);
        using(var dim=new SolidBrush(Color.FromArgb(115,0,0,0))) e.Graphics.FillRectangle(dim,ClientRectangle);
        if(selection.Width>0&&selection.Height>0) { e.Graphics.DrawImage(desktop,selection,selection,GraphicsUnit.Pixel); using(var pen=new Pen(Color.FromArgb(85,220,183),2)) e.Graphics.DrawRectangle(pen,selection); }
        using(var font=new Font("Segoe UI",14)) e.Graphics.DrawString("Drag to capture  |  Esc to cancel",font,Brushes.White,24,24);
    }
    protected override void Dispose(bool disposing) { if(disposing) { desktop.Dispose(); if(Result!=null)Result.Dispose(); } base.Dispose(disposing); }
}
sealed class Editor : Form {
    readonly Bitmap original; public Bitmap Result; readonly PictureBox canvas=new PictureBox();
    readonly Stack<Bitmap> undo=new Stack<Bitmap>(); Point? previous; Color color=Color.FromArgb(240,74,82);
    public Editor(Image source) {
        original=new Bitmap(source); Result=new Bitmap(source); Text="Snapline — annotate"; StartPosition=FormStartPosition.CenterScreen; Size=new Size(960,720); MinimumSize=new Size(400,300);
        var bar=new FlowLayoutPanel { Dock=DockStyle.Top, Height=42, Padding=new Padding(5) };
        AddButton(bar,"Color…",delegate { using(var d=new ColorDialog { Color=color }) if(d.ShowDialog()==DialogResult.OK) color=d.Color; });
        AddButton(bar,"Undo",delegate { if(undo.Count>0) { Result.Dispose(); Result=undo.Pop(); canvas.Image=Result; } });
        AddButton(bar,"Reset",delegate { undo.Push(new Bitmap(Result)); Result.Dispose(); Result=new Bitmap(original); canvas.Image=Result; });
        AddButton(bar,"Save",delegate { DialogResult=DialogResult.OK; Close(); });
        AddButton(bar,"Cancel",delegate { DialogResult=DialogResult.Cancel; Close(); });
        canvas.Dock=DockStyle.Fill; canvas.SizeMode=PictureBoxSizeMode.Zoom; canvas.BackColor=Color.FromArgb(30,34,40); canvas.Image=Result;
        Controls.Add(canvas); Controls.Add(bar);
        canvas.MouseDown+=delegate(object s,MouseEventArgs e) { if(e.Button!=MouseButtons.Left) return; var p=ImagePoint(e.Location); if(p.HasValue) { undo.Push(new Bitmap(Result)); previous=p; canvas.Capture=true; } };
        canvas.MouseMove+=delegate(object s,MouseEventArgs e) { var p=ImagePoint(e.Location); if(previous.HasValue && p.HasValue) { using(var g=Graphics.FromImage(Result)) using(var pen=new Pen(color,Math.Max(2,Result.Width/350f))) { g.SmoothingMode=SmoothingMode.AntiAlias; pen.StartCap=pen.EndCap=LineCap.Round; g.DrawLine(pen,previous.Value,p.Value); } previous=p; canvas.Invalidate(); } };
        canvas.MouseUp+=delegate { previous=null; canvas.Capture=false; };
    }
    Point? ImagePoint(Point p) { float scale=Math.Min((float)canvas.Width/Result.Width,(float)canvas.Height/Result.Height); int x=(int)((p.X-(canvas.Width-Result.Width*scale)/2)/scale),y=(int)((p.Y-(canvas.Height-Result.Height*scale)/2)/scale); if(x<0||y<0||x>=Result.Width||y>=Result.Height) return null; return new Point(x,y); }
    static void AddButton(FlowLayoutPanel panel,string text,Action action) { var b=new Button { Text=text,AutoSize=true }; b.Click+=delegate { action(); }; panel.Controls.Add(b); }
    protected override void Dispose(bool disposing) { if(disposing) { canvas.Image=null; original.Dispose(); Result.Dispose(); foreach(var b in undo)b.Dispose(); } base.Dispose(disposing); }
}
static class SelfTest {
    public static void Run() {
        string root=Path.Combine(Path.GetTempPath(),"Snapline-test-"+Guid.NewGuid().ToString("N"));
        try {
            var single=RopeLayout.Arrange(new double?[] { 0.75 },1200,196,28);
            if(single[0]!=802)throw new Exception("Single photo positioning failed");
            foreach(var centers in new[] { new double?[] { null,null,null },new double?[] { 0.95,0.1,0.5 },new double?[] { 0,0,0 },new double?[] { 1,1,1 } }) {
                int[] layout=RopeLayout.Arrange(centers,1000,196,28);
                if(layout[0]<12 || layout[layout.Length-1]+196>988)throw new Exception("Rope edge constraint failed");
                for(int i=1;i<layout.Length;i++)if(layout[i]-layout[i-1]<224)throw new Exception("Photos overlap");
            }
            var drop=new DropMotion(244); drop.Show=true;
            for(int frame=0;frame<90;frame++)drop.Step(1.0/60);
            if(drop.Position!=0 || drop.Velocity!=0)throw new Exception("Drop did not settle");
            drop.Show=false; for(int frame=0;frame<40;frame++)drop.Step(1.0/60);
            if(!drop.Hidden)throw new Exception("Retraction too slow");
            drop.Show=true; for(int frame=0;frame<8;frame++)drop.Step(1.0/60); drop.Show=false;
            for(int frame=0;frame<3;frame++)drop.Step(1.0/60); drop.Show=true;
            for(int frame=0;frame<90;frame++)drop.Step(1.0/60);
            if(drop.Position!=0)throw new Exception("Animation reversal failed");
            var bounds=new Rectangle(0,0,100,80);
            if(RegionSelector.Between(new Point(70,60),new Point(10,20),bounds)!=new Rectangle(10,20,60,40))throw new Exception("Reverse selection failed");
            if(RegionSelector.Between(new Point(-20,-10),new Point(120,90),bounds)!=bounds)throw new Exception("Selection bounds failed");
            var store=new Store(root); string a,b;
            using(var image=new Bitmap(40,30)) { using(var g=Graphics.FromImage(image))g.Clear(Color.Coral); a=store.Save(image); b=store.Save(image); }
            if(a==b||!File.Exists(a)||!Store.Supported(a)||Store.Supported("x.txt"))throw new Exception("Store validation failed");
            using(var image=Store.Load(a)) { if(image.Width!=40||image.Height!=30||image.GetPixel(0,0).ToArgb()!=Color.Coral.ToArgb())throw new Exception("Image round trip failed"); File.Delete(a); }
            using(var image=Store.Load(b)) using(var editor=new Editor(image)) { if(editor.Result.Width!=40)throw new Exception("Editor initialization failed"); if(Store.Fingerprint(image)!=Store.Fingerprint(editor.Result))throw new Exception("Duplicate fingerprint failed"); using(var rendered=new Bitmap(editor.Width,editor.Height)) editor.DrawToBitmap(rendered,new Rectangle(Point.Empty,editor.Size)); }
            using(var image=Store.Load(b)) using(var transfer=new ImageTransfer(image,b,true)) {
                foreach(string format in new[] { "PNG", "image/png", DataFormats.Bitmap, DataFormats.Dib, DataFormats.FileDrop })if(!transfer.Data.GetDataPresent(format,false))throw new Exception("Missing drag format: "+format);
                var png=transfer.Data.GetData("PNG",false) as Stream; using(var decoded=Image.FromStream(png))if(decoded.Width!=40||decoded.Height!=30)throw new Exception("PNG transfer corrupted");
                var dib=transfer.Data.GetData(DataFormats.Dib,false) as Stream; using(var reader=new BinaryReader(dib,System.Text.Encoding.UTF8,true))if(reader.ReadInt32()!=40||reader.ReadInt32()!=40||reader.ReadInt32()!=30)throw new Exception("DIB header corrupted");
                if(((string[])transfer.Data.GetData(DataFormats.FileDrop))[0]!=b)throw new Exception("File transfer corrupted");
                // Exercise the actual OLE representation used by external drop targets.
                foreach(string name in new[] { "PNG", DataFormats.Dib, DataFormats.FileDrop }) {
                    var format=new System.Runtime.InteropServices.ComTypes.FORMATETC { cfFormat=unchecked((short)DataFormats.GetFormat(name).Id), dwAspect=System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT, lindex=-1, tymed=System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL };
                    var ole=(System.Runtime.InteropServices.ComTypes.IDataObject)transfer.Data;
                    if(ole.QueryGetData(ref format)!=0)throw new Exception("OLE format unavailable: "+name);
                    System.Runtime.InteropServices.ComTypes.STGMEDIUM medium; ole.GetData(ref format,out medium);
                    try { IntPtr memory=Native.GlobalLock(medium.unionmember); if(memory==IntPtr.Zero)throw new Exception("OLE memory unavailable"); try { int header=Marshal.ReadInt32(memory); if(name=="PNG" && header!=unchecked((int)0x474E5089))throw new Exception("OLE PNG corrupted"); if(name==DataFormats.Dib && header!=40)throw new Exception("OLE DIB corrupted"); } finally { Native.GlobalUnlock(medium.unionmember); } } finally { Native.ReleaseStgMedium(ref medium); }
                }
            }
            LineArt.Preview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-preview.png"));
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.txt"),"PASS: rope positions and multi-photo spacing, animation settling and reversal, selection bounds, image persistence, duplicate detection, editor rendering, OLE image/file formats and glass UI rendering.");
        } finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
}
