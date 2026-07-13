using Bot.Capture;
using Bot.Navigation;
using Bot.State;
using OpenCvSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bot;

public sealed class BotController
{
    private readonly BotRuntime _runtime;
    private BotContext Ctx => _runtime.Ctx;
    public BotContext Context => Ctx;
    private BotServices Svc => _runtime.Svc;


    private readonly BotBrain _brain;
    private CancellationTokenSource? _loopCts;
    private bool _initialized = false;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("psapi.dll")]
    private static extern bool EnumProcessModules(IntPtr hProcess, IntPtr[] lphModule, uint cb, out uint lpcbNeeded);

    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_VM_READ = 0x0010;


    public event Action<string>? StatusChanged;
    public event Action<IEnumerable<string>>? WayPointsUpdated;

    private Stopwatch _statsTimer = new();
    public BotController(BotRuntime runtime)
    {
        _runtime = runtime;
        _brain = new BotBrain(runtime);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            Console.WriteLine("[Bot] Capture already initialized.");
            return;
        }

        _initialized = true;
        Svc.Capture.Start();

        StatusChanged?.Invoke("Preloading assets and maps...");

        // kick off background load right away
        var loadTask = Task.Run(async () =>
        {
            // assets
            var lootFolder = "Assets/Loot";
            var foodFolder = "Assets/Food";
            var floorLootFolder = "Assets/FloorLoot";

            Ctx.OneHundredGold = Cv2.ImRead("Assets/Tools/FullStackGp.png", ImreadModes.Grayscale);
            Ctx.BackpackTemplate = Cv2.ImRead("Assets/Tools/Backpack.png", ImreadModes.Grayscale);
            Ctx.BagTemplate = Cv2.ImRead("Assets/Tools/Bag.png", ImreadModes.Grayscale);
            Ctx.RopeTemplate = Cv2.ImRead("Assets/Tools/Rope.png", ImreadModes.Grayscale);
            Ctx.ShovelTemplate = Cv2.ImRead("Assets/Tools/Shovel.png", ImreadModes.Grayscale);
            Ctx.UhTemplate = Cv2.ImRead("Assets/Runes/Uh.png", ImreadModes.Grayscale);

            var messageToPath = "Assets/Chat/MessageTo.png";
            if (File.Exists(messageToPath))
            {
                Ctx.MessageToTemplate = Cv2.ImRead(messageToPath, ImreadModes.Grayscale);
                Console.WriteLine("[Controller] Loaded MessageTo template");
            }

            Ctx.DefaultTabTemplate = LoadOptionalAsset("Assets/Chat/DefaultTab.png");
            Ctx.DefaultTabUnfocusedTemplate = LoadOptionalAsset("Assets/Chat/DefaultTabUnfocused.png");
            Ctx.TabLeftEdgeTemplate = LoadOptionalAsset("Assets/Chat/LeftEdge.png");
            Ctx.TabLeftEdgeUnfocusedTemplate = LoadOptionalAsset("Assets/Chat/LeftEdgeUnfocused.png");
            Ctx.TabRightEdgeTemplate = LoadOptionalAsset("Assets/Chat/RightEdge.png");
            Ctx.TabRightEdgeUnfocusedTemplate = LoadOptionalAsset("Assets/Chat/RightEdgeUnfocused.png");
            Ctx.CloseTabTemplate = LoadOptionalAsset("Assets/Chat/CloseTab.png");

            Ctx.LootTemplates = Directory.GetFiles(lootFolder, "*.png")
                .Select(path => Cv2.ImRead(path, ImreadModes.Grayscale))
                .ToArray();

            Ctx.FoodTemplates = Directory.GetFiles(foodFolder, "*.png")
                .Select(path => Cv2.ImRead(path, ImreadModes.Grayscale))
                .ToArray();

            Ctx.FloorLootTemplates = Directory.GetFiles(floorLootFolder, "*.png")
                .Select(path => Cv2.ImRead(path, ImreadModes.Grayscale))
                .ToArray();

            Console.WriteLine("[Controller] Cached assets");

            Svc.MapRepo.LoadAll("Assets/Minimaps");

            ValidateAssets();
        });

        // while maps/assets load, show process picker
        var tibia = PromptForTargetProcess();
        if (tibia == null || tibia.MainWindowHandle == IntPtr.Zero)
            throw new InvalidOperationException("No valid process selected.");

        Ctx.GameWindowHandle = tibia.MainWindowHandle;
        Ctx.ProcessId = tibia.Id;
        Ctx.ProcessMemoryBaseAddress = GetBaseAddress(tibia.Id);
        tibia.Dispose();

        Console.WriteLine($"[Controller] Attached to process window handle.");

        StatusChanged?.Invoke("Warming up capture...");
        for (int i = 0; i < 3; i++)
        {
            using var warm = Svc.Capture.CaptureSingleFrame();
            await Task.Delay(30);
        }

        // wait for background work if not done yet
        await loadTask;

        //start main loop
        _loopCts = new CancellationTokenSource();
        _statsTimer.Start();
        _ = Task.Run(() => MainLoop(_loopCts.Token));

        Console.WriteLine("[Controller] Started main loop");
        StatusChanged?.Invoke("MainLoop running...");
    }

    private async Task MainLoop(CancellationToken token)
    {
        const int TickRateMs = 45;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var start = DateTime.UtcNow;

                if (_statsTimer.ElapsedMilliseconds > 60000)
                {
                    _statsTimer.Restart();

                    var proc = Process.GetCurrentProcess();
                    
                    Console.WriteLine(
                        $"[MEM] WS={proc.WorkingSet64 / 1024 / 1024}MB " +
                        $"PM={proc.PrivateMemorySize64 / 1024 / 1024}MB " +
                        $"GC0={GC.CollectionCount(0)} " +
                        $"GC1={GC.CollectionCount(1)} " +
                        $"GC2={GC.CollectionCount(2)}"
                    );
                }

                if (!ShouldSuspend())
                {
                    var frame = Svc.Capture?.GetLatestFrameCopy();
                    if(frame != null)
                        _brain.ProcessFrame(frame);
                }

                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                var delay = Math.Max(0, TickRateMs - (int)elapsed);
                await Task.Delay(delay, token);
            }
        }
        catch (TaskCanceledException) { } //Shutdown 
        catch (Exception ex)
        {
            Console.WriteLine("[MainLoop] CRASH: " + ex);
        }
    }

    public void Start() 
    {
        _brain.StartBot();
        StatusChanged?.Invoke($"Running...");
    }
    public void Stop() 
    { 
        _brain.StopBot();
        StatusChanged?.Invoke($"Stopped...");
    }

    public void ResetState()
    {
        _brain.ResetState();
        StatusChanged?.Invoke("State reset");
    }

    public void ToggleRecord() 
    {
        Ctx.RecordMode = !Ctx.RecordMode;
    }
    public void AddWaypoint()
    {
        if (!Ctx.PlayerPosition.IsValid)
        {
            Console.WriteLine("[Bot] Cannot add waypoint – player position unknown.");
            return;
        }

        Svc.PathRepo.Add(new Waypoint(
            WaypointType.Move,
            Ctx.PlayerPosition.X,
            Ctx.PlayerPosition.Y,
            Ctx.PlayerPosition.Z));
        WayPointsUpdated?.Invoke(GetWaypoints());
    }
    public void AddRamp(Direction dir)
    {
        if (!Ctx.PlayerPosition.IsValid)
        {
            Console.WriteLine("[Bot] Cannot add ramp – player position unknown.");
            return;
        }

        Svc.PathRepo.Add(new Waypoint(
            WaypointType.Step,
            Ctx.PlayerPosition.X,
            Ctx.PlayerPosition.Y,
            Ctx.PlayerPosition.Z,
            dir));
        WayPointsUpdated?.Invoke(GetWaypoints());
    }
    public void AddClickTile(Direction dir)
    {
        if (!Ctx.PlayerPosition.IsValid)
        {
            Console.WriteLine("[Bot] Cannot add click in tile – player position unknown.");
            return;
        }

        Svc.PathRepo.Add(new Waypoint(
            WaypointType.RightClick,
            Ctx.PlayerPosition.X,
            Ctx.PlayerPosition.Y,
            Ctx.PlayerPosition.Z,
            dir));
        WayPointsUpdated?.Invoke(GetWaypoints());
    }
    public void AddUseItemInTile(Direction dir, Item item)
    {
        if (!Ctx.PlayerPosition.IsValid)
        {
            Console.WriteLine("[Bot] Cannot add useItem in tile – player position unknown.");
            return;
        }

        Svc.PathRepo.Add(new Waypoint(
            WaypointType.UseItem,
            Ctx.PlayerPosition.X,
            Ctx.PlayerPosition.Y,
            Ctx.PlayerPosition.Z,
            dir,
            item));
        WayPointsUpdated?.Invoke(GetWaypoints());
    }

    public void SavePath(string path)
    {
        Svc.PathRepo.SaveToJson(path);
        WayPointsUpdated?.Invoke(GetWaypoints());
    }
    public void LoadPath(string path)
    {
        Svc.PathRepo.LoadFromJson(path);
        WayPointsUpdated?.Invoke(GetWaypoints());
    }

    public List<string> GetWaypoints()
    {
        var list = Svc.PathRepo.Waypoints
            .Select(wp => $"{wp.Type} {(wp.Type == WaypointType.Move ? $"({wp.X},{wp.Y},{wp.Z})" : $"{wp.Dir}")}")
            .ToList();

        return list;
    }

    public static Process? PromptForTargetProcess()
    {
        var processes = Process.GetProcesses()
            .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
            .OrderBy(p => p.ProcessName)
            .ToList();

        var form = new Form
        {
            Width = 420,
            Height = 400,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(35, 35, 35),
            ShowIcon = false,
            ShowInTaskbar = false,
        };

        // --- Colors ---
        Color Purple = Color.FromArgb(191, 185, 255);
        Color Pink = Color.FromArgb(255, 207, 234);
        Color WGray = Color.FromArgb(192, 192, 192);
        Color Yellow = Color.FromArgb(254, 255, 190);
        string FontName = "Lucida Console";

        // --- Paint gradient background ---
        form.Paint += (s, e) =>
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                form.ClientRectangle,
                Color.FromArgb(175, 233, 255),
                Color.FromArgb(255, 207, 234),
                90f);
            e.Graphics.FillRectangle(brush, form.ClientRectangle);
            // Draw border in SystemColors.Control
            using var pen = new Pen(SystemColors.Control, 2);
            e.Graphics.DrawRectangle(
                pen,
                new Rectangle(1, 1, form.ClientSize.Width - 2, form.ClientSize.Height - 2));
        };

        // --- Title bar ---
        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32
        };
        titleBar.Paint += (s, e) =>
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                titleBar.ClientRectangle, Purple, Pink, 0f);
            e.Graphics.FillRectangle(brush, titleBar.ClientRectangle);
            ControlPaint.DrawBorder(e.Graphics, titleBar.ClientRectangle, WGray, ButtonBorderStyle.Solid);
        };

        var titleLabel = new Label
        {
            Text = "Select Game Window",
            ForeColor = Color.White,
            Font = new Font(FontName, 10, FontStyle.Bold),
            AutoSize = true,
            Location = new System.Drawing.Point(14, 8),
            BackColor = Color.Transparent
        };
        titleBar.Controls.Add(titleLabel);

        System.Drawing.Point dragStart = System.Drawing.Point.Empty;
        titleBar.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                dragStart = new System.Drawing.Point(e.X, e.Y);
        };
        titleBar.MouseMove += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                form.Left += e.X - dragStart.X;
                form.Top += e.Y - dragStart.Y;
            }
        };

        form.Controls.Add(titleBar);

        // --- Main content container (under title bar) ---
        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 8),
            BackColor = Color.Transparent
        };

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontName, 10),
            BackColor = SystemColors.Control,
            ForeColor = Color.FromArgb(191, 185, 255),
            BorderStyle = BorderStyle.FixedSingle
        };
        foreach (var p in processes)
            list.Items.Add($"{p.ProcessName}  —  {p.MainWindowTitle}");

        var btnAttach = new Button
        {
            Text = "Attach",
            Width = 120,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = SystemColors.Control,
            ForeColor = Color.Black,
            Font = new Font(FontName, 9),
            Anchor = AnchorStyles.None
        };
        btnAttach.FlatAppearance.BorderColor = Yellow;
        btnAttach.FlatAppearance.BorderSize = 1;
        btnAttach.MouseEnter += (s, e) => btnAttach.BackColor = Pink;
        btnAttach.MouseLeave += (s, e) => btnAttach.BackColor = SystemColors.Control;
        btnAttach.Click += (s, e) => form.DialogResult = DialogResult.OK;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.Controls.Add(list, 0, 0);
        layout.Controls.Add(btnAttach, 0, 1);
        contentPanel.Controls.Add(layout);

        form.Controls.Add(contentPanel);

        var result = form.ShowDialog();

        Process? selected = null;
        if (result == DialogResult.OK && list.SelectedIndex >= 0)
            selected = processes[list.SelectedIndex];

        foreach (var p in processes)
            if (p != selected)
                p.Dispose();

        form.Dispose();
        return selected;
    }

    private static nint GetBaseAddress(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        try
        {
            var modules = new IntPtr[1];
            if (EnumProcessModules(handle, modules, (uint)(IntPtr.Size * modules.Length), out _))
                return modules[0];
            return IntPtr.Zero;
        }
        finally
        {
            CloseHandle(handle);
        }
    }



    private static Mat? LoadOptionalAsset(string path)
    {
        if (!File.Exists(path)) return null;
        var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (mat.Empty()) { mat.Dispose(); return null; }
        Console.WriteLine($"[Controller] Loaded optional asset: {path}");
        return mat;
    }

    private void ValidateAssets()
    {
        static void Check(Mat mat, string name)
        {
            if (mat == null || mat.Empty())
                throw new InvalidOperationException($"Failed to load required asset: {name}");
        }

        Check(Ctx.OneHundredGold, "Assets/Tools/FullStackGp.png");
        Check(Ctx.BackpackTemplate, "Assets/Tools/Backpack.png");
        Check(Ctx.BagTemplate, "Assets/Tools/Bag.png");
        Check(Ctx.RopeTemplate, "Assets/Tools/Rope.png");
        Check(Ctx.ShovelTemplate, "Assets/Tools/Shovel.png");
        Check(Ctx.UhTemplate, "Assets/Runes/Uh.png");

        if (Ctx.LootTemplates.Length == 0)
            throw new InvalidOperationException("No loot templates found in Assets/Loot/");
        if (Ctx.FoodTemplates.Length == 0)
            throw new InvalidOperationException("No food templates found in Assets/Food/");
    }

    private bool ShouldSuspend()
    {
        var active = GetForegroundWindow();
        if (active != Ctx.GameWindowHandle) return true;
        if (IsIconic(Ctx.GameWindowHandle)) return true;
        return false;
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();

        Svc.Dispose();

        Ctx.RopeTemplate?.Dispose();
        Ctx.ShovelTemplate?.Dispose();
        Ctx.BackpackTemplate?.Dispose();
        Ctx.BagTemplate?.Dispose();
        Ctx.OneHundredGold?.Dispose();
        Ctx.UhTemplate?.Dispose();
        Ctx.MessageToTemplate?.Dispose();
        Ctx.DefaultTabTemplate?.Dispose();
        Ctx.DefaultTabUnfocusedTemplate?.Dispose();
        Ctx.TabLeftEdgeTemplate?.Dispose();
        Ctx.TabLeftEdgeUnfocusedTemplate?.Dispose();
        Ctx.TabRightEdgeTemplate?.Dispose();
        Ctx.TabRightEdgeUnfocusedTemplate?.Dispose();
        Ctx.CloseTabTemplate?.Dispose();

        if (Ctx.LootTemplates != null)
            foreach (var m in Ctx.LootTemplates)
                m?.Dispose();

        if (Ctx.FoodTemplates != null)
            foreach (var m in Ctx.FoodTemplates)
                m?.Dispose();

        if (Ctx.FloorLootTemplates != null)
            foreach (var m in Ctx.FloorLootTemplates)
                m?.Dispose();
        Ctx.CurrentFrame?.Dispose();
        Ctx.CurrentFrameGray?.Dispose();
    }
}