using Bot.Navigation;
using Bot.State;
using Bot.Ui;

namespace Bot;

public partial class MainForm : Form
{
    private ProfileSettings Profile => _controller.Context.Profile;
    private readonly string FontName = "Lucida Console";
    private readonly Color Pink = Color.FromArgb(255, 207, 234);
    private readonly Color Blue = Color.FromArgb(175, 233, 255);
    private readonly Color Teal = Color.FromArgb(203, 255, 230);
    private readonly Color Purple = Color.FromArgb(191, 185, 255);
    private readonly Color Yellow = Color.FromArgb(254, 255, 190);
    private readonly Color WGray = Color.FromArgb(192, 192, 192);
    private Label _titleLabel = null!;
    private TabControl _tabs = null!;
    private Panel _titleBar = null!;
    private Button _closeBtn = null!;
    private Button _minimizeBtn = null!;
    private Point _dragStart;
    private const string AppTitle = "";

    private readonly BotController _controller;

    public MainForm(BotController controller)
    {
        _controller = controller;

        Text = AppTitle;
        Width = 900;
        Height = 550;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(35, 35, 35);
        DoubleBuffered = true;
        ShowInTaskbar = false;

        BuildTitleBar();
        BuildTabs();

        _controller.StatusChanged += msg =>
        {
            if (IsHandleCreated)
                Console.WriteLine($"Status changed: {msg}");
                //BeginInvoke(() => _titleLabel.Text = $"TBot — {msg}");
        };
    }

    private void BuildTitleBar()
    {
        _titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32
        };

        // draw gradient background
        _titleBar.Paint += (s, e) =>
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                _titleBar.ClientRectangle, Purple, Pink, 0f);
            e.Graphics.FillRectangle(brush, _titleBar.ClientRectangle);

            ControlPaint.DrawBorder(
                e.Graphics,
                _titleBar.ClientRectangle,
                WGray,
                ButtonBorderStyle.Solid);
        };

        _titleLabel = new Label
        {
            Text = AppTitle,
            ForeColor = Color.White,
            Font = new Font(FontName, 11, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(14, 8),
            BackColor = Color.Transparent
        };
        _titleBar.Controls.Add(_titleLabel);

        _closeBtn = new Button
        {
            Text = "✖",
            FlatStyle = FlatStyle.Flat,
            Size = new Size(26, 22),
            ForeColor = Color.Black,
            BackColor = SystemColors.Control,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(ClientSize.Width - 30, 5),
            TabStop = false,
            CausesValidation = false,
        };
        _closeBtn.FlatAppearance.BorderColor = Color.DarkGray;
        _closeBtn.FlatAppearance.BorderSize = 1;
        _closeBtn.Click += (s, e) => Close();
        _titleBar.Controls.Add(_closeBtn);

        // --- Minimize button ---
        _minimizeBtn = new Button
        {
            Text = "",
            FlatStyle = FlatStyle.Flat,
            Size = new Size(26, 22),
            ForeColor = Color.Black,
            BackColor = SystemColors.Control,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(ClientSize.Width - 60, 5),
            TabStop = false,
            CausesValidation = false,
        };
        _minimizeBtn.FlatAppearance.BorderColor = Color.DarkGray;
        _minimizeBtn.FlatAppearance.BorderSize = 1;
        _minimizeBtn.Click += (s, e) => WindowState = FormWindowState.Minimized;
        _minimizeBtn.GotFocus += (s, e) => _titleBar.Focus();
        _minimizeBtn.Paint += (s, e) =>
        {
            if (s is not Button btn) return;
            using var brush = new SolidBrush(btn.ForeColor);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString("＿", btn.Font, brush, btn.ClientRectangle, sf);
        };
        _titleBar.Controls.Add(_minimizeBtn);


        _titleBar.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _dragStart = new Point(e.X, e.Y);
        };

        _titleBar.MouseMove += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Left += e.X - _dragStart.X;
                Top += e.Y - _dragStart.Y;
            }
        };

        _titleBar.Resize += (s, e) =>
        {
            _closeBtn.Left = _titleBar.Width - _closeBtn.Width - 4;
            _minimizeBtn.Left = _closeBtn.Left - _minimizeBtn.Width - 4;
        };

        Controls.Add(_titleBar);
    }

    private void BuildTabs()
    {
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontName, 10),
            Appearance = TabAppearance.FlatButtons,
            ItemSize = new Size(120, 28),
            SizeMode = TabSizeMode.Fixed,
        };
        Controls.Add(_tabs);
        _tabs.BringToFront();

        var tabBot = new TabPage("Controls") { BackColor = Blue, ForeColor = Color.White };
        var tabWp = new TabPage("Cavebot") { BackColor = Blue, ForeColor = Color.White };

        BuildBotTab(tabBot);
        BuildWpTab(tabWp);

        _tabs.TabPages.Add(tabBot);
        _tabs.TabPages.Add(tabWp);
    }

    private void BuildBotTab(TabPage tab)
    {
        // gradient background layer
        tab.UseVisualStyleBackColor = false;
        tab.Paint += (s, e) =>
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                tab.ClientRectangle,
                Blue,   // top
                Pink,      // bottom
                90f);
            e.Graphics.FillRectangle(brush, tab.ClientRectangle);
        };


        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10),
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // transparent overlay for controls
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(10),
            BackColor = Color.Transparent
        };

        var btnInit = MakeButton("Initialize Capture", async (s, e) =>
        {
            ApplyUiToProfile();

            var missing = Profile.Missing();
            UpdateMissingLabel(missing);

            if (missing.Length > 0)
            {
                Console.WriteLine($"TBot — Profile incomplete.");
                return;
            }

            ProfileStore.Save(Profile);

            await _controller.InitializeAsync();
        });

        var btnStart = MakeButton("Start Bot", (s, e) =>
        {
            ApplyUiToProfile();
            ProfileStore.Save(Profile);
            Console.WriteLine($"[UI] Profile saved — AttackMonsters={Profile.AttackMonsters}, OpenBags={Profile.OpenBags}");
            _controller.Start();
        });
        var btnStop = MakeButton("Stop Bot", (s, e) => _controller.Stop());
        var btnReset = MakeButton("Reset Bot", (s, e) => _controller.ResetState());
        var btnRecord = MakeButton("Toggle Record", (s, e) => _controller.ToggleRecord());
        left.Controls.AddRange([btnInit, btnStart, btnStop, btnReset, btnRecord]);

        _attackMonstersChk = new CheckBox
        {
            Text = "Attack monsters",
            Checked = Profile.AttackMonsters,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true
        };
        _openBagsChk = new CheckBox
        {
            Text = "Open corpse bags (off = gold + food only)",
            Checked = Profile.OpenBags,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true
        };
        left.Controls.Add(_attackMonstersChk);
        left.Controls.Add(_openBagsChk);

        var right = BuildProfilePanel();
        layout.Controls.Add(left, 0, 0);
        layout.Controls.Add(right, 1, 0);

        tab.Controls.Add(layout);
    }

    private void BuildWpTab(TabPage tab)
    {
        tab.UseVisualStyleBackColor = false;
        tab.Paint += (s, e) =>
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                tab.ClientRectangle,
                Blue,   // top
                Pink,      // bottom
                90f);
            e.Graphics.FillRectangle(brush, tab.ClientRectangle);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10),
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320)); // left list
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // right controls

        // --- Left side: waypoint list ---
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontName, 11),
            BackColor = SystemColors.Control,
            ForeColor = Purple,
            BorderStyle = BorderStyle.FixedSingle
        };
        layout.Controls.Add(list, 0, 0);

        // --- Right side panel ---
        var rightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            RowStyles =
        {
            new RowStyle(SizeType.AutoSize),   // direction row
            new RowStyle(SizeType.Percent, 100), // spacer for buttons
            new RowStyle(SizeType.Absolute, 90) // save/load bottom section
        },
            Padding = new Padding(5)
        };
        layout.Controls.Add(rightPanel, 1, 0);

        // --- Direction selector ---
        var dirRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Width = 400,
            Height = 35
        };

        var lblDir = new Label
        {
            Text = "Directon",
            ForeColor = Yellow,
            Width = 60,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font(FontName, 7)
        };

        var comboDir = new ComboBox
        {
            Width = 120,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font(FontName, 10),
            BackColor = Purple,
            ForeColor = Teal
        };
        comboDir.Items.AddRange(["North", "East", "South", "West"]);
        comboDir.SelectedIndex = 0;

        var lblItem = new Label
        {
            Text = "Item",
            ForeColor = Yellow,
            Width = 60,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font(FontName, 7)
        };

        var comboItem = new ComboBox
        {
            Width = 120,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font(FontName, 10),
            BackColor = Purple,
            ForeColor = Teal
        };
        comboItem.Items.AddRange(["Rope", "Shovel"]);
        comboItem.SelectedIndex = 0;

        dirRow.Controls.Add(lblDir);
        dirRow.Controls.Add(comboDir);
        dirRow.Controls.Add(lblItem);
        dirRow.Controls.Add(comboItem);
        rightPanel.Controls.Add(dirRow, 0, 0);

        // --- Action buttons ---
        var btnPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Top,
            AutoSize = true
        };

        var btnWalk = MakeButton("WalkDir", (s, e) =>
        {
            if (comboDir.SelectedItem is string dirText)
            {
                var dir = dirText switch
                {
                    "North" => Direction.North,
                    "East" => Direction.East,
                    "South" => Direction.South,
                    "West" => Direction.West,
                    _ => Direction.North
                };
                _controller.AddRamp(dir);
            }
        });

        var btnAdd = MakeButton("Waypoint", (s, e) => _controller.AddWaypoint());

        var btnUse = MakeButton("RightClick", (s, e) =>
        {
            if (comboDir.SelectedItem is string dirText)
            {
                var dir = dirText switch
                {
                    "North" => Direction.North,
                    "East" => Direction.East,
                    "South" => Direction.South,
                    "West" => Direction.West,
                    _ => Direction.North
                };
                _controller.AddClickTile(dir);
            }
        });

        var btnUseItem = MakeButton("UseItem", (s, e) =>
        {
            if (comboDir.SelectedItem is string dirText)
            {
                var dir = dirText switch
                {
                    "North" => Direction.North,
                    "East" => Direction.East,
                    "South" => Direction.South,
                    "West" => Direction.West,
                    _ => Direction.North
                };

                var item = comboItem.SelectedItem switch
                {
                    "Rope" => Item.Rope,
                    "Shovel" => Item.Shovel,
                    _ => Item.Rope
                };

                _controller.AddUseItemInTile(dir, item);
            }
        });

        btnPanel.Controls.AddRange([btnWalk, btnAdd, btnUse, btnUseItem]);
        rightPanel.Controls.Add(btnPanel, 0, 1);


        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 90,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        var saveRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Bottom,
            RightToLeft = RightToLeft.Yes,
            Padding = new Padding(0),
            Margin = new Padding(5, 0, 10, 2),
            AutoSize = true,
            WrapContents = false
        };

        var loadRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Bottom,
            RightToLeft = RightToLeft.Yes,
            Padding = new Padding(0),
            Margin = new Padding(5, 0, 10, 4),
            AutoSize = true,
            WrapContents = false
        };

        var txtFile = new TextBox
        {
            Width = 160,
            Font = new Font(FontName, 10),
            BackColor = Purple,
            ForeColor = Teal,
            PlaceholderText = "Filename",
            Text = ""
        };

        var dropdown = new ComboBox
        {
            Width = 160,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font(FontName, 10),
            BackColor = Purple,
            ForeColor = Teal,
        };

        void RefreshDropdown()
        {
            dropdown.Items.Clear();

            var folder = Path.Combine(AppContext.BaseDirectory, "Paths");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            foreach (var file in Directory.GetFiles(folder, "*.json"))
                dropdown.Items.Add(Path.GetFileName(file));
        }
        RefreshDropdown();

        var btnSave = MakeButton("Save", (s, e) =>
        {
            var fileName = txtFile.Text.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "";
            var path = $"{fileName}.json";
            _controller.SavePath(path);
            RefreshDropdown();
        });

        var btnLoad = MakeButton("Load", (s, e) =>
        {
            if (dropdown.SelectedItem is string file)
                _controller.LoadPath(file);
        });

        // build layout right-aligned bottom corner
        saveRow.Controls.AddRange([btnSave, txtFile]);
        loadRow.Controls.AddRange([btnLoad, dropdown]);
        bottomPanel.Controls.Add(loadRow);
        bottomPanel.Controls.Add(saveRow);
        rightPanel.Controls.Add(bottomPanel, 0, 2);



        tab.Controls.Add(layout);

        // --- Hook waypoint list updates ---
        _controller.WayPointsUpdated += items =>
        {
            if (IsHandleCreated)
                BeginInvoke(() =>
                {
                    list.BeginUpdate();
                    list.Items.Clear();
                    foreach (var wp in items)
                        list.Items.Add(wp);
                    list.EndUpdate();
                });
        };
    }


    private Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Width = 120,
            FlatStyle = FlatStyle.Flat,
            BackColor = SystemColors.Control,
            ForeColor = Color.Black,
            Font = new Font(FontName, 9),
            TextAlign = ContentAlignment.BottomCenter,
        };

        b.FlatAppearance.BorderColor = Yellow;
        b.FlatAppearance.BorderSize = 1;
        b.MouseEnter += (s, e) => b.BackColor = Pink;
        b.MouseLeave += (s, e) => b.BackColor = SystemColors.Control;
        b.Click += onClick;
        return b;
    }

    private Label _missingLbl = null!;
    private TextBox _playerTxt = null!;
    private TextBox _discordWebHookUrl = null!;
    private TextBox _aiContextTxt = null!;
    private CheckBox _aiChatEnabledChk = null!;
    private CheckBox _attackMonstersChk = null!;
    private CheckBox _openBagsChk = null!;

    private System.Windows.Forms.Control BuildProfilePanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        int row = 0;

        // PlayerName
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "PlayerName", ForeColor = Yellow, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _playerTxt = new TextBox { Text = Profile.PlayerName, Width = 200, BackColor = Purple, ForeColor = Teal };
        panel.Controls.Add(_playerTxt, 1, row);
        //panel.Controls.Add(new Label(), 2, row);
        row++;

        //Discord WebHook URL
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "DiscordWebHookUrl", ForeColor = Yellow, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _discordWebHookUrl = new TextBox { Text = Profile.DiscordWebhookUrl, Width = 200, BackColor = Purple, ForeColor = Teal };
        panel.Controls.Add(_discordWebHookUrl, 1, row);
        //panel.Controls.Add(new Label(), 2, row);
        row++;

        // Rect rows helper
        void AddRectRow(string title, Func<RectDto> get, Action<RectDto> set)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = title, ForeColor = Yellow, AutoSize = true , Anchor = AnchorStyles.Left }, 0, row);

            var valueLabel = new Label
            {
                Text = get().ToString(),
                ForeColor = Color.White,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };
            panel.Controls.Add(valueLabel, 1, row);

            var btn = MakeButton("Set", (s, e) =>
            {
                var r = PromptRectWithMinimize();
                if (r == null) return;

                var dto = RectDto.FromRectangle(r.Value);
                set(dto);

                valueLabel.Text = dto.ToString();
                UpdateMissingLabel(Profile.Missing());
            });

            btn.Width = 70;
            panel.Controls.Add(btn, 2, row);

            row++;
        }

        AddRectRow("GameWindow", () => Profile.GameWindowRect, v => Profile.GameWindowRect = v);
        AddRectRow("Backpack", () => Profile.BpRect, v => Profile.BpRect = v);
        AddRectRow("Tools", () => Profile.ToolsRect, v => Profile.ToolsRect = v);
        AddRectRow("Loot", () => Profile.LootRect, v => Profile.LootRect = v);
        AddRectRow("UH", () => Profile.UhRect, v => Profile.UhRect = v);
        AddRectRow("ChatArea", () => Profile.ChatRect, v => Profile.ChatRect = v);
        AddRectRow("ChatTabs", () => Profile.ChatTabsRect, v => Profile.ChatTabsRect = v);

        // AI Chat settings
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "AI Context", ForeColor = Yellow, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _aiContextTxt = new TextBox { Text = Profile.AiChatContext, Width = 200, BackColor = Purple, ForeColor = Teal, Multiline = true, Height = 40 };
        panel.Controls.Add(_aiContextTxt, 1, row);
        row++;

        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "AI Chat", ForeColor = Yellow, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _aiChatEnabledChk = new CheckBox { Text = "Enabled", Checked = Profile.AiChatEnabled, ForeColor = Color.White, BackColor = Color.Transparent };
        panel.Controls.Add(_aiChatEnabledChk, 1, row);
        row++;

        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _missingLbl = new Label { ForeColor = Color.White, AutoSize = true };
        panel.Controls.Add(new Label { Text = "Missing", ForeColor = Yellow, AutoSize = true }, 0, row);
        panel.Controls.Add(_missingLbl, 1, row);
        panel.SetColumnSpan(_missingLbl, 2);

        UpdateMissingLabel(Profile.Missing());

        return panel;
    }

    private void ApplyUiToProfile()
    {
        Profile.PlayerName = _playerTxt.Text.Trim();
        Profile.DiscordWebhookUrl = _discordWebHookUrl.Text.Trim();
        Profile.AiChatContext = _aiContextTxt.Text.Trim();
        Profile.AiChatEnabled = _aiChatEnabledChk.Checked;
        Profile.AttackMonsters = _attackMonstersChk.Checked;
        Profile.OpenBags = _openBagsChk.Checked;
    }

    private void UpdateMissingLabel(string[] missing)
    {
        _missingLbl.Text = missing.Length == 0 ? "none" : string.Join(", ", missing);
    }

    private Rectangle? PromptRectWithMinimize()
    {
        var prevState = WindowState;

        try
        {
            // Hide/minimize the bot UI so it doesn't cover the game
            WindowState = FormWindowState.Minimized;

            // Give Windows a moment to repaint before showing overlay
            Application.DoEvents();
            Thread.Sleep(120);

            return RectSelectOverlay.Prompt();
        }
        finally
        {
            // Restore
            WindowState = prevState == FormWindowState.Minimized ? FormWindowState.Normal : prevState;
            Activate();
        }
    }
}
