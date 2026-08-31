using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DupCleaner
{
    public class MainForm : Form
    {
        // ---- 状态 ----
        private List<string> _dirs = new List<string>();
        private List<DupGroup> _groups = new List<DupGroup>();
        private bool _busy;
        private bool _folderMode;                     // 结果列表当前显示的是“重复文件夹”还是“重复文件”
        private volatile bool _cancel;
        private readonly System.Diagnostics.Stopwatch _uiTick = new System.Diagnostics.Stopwatch();
        private readonly object _uiLock = new object();

        // ---- 控件 ----
        private ListBox _dirList;
        private Button _btnImport, _btnAdd, _btnRemove, _btnScan, _btnScanDir, _btnCancel, _btnClean;
        private ProgressBar _progress;
        private Label _lblStatus;
        private ListView _lvResult;
        private RadioButton _rbRecycle, _rbDelete;
        private NumericUpDown _numMinSize;

        public MainForm()
        {
            Text = "曲库重复文件清理工具";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 540);
            ClientSize = new Size(920, 620);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("微软雅黑", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { /* 保持默认图标 */ }
            BuildUi();
        }

        private void BuildUi()
        {
            // 顶部：文件夹管理
            _dirList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Font = new Font("微软雅黑", 9F)
            };

            _btnImport = MakeButton("导入 ktv_config.json");
            _btnImport.Click += BtnImport_Click;
            _btnAdd = MakeButton("添加文件夹");
            _btnAdd.Click += BtnAdd_Click;
            _btnRemove = MakeButton("移除选中");
            _btnRemove.Click += BtnRemove_Click;

            var dirBtnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(4, 6, 4, 2),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            dirBtnPanel.Controls.AddRange(new Control[] { _btnImport, _btnAdd, _btnRemove });

            var dirBox = new GroupBox { Text = " 曲库文件夹 ", Dock = DockStyle.Fill };
            var dirLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            dirLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            dirLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dirLayout.Controls.Add(dirBtnPanel, 0, 0);
            dirLayout.Controls.Add(_dirList, 0, 1);
            dirBox.Controls.Add(dirLayout);

            // 底部控件
            _progress = new ProgressBar { Minimum = 0, Maximum = 100, Dock = DockStyle.Fill };
            _lblStatus = new Label
            {
                Text = "就绪。请导入 ktv_config.json 或手动添加扫描文件夹。",
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _btnScan = MakeButton("开始扫描(重复文件)", 170);
            _btnScan.Click += BtnScan_Click;
            _btnScanDir = MakeButton("开始扫描(重复文件夹)", 190);
            _btnScanDir.Click += BtnScanDir_Click;
            _btnCancel = MakeButton("取消");
            _btnCancel.Enabled = false;
            _btnCancel.Click += BtnCancel_Click;

            _rbRecycle = new RadioButton { Text = "移到回收站（可恢复）", AutoSize = true, Checked = true };
            _rbDelete = new RadioButton { Text = "永久删除", AutoSize = true };

            _btnClean = MakeButton("一键清理勾选的重复文件", 200);
            _btnClean.Enabled = false;
            _btnClean.Click += BtnClean_Click;

            var actionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(4, 6, 4, 4),
                WrapContents = true
            };
            actionPanel.Controls.Add(_btnScan);
            actionPanel.Controls.Add(_btnScanDir);
            actionPanel.Controls.Add(_btnCancel);
            actionPanel.Controls.Add(new Label { Text = "  删除方式：", AutoSize = true, Anchor = AnchorStyles.Left });
            actionPanel.Controls.Add(_rbRecycle);
            actionPanel.Controls.Add(_rbDelete);
            actionPanel.Controls.Add(BuildMinSizeGroup());
            actionPanel.Controls.Add(_btnClean);

            // 重复文件列表（中部）
            _lvResult = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                CheckBoxes = true,
                GridLines = true,
                HideSelection = false
            };
            _lvResult.Columns.Add("操作", 60);
            _lvResult.Columns.Add("文件名", 220);
            _lvResult.Columns.Add("大小", 90);
            _lvResult.Columns.Add("创建时间", 140);
            _lvResult.Columns.Add("路径", 480);
            _lvResult.Resize += delegate { ResizePathColumn(); };
            _lvResult.ItemChecked += LvResult_ItemChecked;

            var listBox = new GroupBox { Text = " 重复文件列表（勾选即待删除，保留项默认不勾选） ", Dock = DockStyle.Fill };
            listBox.Controls.Add(_lvResult);

            // 整体竖排布局
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Margin = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));   // 0: 文件夹
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));     // 1: 重复列表
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));      // 2: 操作按钮
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));     // 3: 进度
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));     // 4: 状态
            layout.Controls.Add(dirBox, 0, 0);
            layout.Controls.Add(listBox, 0, 1);
            layout.Controls.Add(actionPanel, 0, 2);
            layout.Controls.Add(_progress, 0, 3);
            layout.Controls.Add(_lblStatus, 0, 4);
            Controls.Add(layout);

            // 结果列表右键菜单
            var miOpen = new ToolStripMenuItem("打开文件");
            miOpen.Click += (s, e) => OpenSelected(true);
            var miLoc = new ToolStripMenuItem("打开文件所在位置");
            miLoc.Click += (s, e) => OpenSelected(false);
            var cms = new ContextMenuStrip();
            cms.Items.Add(miOpen);
            cms.Items.Add(miLoc);
            _lvResult.ContextMenuStrip = cms;
        }

        // 右键操作：open=true 用默认程序打开；open=false 在资源管理器中定位
        private void OpenSelected(bool open)
        {
            if (_lvResult.SelectedItems.Count == 0) return;
            string path = _lvResult.SelectedItems[0].SubItems[4].Text;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (open)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                    { Arguments = "/select,\"" + path + "\"", UseShellExecute = false });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private Button MakeButton(string text, int width = 150)
        {
            return new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, 28),
                Margin = new Padding(0, 0, 8, 0)
            };
        }

        // “忽略 [xx] kB 以下的文件”整组（Label+输入框+单位合成一个控件，保证同行）
        private FlowLayoutPanel BuildMinSizeGroup()
        {
            _numMinSize = new NumericUpDown
            {
                Value = Scanner.MinFileSize / 1024,
                Minimum = 0,
                Maximum = 1000000,
                Increment = 10,
                Width = 76,
                Margin = new Padding(0, 2, 0, 0)
            };
            var group = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(6, 6, 4, 0),
                Anchor = AnchorStyles.Left
            };
            group.Controls.Add(new Label { Text = "忽略 ", AutoSize = true, Anchor = AnchorStyles.Left });
            group.Controls.Add(_numMinSize);
            group.Controls.Add(new Label { Text = " kB 以下的文件", AutoSize = true, Anchor = AnchorStyles.Left });
            return group;
        }

        // 让“路径”列自动铺满列表剩余宽度
        private void ResizePathColumn()
        {
            int fixedW = 60 + 220 + 90 + 140 + 6;
            int avail = _lvResult.ClientSize.Width - fixedW;
            if (_lvResult.Columns.Count >= 5 && avail > 120)
                _lvResult.Columns[4].Width = avail;
        }

        // ---- 操作：文件夹管理 ----
        private void BtnImport_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            using (var ofd = new OpenFileDialog
            {
                Title = "选择 ktv_config.json",
                Filter = "JSON 配置文件|ktv_config.json;*.json|所有文件|*.*"
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var dirs = Scanner.LoadKtvConfig(ofd.FileName);
                    if (dirs.Count == 0)
                    {
                        MessageBox.Show(this, "配置文件中没有找到 mediaDirs 目录列表，无法导入。\n你可以点“添加文件夹”手动选择。",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    AddDirs(dirs);
                    SetStatus("已从配置导入 " + dirs.Count + " 个文件夹。");

                    // 检查每个文件夹在本机是否存在（KTV 配置常指向其他机器/驱动器的路径）
                    var missing = new List<string>();
                    foreach (string d in dirs)
                        if (!System.IO.Directory.Exists(d)) missing.Add(d);
                    if (missing.Count > 0)
                    {
                        MessageBox.Show(this,
                            "本次导入中有 " + missing.Count + " 个文件夹在本机不存在（可能属于其他机器或未挂载的驱动器）：\n\n"
                            + string.Join("\n", missing)
                            + "\n\n这些文件夹已显示在列表中，扫描时会自动跳过。可在列表中移除或用“添加文件夹”补齐。",
                            "部分文件夹不存在", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "解析配置失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            using (var fbd = new FolderBrowserDialog { Description = "选择要扫描的曲库文件夹" })
            {
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                AddDirs(new List<string> { fbd.SelectedPath });
                SetStatus("已添加文件夹：" + fbd.SelectedPath);
            }
        }

        private void BtnRemove_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            if (_dirList.SelectedIndex < 0)
            {
                MessageBox.Show(this, "请先在列表中选中要移除的文件夹。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int idx = _dirList.SelectedIndex;
            _dirs.RemoveAt(idx);
            RefreshDirList();
        }

        private void AddDirs(List<string> dirs)
        {
            foreach (string d in dirs)
            {
                string norm = d.TrimEnd('\\', '/');
                if (string.IsNullOrEmpty(norm)) continue;
                if (!_dirs.Any(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)))
                    _dirs.Add(norm);
            }
            RefreshDirList();
        }

        private void RefreshDirList()
        {
            _dirList.Items.Clear();
            _dirList.Items.AddRange(_dirs.ToArray());
        }

        // ---- 操作：扫描 ----
        private void BtnScan_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            if (_dirs.Count == 0)
            {
                MessageBox.Show(this, "请先添加至少一个扫描文件夹。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Scanner.MinFileSize = (long)Math.Max(0, _numMinSize.Value) * 1024;
            _groups.Clear();
            _folderMode = false;
            _lvResult.Items.Clear();
            _btnClean.Enabled = false;
            _cancel = false;
            _uiTick.Restart();
            SetBusy(true);

            Task.Run(() =>
            {
                try
                {
                    RunOnUi(delegate { _progress.Style = ProgressBarStyle.Marquee; SetStatus("正在统计并扫描文件…"); });

                    // 扫描（在后台线程执行，仅进度回调切回界面线程；限流避免刷爆消息队列）
                    List<MediaFile> files = Scanner.ScanAll(new List<string>(_dirs),
                        (cur, total, name) =>
                        {
                            if (_cancel) throw new OperationCanceledException();
                            if (!UiDue()) return;
                            RunOnUi(delegate
                            {
                                if (total > 0)
                                {
                                    _progress.Style = ProgressBarStyle.Continuous;
                                    _progress.Maximum = total;
                                    _progress.Value = Math.Min(cur, total);
                                }
                                _lblStatus.Text = "正在扫描：" + name;
                            });
                        });

                    int count = files.Count;
                    RunOnUi(delegate { SetStatus("扫描完成，共 " + count + " 个文件，正在比对查重…"); });

                    // 查重（后台线程执行；同样限流）
                    List<DupGroup> groups = Scanner.Detect(files, (done, total) =>
                    {
                        if (_cancel) throw new OperationCanceledException();
                        if (!UiDue()) return;
                        RunOnUi(delegate
                        {
                            if (total > 0)
                            {
                                _progress.Maximum = total;
                                _progress.Value = Math.Min(done, total);
                                _lblStatus.Text = "正在比对 " + done + "/" + total;
                            }
                        });
                    });
                    RunOnUi(delegate { ShowGroups(groups); });
                }
                catch (OperationCanceledException)
                {
                    RunOnUi(delegate { SetStatus("已取消。"); });
                }
                catch (AggregateException ae)
                {
                    if (ae.Flatten().InnerExceptions.Any(x => x is OperationCanceledException))
                        RunOnUi(delegate { SetStatus("已取消。"); });
                    else
                        RunOnUi(delegate { SetStatus("扫描出错：" + ae.Flatten().Message); });
                }
                catch (Exception ex)
                {
                    RunOnUi(delegate { SetStatus("出错：" + ex.Message); });
                }
                finally
                {
                    RunOnUi(delegate { SetBusy(false); });
                }
            });
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            _cancel = true;
            _btnCancel.Enabled = false;
            SetStatus("正在取消…");
        }

        // ---- 操作：扫描重复文件夹 ----
        private void BtnScanDir_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            if (_dirs.Count == 0)
            {
                MessageBox.Show(this, "请先添加至少一个扫描文件夹。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Scanner.MinFileSize = (long)Math.Max(0, _numMinSize.Value) * 1024;
            _groups.Clear();
            _folderMode = true;
            _lvResult.Items.Clear();
            _btnClean.Enabled = false;
            _cancel = false;
            _uiTick.Restart();
            SetBusy(true);

            Task.Run(() =>
            {
                try
                {
                    RunOnUi(delegate { _progress.Style = ProgressBarStyle.Marquee; SetStatus("正在统计文件夹…"); });

                    List<Scanner.FolderEntry> folders = Scanner.ScanFolders(new List<string>(_dirs),
                        (cur, total, name) =>
                        {
                            if (_cancel) throw new OperationCanceledException();
                            if (!UiDue()) return;
                            RunOnUi(delegate
                            {
                                if (total > 0)
                                {
                                    _progress.Style = ProgressBarStyle.Continuous;
                                    _progress.Maximum = total;
                                    _progress.Value = Math.Min(cur, total);
                                }
                                _lblStatus.Text = "正在扫描文件夹：" + name;
                            });
                        });

                    int count = folders.Count;
                    RunOnUi(delegate { SetStatus("文件夹统计完成，共 " + count + " 个，正在比对…"); });

                    List<Scanner.DupFolderGroup> groups = Scanner.DetectFolders(folders, (done, total) =>
                    {
                        if (_cancel) throw new OperationCanceledException();
                        if (!UiDue()) return;
                        RunOnUi(delegate
                        {
                            if (total > 0)
                            {
                                _progress.Maximum = total;
                                _progress.Value = Math.Min(done, total);
                                _lblStatus.Text = "正在比对文件夹 " + done + "/" + total;
                            }
                        });
                    });
                    RunOnUi(delegate { ShowFolderGroups(groups); });
                }
                catch (OperationCanceledException)
                {
                    RunOnUi(delegate { SetStatus("已取消。"); });
                }
                catch (AggregateException ae)
                {
                    if (ae.Flatten().InnerExceptions.Any(x => x is OperationCanceledException))
                        RunOnUi(delegate { SetStatus("已取消。"); });
                    else
                        RunOnUi(delegate { SetStatus("扫描出错：" + ae.Flatten().Message); });
                }
                catch (Exception ex)
                {
                    RunOnUi(delegate { SetStatus("出错：" + ex.Message); });
                }
                finally
                {
                    RunOnUi(delegate { SetBusy(false); });
                }
            });
        }

        private void ShowFolderGroups(List<Scanner.DupFolderGroup> groups)
        {
            _lvResult.BeginUpdate();
            _lvResult.Items.Clear();
            int kept = 0, dup = 0;
            foreach (var g in groups)
            {
                _lvResult.Items.Add(NewFolderRow(true, g.Keep)); kept++;
                foreach (var d in g.Delete)
                {
                    _lvResult.Items.Add(NewFolderRow(false, d)); dup++;
                }
            }
            _lvResult.EndUpdate();
            _btnClean.Enabled = groups.Count > 0;
            SetStatus(string.Format("文件夹查重完成：发现 {0} 组重复文件夹，待清理 {1} 个，共可释放约 {2}。",
                groups.Count, dup, Scanner.FormatSize(groups.Sum(x => (long)x.Delete.Count * (x.Delete.Count > 0 ? x.Delete[0].TotalSize : 0)))));
        }

        private ListViewItem NewFolderRow(bool keep, Scanner.FolderEntry fe)
        {
            var item = new ListViewItem(keep ? "保留" : "待删");
            item.SubItems.Add(fe.Name);
            item.SubItems.Add(fe.FileCount + " 个文件 / " + Scanner.FormatSize(fe.TotalSize));
            item.SubItems.Add(fe.Created.ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(fe.Path);
            item.Checked = !keep; // 保留项默认不勾选，待删项默认勾选
            return item;
        }

        // 限流：距上次真正刷新 UI 至少 80ms 才返回 true，避免海量回调刷爆消息队列
        private bool UiDue()
        {
            lock (_uiLock)
            {
                if (_uiTick.ElapsedMilliseconds < 80) return false;
                _uiTick.Restart();
                return true;
            }
        }

        // 删除进度：只更新"已删除 x/xxxx"状态，不做逐条列表动态渲染
        private void OnDeleteProgress(int done, int total)
        {
            if (_cancel) throw new OperationCanceledException();
            if (!UiDue()) return;
            RunOnUi(delegate { SetStatus(string.Format("已删除 {0}/{1}", done, total)); });
        }

        private void ShowGroups(List<DupGroup> groups)
        {
            _groups = groups;
            _lvResult.BeginUpdate();
            _lvResult.Items.Clear();
            int kept = 0, dup = 0;
            foreach (var g in groups)
            {
                _lvResult.Items.Add(NewRow(true, g.Keep)); kept++;
                foreach (var d in g.Delete)
                {
                    _lvResult.Items.Add(NewRow(false, d)); dup++;
                }
            }
            _lvResult.EndUpdate();
            _btnClean.Enabled = groups.Count > 0;
            SetStatus(string.Format("查重完成：发现 {0} 组重复，待清理 {1} 个，共可释放约 {2}。",
                groups.Count, dup, Scanner.FormatSize(groups.Sum(x => (long)x.Delete.Count * x.Size))));
        }

        private ListViewItem NewRow(bool keep, MediaFile mf)
        {
            var item = new ListViewItem(keep ? "保留" : "待删");
            item.SubItems.Add(mf.Name);
            item.SubItems.Add(Scanner.FormatSize(mf.Size));
            item.SubItems.Add(mf.Created.ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(mf.Path);
            item.Checked = !keep; // 保留项默认不勾选，待删项默认勾选
            return item;
        }

        private void LvResult_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            // 不允许取消“保留”项
            if (e.Item.Text == "保留" && !e.Item.Checked)
            {
                // 保持勾选设置为保留项不可勾选
                e.Item.SubItems[0].BackColor = Color.GhostWhite;
                e.Item.Checked = false; // 保留项始终不勾选
            }
        }

        // ---- 操作：一键清理 ----
        private void BtnClean_Click(object sender, EventArgs e)
        {
            if (_busy || _lvResult.Items.Count == 0) return;
            bool isDir = _folderMode;
            var toDelete = new List<string>();
            foreach (ListViewItem it in _lvResult.Items)
            {
                if (it.Checked && it.Text == "待删")
                    toDelete.Add(it.SubItems[4].Text);
            }
            if (toDelete.Count == 0)
            {
                MessageBox.Show(this, "没有勾选任何" + (isDir ? "待删除的文件夹" : "待删除的文件") + "。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string mode = _rbRecycle.Checked ? "移到回收站" : "永久删除";
            string head = "重复文件";
            string note = "（将保留每组最早进入硬盘的那一个）";
            if (isDir)
            {
                head = "重复文件夹";
                note = "（将保留每个重复组中最早的一个文件夹，其余整文件夹删除，其中的所有文件一并删除！）";
            }
            var confirm = MessageBox.Show(this,
                string.Format("确认要{0}选中的 {1} 个{2}吗？\n\n{3}", mode, toDelete.Count, head, note),
                "确认清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            SetBusy(true);
            _cancel = false;
            Task.Run(() =>
            {
                try
                {
                    int fail;
                    RunOnUi(delegate { SetStatus("正在清理…"); });
                    Action<int, int> cb = OnDeleteProgress;
                    if (_rbRecycle.Checked)
                    {
                        if (isDir) fail = Scanner.DeleteFolderToRecycleBin(toDelete, cb);
                        else fail = Scanner.DeleteToRecycleBin(toDelete, cb);
                    }
                    else
                    {
                        if (isDir) fail = Scanner.DeleteFolderPermanent(toDelete, cb);
                        else fail = Scanner.DeletePermanent(toDelete, cb);
                    }

                    int ok = toDelete.Count - fail;
                    // 从列表中移除已删除项
                    var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string p in toDelete)
                    {
                        bool exists;
                        try { exists = isDir ? System.IO.Directory.Exists(p) : System.IO.File.Exists(p); }
                        catch { exists = true; }
                        if (!exists) gone.Add(p);
                    }
                    int removed = 0;
                    for (int i = _lvResult.Items.Count - 1; i >= 0; i--)
                    {
                        string p = _lvResult.Items[i].SubItems[4].Text;
                        if (gone.Contains(p)) { _lvResult.Items.RemoveAt(i); removed++; }
                    }
                    if (_lvResult.Items.Count == 0) _btnClean.Enabled = false;

                    RunOnUi(delegate
                    {
                        SetStatus(string.Format("清理完成：成功 {0} 个，失败 {1} 个，已从列表移除 {2} 项。",
                            ok, fail, removed));
                        if (fail > 0)
                            MessageBox.Show(this, string.Format("清理完成，但有 {0} 项失败（可能被占用或已不存在）。", fail),
                                "部分失败", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (OperationCanceledException)
                {
                    RunOnUi(delegate { SetStatus("已取消，剩余文件未删除。"); });
                }
                catch (Exception ex)
                {
                    RunOnUi(delegate { SetStatus("清理出错：" + ex.Message); });
                }
                finally
                {
                    RunOnUi(delegate { SetBusy(false); });
                }
            });
        }

        // ---- 辅助 ----
        private void RunOnUi(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) a(); });
            else a();
        }

        private void SetStatus(string s)
        {
            _lblStatus.Text = s;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnImport.Enabled = !busy;
            _btnAdd.Enabled = !busy;
            _btnRemove.Enabled = !busy;
            _btnScan.Enabled = !busy;
            _btnScanDir.Enabled = !busy;
            _btnCancel.Enabled = busy;
            _btnClean.Enabled = !busy && _lvResult.Items.Count > 0;
            _dirList.Enabled = !busy;
        }
    }
}