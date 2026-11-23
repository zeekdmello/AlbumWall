// Album Wall: a Windows screensaver that shows a moving wall of album covers.
// A .scr file is an ordinary .exe that Windows starts with one of these arguments:
//   /s          run full screen
//   /p <hwnd>   draw inside the small preview box in Screen Saver Settings
//   /c          show the settings dialog (also used when there are no arguments)
// One extra argument exists for testing: /snapshot <file.png> renders a frame off-screen.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Shade = System.Windows.Shapes.Rectangle;
using WF = System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);

    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
}

sealed class Settings
{
    const string Key = @"HKEY_CURRENT_USER\Software\AlbumWall";

    public string Folder;
    public int Rows;
    public int Speed; // 0 slow, 1 medium, 2 fast

    public double DriftPerSecond { get { return new double[] { 14, 30, 60 }[Speed]; } }

    public static string DefaultFolder()
    {
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string apple = Path.Combine(music, "Apple Music", "Apple Music Library.musiclibrary", "artwork");
        return Directory.Exists(apple) ? apple : music;
    }

    public static Settings Load()
    {
        var s = new Settings();
        s.Folder = Registry.GetValue(Key, "Folder", null) as string;
        if (string.IsNullOrEmpty(s.Folder)) s.Folder = DefaultFolder();
        s.Rows = Clamp(Registry.GetValue(Key, "Rows", 5), 2, 10, 5);
        s.Speed = Clamp(Registry.GetValue(Key, "Speed", 1), 0, 2, 1);
        return s;
    }

    public void Save()
    {
        Registry.SetValue(Key, "Folder", Folder);
        Registry.SetValue(Key, "Rows", Rows, RegistryValueKind.DWord);
        Registry.SetValue(Key, "Speed", Speed, RegistryValueKind.DWord);
    }

    static int Clamp(object value, int min, int max, int fallback)
    {
        if (!(value is int)) return fallback;
        return Math.Max(min, Math.Min(max, (int)value));
    }
}

// Finds the covers and decodes them on a background thread, so the animation never waits on disk.
sealed class CoverSource
{
    static readonly HashSet<string> Extensions =
        new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".bmp" }, StringComparer.OrdinalIgnoreCase);

    readonly BlockingCollection<BitmapSource> ready = new BlockingCollection<BitmapSource>(32);
    readonly string[] files;
    readonly Random random = new Random();

    public CoverSource(string folder) { files = FindImages(folder); }

    public int Count { get { return files.Length; } }

    public void Start(int decodePixels)
    {
        if (files.Length == 0) return;
        var worker = new Thread(() => DecodeLoop(decodePixels));
        worker.IsBackground = true;
        worker.Priority = ThreadPriority.BelowNormal;
        worker.Start();
    }

    public BitmapSource TryNext()
    {
        BitmapSource cover;
        return ready.TryTake(out cover) ? cover : null;
    }

    // Apple Music can keep the same cover under several names. Identical byte length is a
    // cheap and reliable enough test, since unrelated JPEGs almost never match exactly.
    public static string[] FindImages(string folder)
    {
        var found = new List<FileInfo>();
        Walk(folder, found);
        return found.GroupBy(f => f.Length).Select(g => g.First().FullName).ToArray();
    }

    static void Walk(string folder, List<FileInfo> found)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            foreach (var file in dir.EnumerateFiles())
                if (Extensions.Contains(file.Extension)) found.Add(file);
            foreach (var sub in dir.EnumerateDirectories())
                Walk(sub.FullName, found);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    void DecodeLoop(int decodePixels)
    {
        int failures = 0;
        while (failures < files.Length)
        {
            Shuffle();
            foreach (string file in files)
            {
                BitmapSource cover = Decode(file, decodePixels);
                if (cover == null) { failures++; continue; }
                failures = 0;
                ready.Add(cover);
            }
        }
    }

    static BitmapSource Decode(string file, int decodePixels)
    {
        try
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = decodePixels;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        catch (Exception e)
        {
            if (e is IOException || e is NotSupportedException || e is ArgumentException ||
                e is UnauthorizedAccessException || e is InvalidOperationException ||
                e is System.IO.FileFormatException)
                return null;
            throw;
        }
    }

    void Shuffle()
    {
        for (int i = files.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            string swap = files[i]; files[i] = files[j]; files[j] = swap;
        }
    }
}

sealed class Tile
{
    public Grid Root;
    public Image Art;
    public Shade Darken;
    public ScaleTransform Scale;
    public TranslateTransform Move;
    public int Slot;
    public long Wraps;
    public double X;
    public double IntroDelay;
    public double FlipTime = -1; // below zero means not flipping
    public bool Swapped;
    public BitmapSource NextCover;
}

sealed class Row
{
    public List<Tile> Tiles = new List<Tile>();
    public double Offset;
    public double Speed;
}

sealed class Wall : Grid
{
    const double Gap = 3;
    const double FlipSeconds = 1.0;

    readonly CoverSource covers;
    readonly double width, height, tile, pitch;
    readonly List<Row> rows = new List<Row>();
    readonly Random random = new Random();
    readonly Stopwatch clock = new Stopwatch();
    double nextFlipIn = 1.5;
    int slots;

    public double TilePixelsAtDpi(double scale) { return tile * scale; }

    public Wall(CoverSource covers, double width, double height, int rowCount, double drift)
    {
        this.covers = covers;
        this.width = width;
        this.height = height;
        Width = width;
        Height = height;
        Background = Brushes.Black;
        ClipToBounds = true;

        tile = (height - Gap * (rowCount - 1)) / rowCount;
        pitch = tile + Gap;
        slots = (int)Math.Ceiling(width / pitch) + 2;

        var canvas = new Canvas();
        Children.Add(canvas);

        for (int r = 0; r < rowCount; r++)
        {
            var row = new Row();
            // Neighbouring rows travel in opposite directions at slightly different speeds.
            row.Speed = drift * (r % 2 == 0 ? 1 : -1) * (0.8 + 0.4 * random.NextDouble());
            for (int s = 0; s < slots; s++)
            {
                Tile t = MakeTile(s);
                Canvas.SetTop(t.Root, r * pitch);
                canvas.Children.Add(t.Root);
                row.Tiles.Add(t);
            }
            rows.Add(row);
        }

        Children.Add(MakeVignette());

        if (covers.Count == 0)
        {
            var message = new TextBlock();
            message.Text = "No album art found.\nChoose a folder in the screensaver's Settings.";
            message.Foreground = Brushes.White;
            message.FontSize = Math.Max(11, height / 30);
            message.TextAlignment = TextAlignment.Center;
            message.HorizontalAlignment = HorizontalAlignment.Center;
            message.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(message);
        }
    }

    Tile MakeTile(int slot)
    {
        var t = new Tile();
        t.Slot = slot;
        t.IntroDelay = random.NextDouble() * 1.4;

        t.Art = new Image();
        t.Art.Stretch = Stretch.UniformToFill;
        RenderOptions.SetBitmapScalingMode(t.Art, BitmapScalingMode.Linear);

        t.Darken = new Shade();
        t.Darken.Fill = Brushes.Black;
        t.Darken.Opacity = 0;

        t.Scale = new ScaleTransform(0, 1);
        t.Move = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(t.Scale);
        transforms.Children.Add(t.Move);

        t.Root = new Grid();
        t.Root.Width = tile;
        t.Root.Height = tile;
        t.Root.ClipToBounds = true;
        t.Root.RenderTransformOrigin = new Point(0.5, 0.5);
        t.Root.RenderTransform = transforms;
        t.Root.Children.Add(t.Art);
        t.Root.Children.Add(t.Darken);
        return t;
    }

    static Shade MakeVignette()
    {
        var brush = new RadialGradientBrush();
        brush.RadiusX = 0.75;
        brush.RadiusY = 0.75;
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(140, 0, 0, 0), 1.0));
        brush.Freeze();
        var overlay = new Shade();
        overlay.Fill = brush;
        overlay.IsHitTestVisible = false;
        return overlay;
    }

    public void StartAnimating()
    {
        clock.Start();
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object sender, EventArgs e)
    {
        double dt = clock.Elapsed.TotalSeconds;
        clock.Restart();
        Tick(Math.Min(dt, 0.1));
    }

    public void Tick(double dt)
    {
        double loop = slots * pitch;
        foreach (Row row in rows)
        {
            row.Offset += row.Speed * dt;
            foreach (Tile t in row.Tiles)
            {
                double raw = t.Slot * pitch + row.Offset;
                long wraps = (long)Math.Floor(raw / loop);
                t.X = raw - wraps * loop - pitch;
                t.Move.X = t.X;

                // A tile that just wrapped is off-screen, so it can change cover unseen.
                if (wraps != t.Wraps)
                {
                    t.Wraps = wraps;
                    if (t.FlipTime < 0)
                    {
                        BitmapSource fresh = covers.TryNext();
                        if (fresh != null) t.Art.Source = fresh;
                    }
                }
                Animate(t, dt);
            }
        }

        nextFlipIn -= dt;
        if (nextFlipIn <= 0)
        {
            nextFlipIn = 0.25 + random.NextDouble() * 0.5;
            StartRandomFlip();
        }
    }

    void Animate(Tile t, double dt)
    {
        if (t.Art.Source == null)
        {
            // The wall assembles itself as covers arrive from the decoder.
            t.IntroDelay -= dt;
            if (t.IntroDelay > 0) return;
            BitmapSource first = covers.TryNext();
            if (first == null) return;
            t.Art.Source = first;
            t.FlipTime = 0.5; // start at the edge-on midpoint, so only the second half plays
            t.Swapped = true;
        }

        if (t.FlipTime < 0) return;

        t.FlipTime += dt / FlipSeconds;
        if (t.FlipTime >= 1)
        {
            t.FlipTime = -1;
            t.Scale.ScaleX = 1;
            t.Darken.Opacity = 0;
            return;
        }

        double p = t.FlipTime;
        double eased = p * p * (3 - 2 * p);
        double angle = eased * Math.PI;
        t.Scale.ScaleX = Math.Max(0.001, Math.Abs(Math.Cos(angle)));
        t.Darken.Opacity = Math.Sin(angle) * 0.6;

        if (!t.Swapped && eased >= 0.5)
        {
            t.Art.Source = t.NextCover;
            t.NextCover = null;
            t.Swapped = true;
        }
    }

    void StartRandomFlip()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Row row = rows[random.Next(rows.Count)];
            Tile t = row.Tiles[random.Next(row.Tiles.Count)];
            bool visible = t.X >= 0 && t.X + tile <= width;
            if (!visible || t.FlipTime >= 0 || t.Art.Source == null) continue;

            BitmapSource next = covers.TryNext();
            if (next == null) return;
            t.NextCover = next;
            t.Swapped = false;
            t.FlipTime = 0;
            return;
        }
    }
}

sealed class SettingsForm : WF.Form
{
    readonly Settings settings;
    readonly WF.TextBox folder = new WF.TextBox();
    readonly WF.Label found = new WF.Label();
    readonly WF.NumericUpDown rows = new WF.NumericUpDown();
    readonly WF.ComboBox speed = new WF.ComboBox();

    public SettingsForm(Settings settings)
    {
        this.settings = settings;
        Text = "Album Wall Settings";
        FormBorderStyle = WF.FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = WF.FormStartPosition.CenterScreen;
        AutoScaleMode = WF.AutoScaleMode.Dpi;
        ClientSize = new System.Drawing.Size(480, 210);

        Add(new WF.Label { Text = "Folder with album covers:", Left = 14, Top = 14, Width = 450 });

        folder.SetBounds(14, 36, 360, 24);
        folder.Text = settings.Folder;
        folder.TextChanged += (s, e) => UpdateCount();
        Add(folder);

        var browse = new WF.Button { Text = "Browse...", Left = 382, Top = 34, Width = 84, Height = 28 };
        browse.Click += OnBrowse;
        Add(browse);

        found.SetBounds(14, 64, 450, 20);
        Add(found);

        Add(new WF.Label { Text = "Rows of albums:", Left = 14, Top = 98, Width = 130 });
        rows.SetBounds(150, 96, 70, 24);
        rows.Minimum = 2;
        rows.Maximum = 10;
        rows.Value = settings.Rows;
        Add(rows);

        Add(new WF.Label { Text = "Movement speed:", Left = 14, Top = 132, Width = 130 });
        speed.SetBounds(150, 130, 110, 24);
        speed.DropDownStyle = WF.ComboBoxStyle.DropDownList;
        speed.Items.AddRange(new object[] { "Slow", "Medium", "Fast" });
        speed.SelectedIndex = settings.Speed;
        Add(speed);

        var ok = new WF.Button { Text = "OK", Left = 300, Top = 170, Width = 80, Height = 28, DialogResult = WF.DialogResult.OK };
        ok.Click += (s, e) => Save();
        Add(ok);
        var cancel = new WF.Button { Text = "Cancel", Left = 386, Top = 170, Width = 80, Height = 28, DialogResult = WF.DialogResult.Cancel };
        Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        UpdateCount();
    }

    void Add(WF.Control control) { Controls.Add(control); }

    void OnBrowse(object sender, EventArgs e)
    {
        using (var dialog = new WF.FolderBrowserDialog())
        {
            dialog.SelectedPath = folder.Text;
            if (dialog.ShowDialog(this) == WF.DialogResult.OK) folder.Text = dialog.SelectedPath;
        }
    }

    void UpdateCount()
    {
        found.Text = Directory.Exists(folder.Text)
            ? CoverSource.FindImages(folder.Text).Length.ToString("N0") + " covers found"
            : "That folder does not exist";
    }

    void Save()
    {
        settings.Folder = folder.Text;
        settings.Rows = (int)rows.Value;
        settings.Speed = speed.SelectedIndex;
        settings.Save();
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();

        string option = args.Length > 0 ? args[0].Trim().TrimStart('/', '-').ToLowerInvariant() : "c";
        string value = null;
        int colon = option.IndexOf(':');
        if (colon >= 0)
        {
            value = option.Substring(colon + 1);
            option = option.Substring(0, colon);
        }
        else if (args.Length > 1)
        {
            value = args[1];
        }

        Settings settings = Settings.Load();

        if (option == "snapshot") return Snapshot(settings, value);
        if (option == "s") { RunFullScreen(settings); return 0; }
        if (option == "p") { RunPreview(settings, value); return 0; }

        WF.Application.EnableVisualStyles();
        WF.Application.Run(new SettingsForm(settings));
        return 0;
    }

    static double DipScale()
    {
        return WF.Screen.PrimaryScreen.Bounds.Width / SystemParameters.PrimaryScreenWidth;
    }

    static void RunFullScreen(Settings settings)
    {
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        double scale = DipScale();
        var covers = new CoverSource(settings.Folder);
        double tallest = 0;
        var walls = new List<Wall>();

        foreach (WF.Screen screen in WF.Screen.AllScreens)
        {
            double w = screen.Bounds.Width / scale;
            double h = screen.Bounds.Height / scale;

            var wall = new Wall(covers, w, h, settings.Rows, settings.DriftPerSecond);
            walls.Add(wall);
            tallest = Math.Max(tallest, wall.TilePixelsAtDpi(scale));

            var window = new Window();
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = screen.Bounds.Left / scale;
            window.Top = screen.Bounds.Top / scale;
            window.Width = w;
            window.Height = h;
            window.Topmost = true;
            window.ShowInTaskbar = false;
            window.Background = Brushes.Black;
            window.Cursor = Cursors.None;
            window.Content = wall;
            CloseOnInput(window);
            window.Show();
            window.Activate();
        }

        covers.Start((int)Math.Ceiling(tallest));
        foreach (Wall wall in walls) wall.StartAnimating();
        app.Run();
    }

    static void CloseOnInput(Window window)
    {
        Point? start = null;
        window.PreviewMouseMove += (s, e) =>
        {
            Point now = e.GetPosition(window);
            if (start == null) { start = now; return; } // the first event only reports where the mouse is
            Vector moved = now - start.Value;
            if (moved.Length > 8) Application.Current.Shutdown();
        };
        window.PreviewMouseDown += (s, e) => Application.Current.Shutdown();
        window.PreviewKeyDown += (s, e) => Application.Current.Shutdown();
    }

    static void RunPreview(Settings settings, string handle)
    {
        long parsed;
        if (!long.TryParse(handle, out parsed)) return;
        var parent = new IntPtr(parsed);

        Native.RECT rect;
        if (!Native.GetClientRect(parent, out rect)) return;
        int pixelWidth = rect.Right - rect.Left;
        int pixelHeight = rect.Bottom - rect.Top;

        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var parameters = new HwndSourceParameters("AlbumWallPreview", pixelWidth, pixelHeight);
        parameters.ParentWindow = parent;
        parameters.WindowStyle = Native.WS_CHILD | Native.WS_VISIBLE;
        var source = new HwndSource(parameters);

        double scale = DipScale();
        var covers = new CoverSource(settings.Folder);
        var wall = new Wall(covers, pixelWidth / scale, pixelHeight / scale, 3, settings.DriftPerSecond);
        source.RootVisual = wall;
        covers.Start((int)Math.Ceiling(wall.TilePixelsAtDpi(scale)));
        wall.StartAnimating();

        // The settings dialog just destroys its preview box, so watch for that and leave.
        var watchdog = new DispatcherTimer();
        watchdog.Interval = TimeSpan.FromMilliseconds(500);
        watchdog.Tick += (s, e) => { if (!Native.IsWindow(parent)) app.Shutdown(); };
        watchdog.Start();

        app.Run();
    }

    static int Snapshot(Settings settings, string path)
    {
        if (string.IsNullOrEmpty(path)) return 2;

        const int W = 1920, H = 1080;
        var covers = new CoverSource(settings.Folder);
        var wall = new Wall(covers, W, H, settings.Rows, settings.DriftPerSecond);
        covers.Start((int)Math.Ceiling(wall.TilePixelsAtDpi(1)));

        for (int i = 0; i < 300; i++)
        {
            wall.Tick(1 / 30.0);
            Thread.Sleep(10);
        }

        wall.Measure(new Size(W, H));
        wall.Arrange(new Rect(0, 0, W, H));
        wall.UpdateLayout();

        var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(wall);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        return 0;
    }
}
