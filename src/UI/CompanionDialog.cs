using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ProxyPicker.Core;

namespace ProxyPicker.UI
{
    /// <summary>
    /// 双向软件联动启停设置对话框（v2.6.0）：
    /// 1) 跟随电脑上指定软件同步启停（支持同生共死关闭自动还原退出、开机静默守望及桌面原生图标联动快捷方式）；
    /// 2) 打开 ProxyPicker 时自动同步拉起用户勾选的电脑软件（代理客户端优先，规则生效后再启动其余业务软件）。
    /// 全控件基于 Theme.S 显式物理像素缩放（AutoScaleMode=None），确保 100%–200% 高 DPI 屏清晰无裁切。
    /// </summary>
    internal sealed class CompanionDialog : Form
    {
        readonly AppCompanion.CompanionConfig _initial;
        readonly List<AppCompanion.AppCandidate> _candidates;
        readonly List<AppCompanion.AppCandidate> _launchDisplayed = new List<AppCompanion.AppCandidate>();
        readonly HashSet<string> _selectedLaunchPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<Font> _ownedFonts = new List<Font>();

        CheckBox _chkFollowEnable;
        ComboBox _cmbFollowApp;
        Button _btnBrowseFollow;
        Label _lblFollowPath;
        CheckBox _chkFollowAutoExit;
        CheckBox _chkFollowWatchLogon;
        Button _btnCreateShortcut;

        CheckBox _chkLaunchEnable;
        CheckBox _chkLaunchShowAll;
        CheckedListBox _chkLaunchApps;
        Button _btnBrowseLaunch;
        CheckBox _chkLaunchOnFollow;
        Label _lblHint;

        Button _btnLaunchNow;
        Button _btnCancel;
        Button _btnSave;

        bool _suppressCheck;
        string _customFollowPath = "";
        string _customFollowName = "";

        public AppCompanion.CompanionConfig ResultConfig { get; private set; }
        public bool LaunchNowRequested { get; private set; }

        int S(float logicalPx) { return Theme.Px(logicalPx); }

        Font F(float logicalPx, FontStyle style = FontStyle.Regular)
        {
            var f = Theme.Ui(logicalPx * Theme.S, style);
            _ownedFonts.Add(f);
            return f;
        }

        public CompanionDialog(
            AppCompanion.CompanionConfig current,
            List<AppCompanion.AppCandidate> candidates)
        {
            _initial = (current ?? new AppCompanion.CompanionConfig()).Clone();
            _candidates = candidates != null
                ? new List<AppCompanion.AppCandidate>(candidates)
                : new List<AppCompanion.AppCandidate>();

            foreach (var p in _initial.LaunchAppPaths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    string clean = p.Trim().Trim('"');
                    _selectedLaunchPaths.Add(clean);
                    EnsureCandidateForPath(clean, "");
                }
            }
            if (!string.IsNullOrWhiteSpace(_initial.FollowAppPath))
            {
                _customFollowPath = _initial.FollowAppPath.Trim().Trim('"');
                _customFollowName = _initial.FollowAppName ?? "";
                EnsureCandidateForPath(_customFollowPath, _customFollowName);
            }

            Text = "ProxyPicker · 软件联动同步启停";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = F(12f);
            ClientSize = new Size(S(640), S(606));
            try { Icon = Draw.AppIco(32); } catch { }

            BuildControls();
            LoadInitialValues();
            UpdateEnabledStates();
        }

        void EnsureCandidateForPath(string fullPath, string nameHint)
        {
            string exe = AppCompanion.ExtractExeName(fullPath);
            if (exe.Length == 0) return;
            foreach (var c in _candidates)
            {
                if (string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(c.FullPath) && !string.IsNullOrWhiteSpace(fullPath))
                        c.FullPath = fullPath;
                    return;
                }
            }
            string nm = !string.IsNullOrWhiteSpace(nameHint)
                ? nameHint.Trim()
                : Catalog.DisplayName(exe);
            if (string.IsNullOrWhiteSpace(nm)) nm = Path.GetFileNameWithoutExtension(fullPath);
            _candidates.Add(new AppCompanion.AppCandidate
            {
                Exe = exe,
                Name = nm,
                FullPath = fullPath,
                IsProxyClient = AppCompanion.IsProxyClientExe(exe),
                Running = AppCompanion.IsProcessRunning(exe)
            });
        }

        void BuildControls()
        {
            int pad = S(20);
            int innerW = ClientSize.Width - pad * 2;

            var lblTitle = new Label
            {
                Left = pad, Top = S(14), Width = innerW, Height = S(28),
                ForeColor = Theme.Fg, Font = F(15.5f, FontStyle.Bold),
                Text = "软件联动同步启停（双向自动联动）"
            };
            Controls.Add(lblTitle);

            var lblSub = new Label
            {
                Left = pad, Top = S(42), Width = innerW, Height = S(22),
                ForeColor = Theme.Dim, Font = F(11.3f),
                Text = "省去每次手动点开多个软件：既可跟随指定软件同生共死，也可打开 ProxyPicker 时一键拉起常用软件。"
            };
            Controls.Add(lblSub);

            // 区块一：跟随电脑软件同步启停
            var pFollow = new Panel
            {
                Left = pad, Top = S(70), Width = innerW, Height = S(206),
                BackColor = Theme.Card, BorderStyle = BorderStyle.FixedSingle
            };
            int fp = S(14);
            int fw = pFollow.Width - fp * 2;

            _chkFollowEnable = new CheckBox
            {
                Left = fp, Top = S(10), Width = fw, Height = S(24),
                ForeColor = Theme.Fg, Font = F(13f, FontStyle.Bold),
                Text = "1. 跟随指定软件同步开启 ProxyPicker（打开它时自动后台生效分流）"
            };
            _chkFollowEnable.CheckedChanged += (s, e) => UpdateEnabledStates();
            pFollow.Controls.Add(_chkFollowEnable);

            int browseW = S(104);
            _cmbFollowApp = new ComboBox
            {
                Left = fp, Top = S(40), Width = fw - browseW - S(8),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Theme.Bg, ForeColor = Theme.Fg, Font = F(12f)
            };
            _cmbFollowApp.SelectedIndexChanged += (s, e) => UpdateFollowPathLabel();
            pFollow.Controls.Add(_cmbFollowApp);

            _btnBrowseFollow = MakeFlatButton("浏览 .exe…", fp + _cmbFollowApp.Width + S(8), S(39), browseW, S(28));
            _btnBrowseFollow.Click += (s, e) => BrowseFollowExe();
            pFollow.Controls.Add(_btnBrowseFollow);

            _lblFollowPath = new Label
            {
                Left = fp, Top = S(72), Width = fw, Height = S(18),
                ForeColor = Theme.Dim, Font = F(10.8f),
                AutoEllipsis = true,
                Text = "尚未选择目标软件"
            };
            pFollow.Controls.Add(_lblFollowPath);

            _chkFollowAutoExit = new CheckBox
            {
                Left = fp, Top = S(96), Width = fw, Height = S(24),
                ForeColor = Theme.Fg, Font = F(11.8f),
                Text = "目标软件关闭后，ProxyPicker 也自动还原内核配置并退出（同生共死，推荐）",
                Checked = true
            };
            pFollow.Controls.Add(_chkFollowAutoExit);

            _chkFollowWatchLogon = new CheckBox
            {
                Left = fp, Top = S(124), Width = fw, Height = S(24),
                ForeColor = Theme.Fg, Font = F(11.8f),
                Text = "开机静默守望：直接点击原软件图标即可自动拉起 ProxyPicker（仅当前用户，零管理员权限）"
            };
            pFollow.Controls.Add(_chkFollowWatchLogon);

            _btnCreateShortcut = MakeFlatButton("在桌面生成联动快捷方式（零后台常驻）", fp, S(156), S(292), S(32));
            _btnCreateShortcut.ForeColor = Theme.Accent;
            _btnCreateShortcut.Click += (s, e) => CreateDesktopShortcut();
            pFollow.Controls.Add(_btnCreateShortcut);

            var lblShortcutTip = new Label
            {
                Left = fp + S(300), Top = S(162), Width = fw - S(300), Height = S(22),
                ForeColor = Theme.Dim, Font = F(10.8f),
                Text = "保留原软件图标，双击一键双启"
            };
            pFollow.Controls.Add(lblShortcutTip);

            Controls.Add(pFollow);

            // 区块二：打开 ProxyPicker 时自动同步拉起软件
            var pLaunch = new Panel
            {
                Left = pad, Top = S(288), Width = innerW, Height = S(240),
                BackColor = Theme.Card, BorderStyle = BorderStyle.FixedSingle
            };

            _chkLaunchEnable = new CheckBox
            {
                Left = fp, Top = S(10), Width = fw - S(126), Height = S(24),
                ForeColor = Theme.Fg, Font = F(13f, FontStyle.Bold),
                Text = "2. 打开 ProxyPicker 时自动同步开启以下软件"
            };
            _chkLaunchEnable.CheckedChanged += (s, e) => UpdateEnabledStates();
            pLaunch.Controls.Add(_chkLaunchEnable);

            _chkLaunchShowAll = new CheckBox
            {
                Left = fp + fw - S(122), Top = S(11), Width = S(122), Height = S(22),
                ForeColor = Theme.Accent, Font = F(11.2f),
                Text = "显示全部软件",
                TextAlign = ContentAlignment.MiddleRight,
                CheckAlign = ContentAlignment.MiddleRight
            };
            _chkLaunchShowAll.CheckedChanged += (s, e) => RebuildLaunchChecklist();
            pLaunch.Controls.Add(_chkLaunchShowAll);

            var lblOrderTip = new Label
            {
                Left = fp, Top = S(36), Width = fw, Height = S(18),
                ForeColor = Theme.Dim, Font = F(10.8f),
                Text = "代理客户端（如 Clash Verge）自动优先拉起并等规则生效，再启动其余勾选软件；已在运行的不重复多开。"
            };
            pLaunch.Controls.Add(lblOrderTip);

            _chkLaunchApps = new CheckedListBox
            {
                Left = fp, Top = S(58), Width = fw, Height = S(116),
                CheckOnClick = true,
                IntegralHeight = false,
                BackColor = Theme.Bg, ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = F(11.8f)
            };
            _chkLaunchApps.ItemCheck += OnLaunchItemCheck;
            pLaunch.Controls.Add(_chkLaunchApps);

            _chkLaunchOnFollow = new CheckBox
            {
                Left = fp, Top = S(180), Width = fw - S(136), Height = S(24),
                ForeColor = Theme.Fg, Font = F(11.3f),
                Text = "随目标软件联动开启时，也一并拉起上面勾选的其他未运行软件",
                Checked = true
            };
            pLaunch.Controls.Add(_chkLaunchOnFollow);

            _btnBrowseLaunch = MakeFlatButton("添加其他 .exe…", fp + fw - S(126), S(178), S(126), S(28));
            _btnBrowseLaunch.Click += (s, e) => BrowseAddLaunchExe();
            pLaunch.Controls.Add(_btnBrowseLaunch);

            _lblHint = new Label
            {
                Left = fp, Top = S(210), Width = fw, Height = S(20),
                ForeColor = Theme.Dim, Font = F(10.8f),
                AutoEllipsis = true
            };
            pLaunch.Controls.Add(_lblHint);

            Controls.Add(pLaunch);

            // 底部动作按钮
            int btnY = S(546);
            int btnH = S(38);
            _btnLaunchNow = MakeFlatButton("立即拉起已选软件", pad, btnY, S(148), btnH);
            _btnLaunchNow.Click += (s, e) => OnLaunchNowClick();
            Controls.Add(_btnLaunchNow);

            _btnSave = new Button
            {
                Left = ClientSize.Width - pad - S(132), Top = btnY, Width = S(132), Height = btnH,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent, ForeColor = Color.White,
                Font = F(12.2f, FontStyle.Bold),
                Text = "保存并生效",
                Cursor = Cursors.Hand
            };
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.Click += (s, e) => OnSaveClick();
            Controls.Add(_btnSave);

            _btnCancel = MakeFlatButton("取消", _btnSave.Left - S(10) - S(92), btnY, S(92), btnH);
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }

        Button MakeFlatButton(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Left = x, Top = y, Width = w, Height = h,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Btn, ForeColor = Theme.Fg,
                Font = F(11.6f),
                Text = text,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Theme.Line;
            b.FlatAppearance.BorderSize = 1;
            return b;
        }

        sealed class FollowComboItem
        {
            public AppCompanion.AppCandidate Candidate;
            public override string ToString()
            {
                if (Candidate == null) return "（未选择）";
                string tag = Candidate.IsProxyClient ? " [代理客户端]" : (Candidate.Running ? " [运行中]" : "");
                return Candidate.Name + " (" + Candidate.Exe + ")" + tag;
            }
        }

        void LoadInitialValues()
        {
            _chkFollowEnable.Checked = _initial.FollowEnabled;
            _chkFollowAutoExit.Checked = _initial.FollowAutoExit;
            _chkFollowWatchLogon.Checked = _initial.FollowWatchAtLogon;

            RebuildFollowCombo(_initial.FollowAppExe, _initial.FollowAppPath);

            _chkLaunchEnable.Checked = _initial.LaunchAppsEnabled;
            _chkLaunchOnFollow.Checked = _initial.LaunchAppsOnFollow;
            RebuildLaunchChecklist();
            UpdateLaunchSummary();
        }

        void RebuildFollowCombo(string selectExe, string selectPath)
        {
            _cmbFollowApp.Items.Clear();
            int selIdx = -1;
            for (int i = 0; i < _candidates.Count; i++)
            {
                var c = _candidates[i];
                _cmbFollowApp.Items.Add(new FollowComboItem { Candidate = c });
                if (selIdx < 0)
                {
                    if (!string.IsNullOrWhiteSpace(selectPath) &&
                        string.Equals(c.FullPath, selectPath, StringComparison.OrdinalIgnoreCase))
                        selIdx = i;
                    else if (!string.IsNullOrWhiteSpace(selectExe) &&
                             string.Equals(c.Exe, selectExe, StringComparison.OrdinalIgnoreCase))
                        selIdx = i;
                }
            }
            if (selIdx < 0 && _cmbFollowApp.Items.Count > 0) selIdx = 0;
            if (selIdx >= 0) _cmbFollowApp.SelectedIndex = selIdx;
            UpdateFollowPathLabel();
        }

        AppCompanion.AppCandidate SelectedFollowCandidate()
        {
            var item = _cmbFollowApp.SelectedItem as FollowComboItem;
            return item != null ? item.Candidate : null;
        }

        void UpdateFollowPathLabel()
        {
            var c = SelectedFollowCandidate();
            if (c == null)
            {
                _lblFollowPath.Text = "点右侧「浏览 .exe…」可选任意电脑软件";
                _lblFollowPath.ForeColor = Theme.Dim;
            }
            else
            {
                _lblFollowPath.Text = "程序路径：" + c.FullPath;
                _lblFollowPath.ForeColor = Theme.Dim;
            }
        }

        void RebuildLaunchChecklist()
        {
            _suppressCheck = true;
            try
            {
                _chkLaunchApps.Items.Clear();
                _launchDisplayed.Clear();
                bool showAll = _chkLaunchShowAll.Checked;
                foreach (var c in _candidates)
                {
                    bool selected = _selectedLaunchPaths.Contains(c.FullPath);
                    if (!showAll && !selected && !c.IsProxyClient && !c.Proxied && !c.Running) continue;
                    _launchDisplayed.Add(c);
                    string badge = c.IsProxyClient ? " · 代理客户端(优先)" : (c.Proxied ? " · 已选分流" : (c.Running ? " · 运行中" : ""));
                    _chkLaunchApps.Items.Add(c.Name + " (" + c.Exe + ")" + badge, selected);
                }
            }
            finally { _suppressCheck = false; }
            UpdateLaunchSummary();
        }

        void OnLaunchItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressCheck || e.Index < 0 || e.Index >= _launchDisplayed.Count) return;
            var c = _launchDisplayed[e.Index];
            if (e.NewValue == CheckState.Checked) _selectedLaunchPaths.Add(c.FullPath);
            else _selectedLaunchPaths.Remove(c.FullPath);
            BeginInvoke(new Action(UpdateLaunchSummary));
        }

        void UpdateLaunchSummary()
        {
            var ordered = AppCompanion.OrderLaunchTargets(_selectedLaunchPaths);
            if (ordered.Count == 0)
            {
                _lblHint.Text = "当前尚未勾选同步拉起的软件（可从上方勾选，或点右侧添加任意本地 .exe）";
                _lblHint.ForeColor = Theme.Dim;
            }
            else
            {
                var names = ordered.Select(p => Path.GetFileName(p)).Take(4).ToList();
                string tail = ordered.Count > 4 ? (" 等共 " + ordered.Count + " 个") : ("（共 " + ordered.Count + " 个）");
                _lblHint.Text = "启动顺序：" + string.Join(" → ", names) + tail;
                _lblHint.ForeColor = Theme.Ok;
            }
            if (_btnLaunchNow != null) _btnLaunchNow.Enabled = ordered.Count > 0;
        }

        void UpdateEnabledStates()
        {
            bool fOn = _chkFollowEnable.Checked;
            _cmbFollowApp.Enabled = fOn;
            _btnBrowseFollow.Enabled = fOn;
            _chkFollowAutoExit.Enabled = fOn;
            _chkFollowWatchLogon.Enabled = fOn;
            _btnCreateShortcut.Enabled = fOn && SelectedFollowCandidate() != null;

            bool lOn = _chkLaunchEnable.Checked;
            _chkLaunchShowAll.Enabled = lOn;
            _chkLaunchApps.Enabled = lOn;
            _btnBrowseLaunch.Enabled = lOn;
            _chkLaunchOnFollow.Enabled = lOn;
        }

        void BrowseFollowExe()
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "选择要跟随同步启停的软件（.exe）",
                Filter = "应用程序 (*.exe)|*.exe",
                CheckFileExists = true
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                string err;
                if (!AppCompanion.ValidateTargetExeOrPath(ofd.FileName, true, out err))
                {
                    MessageBox.Show(this, err, "ProxyPicker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string fn = Path.GetFileNameWithoutExtension(ofd.FileName);
                EnsureCandidateForPath(ofd.FileName, fn);
                RebuildFollowCombo(AppCompanion.ExtractExeName(ofd.FileName), ofd.FileName);
                UpdateEnabledStates();
            }
        }

        void BrowseAddLaunchExe()
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "添加打开 ProxyPicker 时同步拉起的软件（.exe）",
                Filter = "应用程序 (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = true
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                foreach (var fp in ofd.FileNames)
                {
                    string err;
                    if (!AppCompanion.ValidateTargetExeOrPath(fp, true, out err))
                    {
                        MessageBox.Show(this, Path.GetFileName(fp) + "：" + err, "ProxyPicker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }
                    EnsureCandidateForPath(fp, Path.GetFileNameWithoutExtension(fp));
                    _selectedLaunchPaths.Add(fp);
                }
                RebuildLaunchChecklist();
            }
        }

        void CreateDesktopShortcut()
        {
            var c = SelectedFollowCandidate();
            if (c == null)
            {
                MessageBox.Show(this, "请先选择要跟随的目标软件。", "ProxyPicker", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string lnkPath, err;
            if (AppCompanion.CreateCompanionShortcut(c.FullPath, c.Name, null, Application.ExecutablePath, out lnkPath, out err))
            {
                MessageBox.Show(this,
                    "已在桌面生成联动快捷方式：\n" + Path.GetFileName(lnkPath) +
                    "\n\n以后直接双击这个图标，就会同时打开「" + c.Name + "」并在后台自动启动 ProxyPicker 套用分流；" +
                    (_chkFollowAutoExit.Checked ? "关闭它时 ProxyPicker 也会自动还原并退出。" : ""),
                    "ProxyPicker", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "生成快捷方式失败：" + err, "ProxyPicker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        AppCompanion.CompanionConfig BuildConfig(out string error)
        {
            error = "";
            var cfg = new AppCompanion.CompanionConfig
            {
                FollowEnabled = _chkFollowEnable.Checked,
                FollowAutoExit = _chkFollowAutoExit.Checked,
                FollowWatchAtLogon = _chkFollowWatchLogon.Checked,
                LaunchAppsEnabled = _chkLaunchEnable.Checked,
                LaunchAppsOnFollow = _chkLaunchOnFollow.Checked,
                LaunchAppPaths = AppCompanion.OrderLaunchTargets(_selectedLaunchPaths)
            };

            var fc = SelectedFollowCandidate();
            if (fc != null)
            {
                cfg.FollowAppExe = fc.Exe;
                cfg.FollowAppPath = fc.FullPath;
                cfg.FollowAppName = fc.Name;
            }
            if (cfg.FollowEnabled)
            {
                if (fc == null || !AppCompanion.ValidateTargetExeOrPath(fc.FullPath, true, out error))
                {
                    if (string.IsNullOrEmpty(error)) error = "请先选择要跟随同步启停的电脑软件";
                    return null;
                }
            }
            if (cfg.LaunchAppsEnabled && cfg.LaunchAppPaths.Count == 0)
            {
                error = "已勾选「打开 ProxyPicker 时自动同步开启软件」，请至少勾选 1 个软件，或取消该开关";
                return null;
            }
            return cfg;
        }

        void OnSaveClick()
        {
            string err;
            var cfg = BuildConfig(out err);
            if (cfg == null)
            {
                MessageBox.Show(this, err, "ProxyPicker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ResultConfig = cfg;
            LaunchNowRequested = false;
            DialogResult = DialogResult.OK;
            Close();
        }

        void OnLaunchNowClick()
        {
            string err;
            var cfg = BuildConfig(out err);
            if (cfg == null)
            {
                // 仅立即拉起勾选软件而不强制校验跟随项
                var paths = AppCompanion.OrderLaunchTargets(_selectedLaunchPaths);
                if (paths.Count == 0) return;
                cfg = _initial.Clone();
                cfg.LaunchAppPaths = paths;
            }
            ResultConfig = cfg;
            LaunchNowRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var f in _ownedFonts) { try { f.Dispose(); } catch { } }
                _ownedFonts.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
