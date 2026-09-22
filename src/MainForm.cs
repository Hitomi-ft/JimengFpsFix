using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace JimengFpsFix;

public sealed class MainForm : Form
{
    private readonly ListView _list = new();
    private readonly TextBox _log = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _status = new();
    private readonly Label _lblFfmpeg = new();

    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnPickFfmpeg = new();
    private readonly Button _btnBrowseOut = new();

    private readonly ComboBox _cboFps = new();
    private readonly ComboBox _cboMethod = new();
    private readonly ComboBox _cboEncode = new();
    private readonly ComboBox _cboAudio = new();
    private readonly ComboBox _cboOutput = new();
    private readonly ComboBox _cboPreset = new();
    private readonly NumericUpDown _numCrf = new();
    private readonly NumericUpDown _numParallel = new();
    private readonly TextBox _txtOutDir = new();
    private readonly TextBox _txtSuffix = new();
    private readonly CheckBox _chkYuv = new();
    private readonly CheckBox _chkDeep = new();
    private readonly CheckBox _chkForce = new();
    private readonly CheckBox _chkRecursive = new();

    private readonly ConcurrentDictionary<string, ListViewItem> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _probeGate = new(4);

    private AppSettings _settings = AppSettings.Load();
    private CancellationTokenSource _cts;
    private volatile bool _running;
    private readonly string[] _initialFiles;

    public MainForm(string[] initialFiles)
    {
        Text = "帧率修复工具  ·  批量修复为精确 CFR 帧率，保持音频对齐";
        ClientSize = new Size(1060, 720);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        // 关键：Dpi 模式必须显式声明「设计 DPI」。否则 WinForms 会拿字体度量当基准算出错误的
        // 缩放系数，Anchor 定位的控件会被放到窗口外面（「开始修复」看不见就是这个原因）。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Microsoft YaHei UI", 9f);
        _initialFiles = initialFiles;

        BuildUi();
        LoadSettingsIntoUi();
        DetectFfmpeg(true);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _list.DragEnter += OnDragEnter;
        _list.DragDrop += OnDragDrop;
        FormClosing += OnFormClosing;
        Load += OnFormLoad;   // 高 DPI 下窗口可能比屏幕还高，兜底收缩

        // must run after the handle exists (files dropped onto the exe are passed as arguments)
        Shown += (_, _) =>
        {
            if (_initialFiles is { Length: > 0 }) AddPaths(_initialFiles);
            UpdateStatusIdle();
        };
    }

    private void OnFormLoad(object sender, EventArgs e)
    {
        // 窗口可能比屏幕还大（或最小尺寸超过屏幕），按工作区兜底
        try
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int minW = Math.Min(900, Math.Max(640, wa.Width - 20));
            int minH = Math.Min(600, Math.Max(420, wa.Height - 40));
            MinimumSize = new Size(minW, minH);
            int w = Math.Min(Width, wa.Width - 20);
            int h = Math.Min(Height, wa.Height - 40);
            if (w != Width || h != Height) Size = new Size(w, h);
        }
        catch { }
    }

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8, 6, 8, 6),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));    // 工具条（含「开始修复」）
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));     // 文件列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 182));   // 设置
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));     // 日志
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));    // 进度 / 状态
        Controls.Add(root);

        // ---- toolbar
        var top = new Panel { Dock = DockStyle.Fill };
        AddButton(top, "添加文件…", 0, 0, 96, (_, _) => PickFiles());
        AddButton(top, "添加文件夹…", 102, 0, 106, (_, _) => PickFolder());
        AddButton(top, "移除选中", 214, 0, 88, (_, _) => RemoveSelected());
        AddButton(top, "清空列表", 308, 0, 82, (_, _) => ClearList());
        AddButton(top, "打开输出目录", 396, 0, 106, (_, _) => OpenOutputDir());
        AddButton(top, "原理 / 说明", 508, 0, 96, (_, _) => ShowAbout());
        var hint = new Label
        {
            Text = "可直接把 mp4 文件或整个文件夹拖进窗口 · Ctrl/Shift 多选 · 每个文件都会先校验再落地",
            Location = new Point(2, 46),
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 90, 90),
        };
        top.Controls.Add(hint);

        // 「开始修复」放在工具条右上角，窗口再矮也不会被切掉。
        // 用 Resize 显式定位而不是 Anchor —— Anchor 在 DPI 缩放时容易被算歪。
        _btnStart.Text = "开始修复"; _btnStart.Size = new Size(124, 36);
        _btnStart.Font = new Font(Font, FontStyle.Bold);
        _btnStart.Click += (_, _) => StartBatch();
        top.Controls.Add(_btnStart);

        _btnStop.Text = "停止"; _btnStop.Size = new Size(74, 36);
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => StopBatch();
        top.Controls.Add(_btnStop);

        void PlaceTopButtons()
        {
            int right = Math.Max(620, top.ClientSize.Width - 4);
            _btnStart.Location = new Point(right - _btnStart.Width, 2);
            _btnStop.Location = new Point(_btnStart.Left - _btnStop.Width - 6, 3);
        }
        top.SizeChanged += (_, _) => PlaceTopButtons();
        root.Controls.Add(top, 0, 0);
        PlaceTopButtons();

        // ---- file list
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.GridLines = false;
        _list.HideSelection = false;
        _list.Columns.Add("文件", 340);
        _list.Columns.Add("源帧率", 95);
        _list.Columns.Add("帧数", 55);
        _list.Columns.Add("修复方式", 165);
        _list.Columns.Add("结果", 190);
        _list.Columns.Add("输出", 180);
        _list.DoubleClick += (_, _) => OpenSelectedInExplorer();
        var menu = new ContextMenuStrip();
        menu.Items.Add("移除选中", null, (_, _) => RemoveSelected());
        menu.Items.Add("在资源管理器中显示", null, (_, _) => OpenSelectedInExplorer());
        menu.Items.Add("清空列表", null, (_, _) => ClearList());
        _list.ContextMenuStrip = menu;
        root.Controls.Add(_list, 0, 1);

        // ---- settings（全部固定坐标，不依赖 Anchor；整体宽度按 ≥830px 设计）
        var grp = new GroupBox { Dock = DockStyle.Fill, Text = "修复设置" };
        int y1 = 20, y2 = 48, y3 = 76, y4 = 104, y5 = 132;

        AddLabel(grp, "目标帧率", 14, y1 + 4);
        _cboFps.Location = new Point(74, y1); _cboFps.Size = new Size(64, 24);
        _cboFps.DropDownStyle = ComboBoxStyle.DropDown;
        _cboFps.Items.AddRange(new object[] { "24", "23.976", "25", "29.97", "30", "50", "60" });
        grp.Controls.Add(_cboFps);

        AddLabel(grp, "修复方式", 150, y1 + 4);
        _cboMethod.Location = new Point(214, y1); _cboMethod.Size = new Size(132, 24);
        _cboMethod.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboMethod.Items.AddRange(new object[] { "自动（优先无损）", "仅无损重写时间戳", "强制重新编码" });
        grp.Controls.Add(_cboMethod);

        AddLabel(grp, "重编码方式", 358, y1 + 4);
        _cboEncode.Location = new Point(434, y1); _cboEncode.Size = new Size(168, 24);
        _cboEncode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboEncode.Items.AddRange(new object[] { "自动判断", "保留全部帧（变速）", "标准帧率转换（丢/复制帧）" });
        grp.Controls.Add(_cboEncode);

        AddLabel(grp, "质量 CRF", 614, y1 + 4);
        _numCrf.Location = new Point(676, y1); _numCrf.Size = new Size(48, 24);
        _numCrf.Minimum = 0; _numCrf.Maximum = 40; _numCrf.Value = 16;
        grp.Controls.Add(_numCrf);

        AddLabel(grp, "preset", 14, y2 + 4);
        _cboPreset.Location = new Point(62, y2); _cboPreset.Size = new Size(88, 24);
        _cboPreset.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboPreset.Items.AddRange(new object[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" });
        grp.Controls.Add(_cboPreset);

        AddLabel(grp, "音频处理", 162, y2 + 4);
        _cboAudio.Location = new Point(226, y2); _cboAudio.Size = new Size(168, 24);
        _cboAudio.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboAudio.Items.AddRange(new object[] { "自动（时长变化才变速）", "原样复制（推荐）", "强制变速对齐", "与视频等长（填充/裁剪）" });
        grp.Controls.Add(_cboAudio);

        AddLabel(grp, "输出位置", 406, y2 + 4);
        _cboOutput.Location = new Point(470, y2); _cboOutput.Size = new Size(120, 24);
        _cboOutput.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboOutput.Items.AddRange(new object[] { "源文件同目录", "指定目录", "覆盖源文件" });
        grp.Controls.Add(_cboOutput);

        AddLabel(grp, "并行文件数", 604, y2 + 4);
        _numParallel.Location = new Point(686, y2); _numParallel.Size = new Size(44, 24);
        _numParallel.Minimum = 1; _numParallel.Maximum = 8; _numParallel.Value = 1;
        grp.Controls.Add(_numParallel);

        AddLabel(grp, "输出目录", 14, y3 + 4);
        _txtOutDir.Location = new Point(78, y3); _txtOutDir.Size = new Size(430, 24);
        grp.Controls.Add(_txtOutDir);
        _btnBrowseOut.Text = "浏览…"; _btnBrowseOut.Location = new Point(514, y3 - 1); _btnBrowseOut.Size = new Size(62, 26);
        _btnBrowseOut.Click += (_, _) => BrowseOutputDir();
        grp.Controls.Add(_btnBrowseOut);

        AddLabel(grp, "后缀", 590, y3 + 4);
        _txtSuffix.Location = new Point(630, y3); _txtSuffix.Size = new Size(84, 24);
        grp.Controls.Add(_txtSuffix);

        AddCheck(grp, _chkYuv, "重编码时强制 yuv420p", 14, y4, 178);
        AddCheck(grp, _chkDeep, "完整解码校验（更稳）", 196, y4, 176);
        AddCheck(grp, _chkForce, "已达标文件也重新处理", 376, y4, 186);
        AddCheck(grp, _chkRecursive, "文件夹包含子目录", 566, y4, 152);

        AddLabel(grp, "ffmpeg", 14, y5 + 4);
        _lblFfmpeg.Location = new Point(78, y5 + 4); _lblFfmpeg.Size = new Size(556, 20);
        _lblFfmpeg.AutoEllipsis = true;
        grp.Controls.Add(_lblFfmpeg);
        _btnPickFfmpeg.Text = "选择 ffmpeg…"; _btnPickFfmpeg.Location = new Point(642, y5); _btnPickFfmpeg.Size = new Size(110, 26);
        _btnPickFfmpeg.Click += (_, _) => PickFfmpeg();
        grp.Controls.Add(_btnPickFfmpeg);
        root.Controls.Add(grp, 0, 2);

        // ---- log
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(250, 250, 252);
        _log.Font = new Font("Consolas", 9f);
        _log.WordWrap = false;
        root.Controls.Add(_log, 0, 3);

        // ---- bottom: 进度 + 状态（「开始修复」在工具条右上角）
        var bottom = new Panel { Dock = DockStyle.Fill };
        _bar.Location = new Point(2, 6); _bar.Size = new Size(1010, 18);
        bottom.Controls.Add(_bar);
        _status.Location = new Point(2, 28); _status.Size = new Size(1010, 30);
        _status.Text = "就绪";
        bottom.Controls.Add(_status);
        void PlaceBottom()
        {
            int w = Math.Max(200, bottom.ClientSize.Width - 4);
            _bar.Width = w;
            _status.Width = w;
        }
        bottom.SizeChanged += (_, _) => PlaceBottom();
        root.Controls.Add(bottom, 0, 4);
        PlaceBottom();
    }

    private static void AddLabel(Control parent, string text, int x, int y, AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left)
    {
        parent.Controls.Add(new Label { Text = text, Location = new Point(x, y), AutoSize = true, Anchor = anchor });
    }

    private static void AddButton(Control parent, string text, int x, int y, int w, EventHandler onClick)
    {
        var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 30) };
        b.Click += onClick;
        parent.Controls.Add(b);
    }

    private static void AddCheck(Control parent, CheckBox cb, string text, int x, int y, int w)
    {
        cb.Text = text;
        cb.Location = new Point(x, y);
        cb.Size = new Size(w, 24);
        cb.Checked = true;
        parent.Controls.Add(cb);
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettingsIntoUi()
    {
        _cboFps.Text = _settings.Fps;
        _cboMethod.SelectedIndex = Math.Clamp(_settings.MethodIndex, 0, 2);
        _cboEncode.SelectedIndex = Math.Clamp(_settings.EncodeIndex, 0, 2);
        _cboAudio.SelectedIndex = Math.Clamp(_settings.AudioIndex, 0, 3);
        _cboOutput.SelectedIndex = Math.Clamp(_settings.OutputIndex, 0, 2);
        _cboPreset.SelectedItem = _settings.Preset;
        if (_cboPreset.SelectedIndex < 0) _cboPreset.SelectedItem = "medium";
        _numCrf.Value = Math.Clamp(_settings.Crf, 0, 40);
        _numParallel.Value = Math.Clamp(_settings.Parallel, 1, 8);
        _txtOutDir.Text = _settings.OutputDir;
        _txtSuffix.Text = _settings.Suffix;
        _chkYuv.Checked = _settings.ForceYuv420p;
        _chkDeep.Checked = _settings.DeepVerify;
        _chkForce.Checked = _settings.Force;
        _chkRecursive.Checked = _settings.Recursive;
    }

    private void SyncSettingsFromUi()
    {
        _settings.Fps = _cboFps.Text.Trim();
        _settings.MethodIndex = Math.Max(0, _cboMethod.SelectedIndex);
        _settings.EncodeIndex = Math.Max(0, _cboEncode.SelectedIndex);
        _settings.AudioIndex = Math.Max(0, _cboAudio.SelectedIndex);
        _settings.OutputIndex = Math.Max(0, _cboOutput.SelectedIndex);
        _settings.Preset = _cboPreset.SelectedItem?.ToString() ?? "medium";
        _settings.Crf = (int)_numCrf.Value;
        _settings.Parallel = (int)_numParallel.Value;
        _settings.OutputDir = _txtOutDir.Text.Trim();
        _settings.Suffix = _txtSuffix.Text.Trim();
        // 不保存「指定目录但目录为空」这种死状态，否则下次点开始只会弹提示不做正事
        if (_settings.OutputIndex == 1 && string.IsNullOrWhiteSpace(_settings.OutputDir))
            _settings.OutputIndex = 0;
        _settings.ForceYuv420p = _chkYuv.Checked;
        _settings.DeepVerify = _chkDeep.Checked;
        _settings.Force = _chkForce.Checked;
        _settings.Recursive = _chkRecursive.Checked;
        _settings.Save();
    }

    // ------------------------------------------------------------------ ffmpeg

    private void DetectFfmpeg(bool announce)
    {
        var ok = FfLocator.Apply(_settings.FfmpegPath, out var err);
        if (ok)
        {
            _lblFfmpeg.ForeColor = Color.FromArgb(20, 110, 30);
            _lblFfmpeg.Text = FfLocator.FfmpegPath + "    " + ShortVersion(FfLocator.Version);
            if (announce) LogLine("已找到 " + FfLocator.FfmpegPath);
        }
        else
        {
            _lblFfmpeg.ForeColor = Color.Firebrick;
            _lblFfmpeg.Text = err;
            if (announce) LogLine("警告: " + err);
        }
    }

    private static string ShortVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var i = v.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? v.Substring(0, i) : v;
    }

    private void PickFfmpeg()
    {
        using var dlg = new OpenFileDialog { Title = "选择 ffmpeg.exe", Filter = "ffmpeg|ffmpeg.exe|可执行文件|*.exe" };
        if (Directory.Exists(_settings.FfmpegPath)) dlg.InitialDirectory = _settings.FfmpegPath;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.FfmpegPath = dlg.FileName;
        _settings.Save();
        DetectFfmpeg(true);
        if (Ff.Located) ProbeAllRows();
    }

    // ------------------------------------------------------------------ file list

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要修复的视频（可多选）",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mov;*.mkv;*.m4v;*.avi;*.webm;*.ts;*.flv;*.wmv|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择包含视频的文件夹", ShowNewFolderButton = false };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dlg.SelectedPath });
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var found = FileCollect.Expand(paths, _chkRecursive.Checked);
        int added = 0;
        foreach (var f in found)
        {
            if (_rows.ContainsKey(f)) continue;
            var item = new ListViewItem(Path.GetFileName(f));
            item.SubItems.Add("…");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("待处理");
            item.SubItems.Add("");
            item.Tag = f;
            item.ToolTipText = f;
            _list.Items.Add(item);
            _rows[f] = item;
            added++;
        }
        if (added > 0)
        {
            LogLine($"已添加 {added} 个文件（列表共 {_list.Items.Count} 个）");
            ProbeAllRows();
        }
        else if (found.Count == 0)
        {
            LogLine("没有找到视频文件");
        }
        UpdateStatusIdle();
    }

    private void ProbeAllRows()
    {
        if (!Ff.Located) return;
        foreach (var item in _list.Items.Cast<ListViewItem>())
        {
            var path = item.Tag as string;
            if (path == null || item.SubItems[1].Text != "…") continue;
            _ = Task.Run(async () =>
            {
                await _probeGate.WaitAsync();
                try
                {
                    var info = Ff.Probe(path, out _);
                    var packets = Ff.ReadVideoPackets(path, out _);
                    var v = info?.FirstVideo;
                    if (v == null)
                    {
                        Ui(() => { item.SubItems[1].Text = "无视频流"; item.SubItems[4].Text = "不可用"; });
                        return;
                    }
                    double srcFps = v.AvgFrameRate.Value;
                    Ui(() =>
                    {
                        item.SubItems[1].Text = $"{srcFps:0.####} fps";
                        item.SubItems[2].Text = packets.Count > 0 ? packets.Count.ToString() : (v.NbFrames > 0 ? v.NbFrames.ToString() : "?");
                        int idx = _list.Items.IndexOf(item);
                        if (idx >= 0) item.SubItems[4].Text = "待处理";
                    });
                }
                catch { }
                finally { _probeGate.Release(); }
            });
        }
    }

    private void RemoveSelected()
    {
        foreach (var item in _list.SelectedItems.Cast<ListViewItem>().ToList())
        {
            _rows.TryRemove(item.Tag as string ?? "", out _);
            _list.Items.Remove(item);
        }
        UpdateStatusIdle();
    }

    private void ClearList()
    {
        _list.Items.Clear();
        _rows.Clear();
        _progress.Clear();
        _bar.Value = 0;
        UpdateStatusIdle();
    }

    private void OpenSelectedInExplorer()
    {
        if (_list.SelectedItems.Count == 0) return;
        var path = _list.SelectedItems[0].Tag as string;
        if (path == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
    }

    private void OpenOutputDir()
    {
        string dir = null;
        if (_settings.OutputIndex == 1 && Directory.Exists(_settings.OutputDir)) dir = _settings.OutputDir;
        if (dir == null && _list.Items.Count > 0) dir = Path.GetDirectoryName(_list.Items[0].Tag as string ?? "");
        if (dir == null || !Directory.Exists(dir)) { MessageBox.Show(this, "还没有可打开的输出目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); } catch { }
    }

    private void BrowseOutputDir()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择输出目录", ShowNewFolderButton = true };
        if (Directory.Exists(_txtOutDir.Text)) dlg.SelectedPath = _txtOutDir.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) { _txtOutDir.Text = dlg.SelectedPath; _cboOutput.SelectedIndex = 1; }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "为什么会出现 24.07 / 24.08 帧？\r\n" +
            "即梦/Dreamina 导出的 MP4 实际是 24 fps 的内容，但每帧的时间戳被对齐到了 1/60 秒的网格上\r\n" +
            "（每帧 512/768 tick 交替，精确值应为 640 tick），容器声明的时长又比真实内容略短，\r\n" +
            "于是 帧数 ÷ 时长 会落在 24.04～24.20 之间（帧数越多越接近 24：169 帧→24.0855、193 帧→24.0748）。\r\n" +
            "本工具不依赖这个数字，而是直接判断时间戳结构，所以 24.07、24.08 是同一种修法。\r\n\r\n" +
            "本工具怎么修？\r\n" +
            "1) 无损模式（默认）：视频不重新编码，只把每帧时间戳写回精确的 1/24 秒网格。\r\n" +
            "   帧数完全不变、画面零损失，通常不到 1 秒完成；音频原样复制，起始时间保持 0，因此对齐不受影响。\r\n" +
            "2) 若源文件不满足无损条件（时间戳不在网格上、时间基无法整除等），自动改用 libx264 重编码为精确 CFR。\r\n\r\n" +
            "每个文件输出后都会重新探测校验：\r\n" +
            "   · 帧率必须精确等于目标值（含容器元数据）\r\n" +
            "   · 每一帧时间戳都必须落在目标网格上（等于证明没有丢帧/复制帧/乱序）\r\n" +
            "   · 音频起始时间必须与源文件一致\r\n" +
            "   · 可选完整解码校验\r\n" +
            "任何一项不通过都会自动回退重编码；结果先写到临时文件，校验通过后才改名落地，不会留下半成品。",
            "原理 / 说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ------------------------------------------------------------------ batch

    private async void StartBatch()
    {
        if (_running) return;
        SyncSettingsFromUi();
        if (!Ff.Located && !FfLocator.Apply(_settings.FfmpegPath, out var ferr))
        {
            MessageBox.Show(this, ferr, "缺少 ffmpeg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var opts = _settings.ToOptions(out var fpsErr);
        if (fpsErr != null)
        {
            MessageBox.Show(this, fpsErr, "帧率无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (opts.Output == OutputMode.OutputDir && string.IsNullOrWhiteSpace(opts.OutputDir))
        {
            MessageBox.Show(this, "请先选择输出目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var files = _list.Items.Cast<ListViewItem>().Select(i => i.Tag as string).Where(s => !string.IsNullOrEmpty(s)).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "请先添加要修复的视频文件（可多选，也可以直接把文件拖进来）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (opts.Output == OutputMode.Overwrite)
        {
            var ans = MessageBox.Show(this,
                $"将直接用修复结果覆盖 {files.Count} 个源文件。\r\n每个文件只有在校验通过后才会替换原文件（先写临时文件），但覆盖后无法还原。\r\n\r\n确定继续吗？",
                "覆盖源文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ans != DialogResult.Yes) return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        SetRunningUi(true);
        _progress.Clear();
        _bar.Value = 0;
        _log.Clear();
        foreach (var item in _list.Items.Cast<ListViewItem>()) item.SubItems[4].Text = "排队中";
        LogLine($"=== 开始处理 {files.Count} 个文件 → {opts.Fps} fps ===");
        LogLine($"模式: {_cboMethod.Text} | 重编码: {_cboEncode.Text} | 音频: {_cboAudio.Text} | 并行 {opts.Parallel}");
        LogLine(new string('-', 96));

        var sw = Stopwatch.StartNew();
        int ok = 0, skipped = 0, failed = 0, cancelled = 0;
        var queue = new ConcurrentQueue<string>(files);
        var engine = new RepairEngine(opts, LogLine);
        int total = files.Count;
        int finished = 0;
        var gate = new object();

        void Report(string file, string method, string result, Color color)
        {
            Ui(() =>
            {
                if (_rows.TryGetValue(file, out var item))
                {
                    if (item.SubItems.Count >= 6)
                    {
                        if (!string.IsNullOrEmpty(method)) item.SubItems[3].Text = method;
                        item.SubItems[4].Text = result;
                        item.SubItems[4].ForeColor = color;
                    }
                }
            });
        }

        void RefreshProgress()
        {
            double sum = finished;
            foreach (var kv in _progress) sum += Math.Clamp(kv.Value, 0, 1);
            double pct = total > 0 ? Math.Clamp(sum / total, 0, 1) : 0;
            Ui(() =>
            {
                _bar.Value = (int)Math.Round(pct * 100);
                _status.Text = $"进度 {finished}/{total}   成功 {ok}  跳过 {skipped}  失败 {failed}   已用 {sw.Elapsed.ToString(@"mm\:ss")}";
            });
        }

        int workers = Math.Min(opts.Parallel, total);
        var tasks = new List<Task>();
        for (int w = 0; w < workers; w++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (queue.TryDequeue(out var file))
                {
                    if (_cts.IsCancellationRequested) { Interlocked.Increment(ref cancelled); break; }
                    var name = Path.GetFileName(file);
                    Report(file, "处理中…", "处理中…", Color.DarkSlateBlue);
                    _progress[file] = 0;
                    RepairResult res;
                    try
                    {
                        res = engine.Process(file, _cts.Token, pct => { _progress[file] = pct; RefreshProgress(); });
                    }
                    catch (Exception ex)
                    {
                        res = new RepairResult { Input = file, Ok = false, Message = "异常: " + ex.Message };
                    }
                    _progress[file] = 1;
                    lock (gate) { finished++; }

                    if (res.Ok && res.Skipped)
                    {
                        Interlocked.Increment(ref skipped);
                        Report(file, "跳过", res.Message, Color.DimGray);
                        LogLine($"[跳过] {name}：{res.Message}");
                    }
                    else if (res.Ok)
                    {
                        Interlocked.Increment(ref ok);
                        string method = res.Method switch
                        {
                            UsedMethod.CopyRetime => "无损重写时间戳",
                            UsedMethod.ReencodeRetime => "重编码(保留帧)",
                            UsedMethod.ReencodeConform => "重编码(标准转换)",
                            _ => "-",
                        };
                        string audioTag = res.AudioKeptBitExact ? "音频原样" : "音频已处理";
                        Report(file, method, $"✔ {opts.Fps} fps ({audioTag})", Color.SeaGreen);
                        Ui(() =>
                        {
                            if (_rows.TryGetValue(file, out var it) && it.SubItems.Count >= 6) it.SubItems[5].Text = Path.GetFileName(res.Output);
                        });
                        LogLine($"[完成] {name}  {res.SourceFrames}帧 {res.SourceFps:0.####}→{opts.Fps} fps  {method}  {res.Seconds:0.00}s  → {Path.GetFileName(res.Output)}");
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        Report(file, "失败", "✘ " + Truncate(res.Message, 60), Color.Firebrick);
                        LogLine($"[失败] {name}：{res.Message}");
                    }
                    RefreshProgress();
                }
            }));
        }

        await Task.WhenAll(tasks);
        sw.Stop();
        SetRunningUi(false);
        _running = false;
        RefreshProgress();
        LogLine(new string('-', 96));
        LogLine($"全部结束：成功 {ok}，跳过 {skipped}，失败 {failed}" + (cancelled > 0 ? $"，取消 {cancelled}" : "") + $"，用时 {sw.Elapsed.ToString(@"mm\:ss")}");
        _status.Text = $"完成：成功 {ok}，跳过 {skipped}，失败 {failed}" + (cancelled > 0 ? $"，取消 {cancelled}" : "") + $"   用时 {sw.Elapsed.ToString(@"mm\:ss")}";
        if (failed == 0 && cancelled == 0 && ok > 0)
        {
            var dir = Path.GetDirectoryName(_list.Items.Count > 0 ? (_list.Items[0].Tag as string) : "") ?? "";
            LogLine("全部通过校验。输出目录：" + (_settings.OutputIndex == 1 ? _settings.OutputDir : dir));
        }
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

    private void StopBatch()
    {
        if (!_running) return;
        _cts?.Cancel();
        LogLine("正在停止…（当前 ffmpeg 进程会被终止）");
        _btnStop.Enabled = false;
    }

    private void SetRunningUi(bool running)
    {
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _cboFps.Enabled = _cboMethod.Enabled = _cboEncode.Enabled = _cboAudio.Enabled = !running;
        _cboOutput.Enabled = _cboPreset.Enabled = _numCrf.Enabled = _numParallel.Enabled = !running;
        _txtOutDir.Enabled = _txtSuffix.Enabled = _btnBrowseOut.Enabled = !running;
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_running)
        {
            var ans = MessageBox.Show(this, "正在处理中，确定要退出吗？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) { e.Cancel = true; return; }
            _cts?.Cancel();
        }
        SyncSettingsFromUi();
    }

    // ------------------------------------------------------------------ helpers

    private void UpdateStatusIdle()
    {
        if (_running) return;
        _status.Text = _list.Items.Count == 0
            ? "就绪：请添加视频文件（可多选或直接拖入），然后点右上角「开始修复」"
            : $"列表中有 {_list.Items.Count} 个文件，点右上角「开始修复」";
    }

    private void LogLine(string line)
    {
        Ui(() =>
        {
            if (_log.TextLength > 400000) _log.Clear();
            _log.AppendText(line + Environment.NewLine);
        });
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated)
        {
            // no handle yet: only safe when we are already on the UI thread
            if (!InvokeRequired) action();
            return;
        }
        if (InvokeRequired)
        {
            try { BeginInvoke(action); } catch { }
        }
        else action();
    }
}
