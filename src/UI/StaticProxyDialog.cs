using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ProxyPicker.Core;

namespace ProxyPicker.UI
{
    /// <summary>
    /// 极简、大字清晰的「独立固定 IP（可选 · 链式加速 / 直连落地）」配置与分流指定对话框：
    /// 1) 所有字体显式乘以 Theme.S 匹配主界面像素级清晰度（解决高 DPI 下原生控件字号过小问题）；
    /// 2) 第 1 步：一键粘贴 SOCKS5 参数自动拆填；
    /// 3) 第 2 步：连接参数（DPAPI 本机加密存储）+ 折叠式高级设置；
    /// 4) 第 3 步：直接勾选「哪些软件单独使用这个独立 IP」（其余代理软件继续走原高速节点，未选软件继续直连）；
    /// 5) 支持零干扰旁路「测试可用性」与停用防风控二选一保护。
    /// </summary>
    internal sealed class StaticProxyDialog : Form
    {
        public enum DialogOutcome
        {
            Cancelled = 0,
            SavedAndEnabled = 1,
            DisabledToDirect = 2,
            DisabledToDefaultProxy = 3
        }

        public sealed class AppChoice
        {
            public string Exe = "";
            public string Name = "";
            public bool Proxy;
            public bool IsStatic;
            public bool Running;

            public override string ToString()
            {
                string tag = Running ? " · 运行中" : "";
                string route = IsStatic ? "" : (Proxy ? "（当前高速代理）" : "（当前直连）");
                return Name + "  [" + Exe + "]" + route + tag;
            }
        }

        public DialogOutcome Outcome { get; private set; }
        public StaticProxy.StaticProxyConfig ResultConfig { get; private set; }
        /// <summary>用户在弹窗里勾选的「单独使用独立固定 IP」的软件 exe 集合（不区分大小写）。</summary>
        public HashSet<string> SelectedStaticExes { get; private set; }

        readonly StaticProxy.StaticProxyConfig _initial;
        readonly List<string> _availableNodes;
        readonly List<AppChoice> _allApps;
        readonly HashSet<string> _checkedStaticExes;
        readonly string _knownConfigPath;
        readonly List<Font> _ownedFonts = new List<Font>();

        TextBox _txtQuick;
        Label _lblQuickHint;
        TextBox _txtHost;
        TextBox _txtPort;
        TextBox _txtUser;
        TextBox _txtPass;
        Button _btnShowPass;

        LinkLabel _lnkAdv;
        Panel _advPanel;
        bool _advExpanded;
        TextBox _txtLabel;
        ComboBox _cmbHop;
        CheckBox _chkIpv4;
        CheckBox _chkUdp;

        Label _lblStep3;
        CheckBox _chkShowAllApps;
        CheckedListBox _chkApps;
        Label _lblAppSummary;

        Panel _statusBox;
        Label _lblStatus;

        Button _btnTest;
        Button _btnDisable;
        Button _btnSave;
        Button _btnCancel;

        bool _testing;
        bool _suppressQuickParse;
        bool _suppressAppCheck;
        bool _lastVerifyOk;
        string _lastVerifySummary = "";
        long _lastVerifyTicks;

        public StaticProxyDialog(
            StaticProxy.StaticProxyConfig current,
            IList<string> concreteNodes,
            IList<AppChoice> apps,
            string knownConfigPath)
        {
            _initial = (current ?? new StaticProxy.StaticProxyConfig()).Clone();
            _availableNodes = concreteNodes != null ? new List<string>(concreteNodes) : new List<string>();
            _allApps = apps != null ? new List<AppChoice>(apps) : new List<AppChoice>();
            _checkedStaticExes = new HashSet<string>(
                _allApps.Where(a => a.Proxy && a.IsStatic).Select(a => a.Exe),
                StringComparer.OrdinalIgnoreCase);
            SelectedStaticExes = new HashSet<string>(_checkedStaticExes, StringComparer.OrdinalIgnoreCase);
            _knownConfigPath = knownConfigPath;
            _lastVerifyOk = _initial.LastVerifiedOk;
            _lastVerifySummary = _initial.LastVerifiedSummary ?? "";
            _lastVerifyTicks = _initial.LastVerifiedAtTicks;

            AutoScaleMode = AutoScaleMode.None;
            Text = "独立固定 IP（可选 · 按软件单独指定）";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = F(12.5f);

            BuildUi();
            PopulateInitialValues();
            RebuildAppChecklist();
            UpdateLayoutForAdvanced();
        }

        static int S(float logical) { return Theme.Px(logical); }

        /// <summary>原生 WinForms 控件无 Graphics.ScaleTransform，必须乘以 Theme.S 与主界面字号保持一致清晰。</summary>
        Font F(float logicalPx, FontStyle style = FontStyle.Regular)
        {
            var font = Theme.Ui(logicalPx * Theme.S, style);
            _ownedFonts.Add(font);
            return font;
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

        void BuildUi()
        {
            int pad = S(20);
            int w = S(560);
            int innerW = w - pad * 2;

            // 顶部清晰说明
            var lblIntro = new Label
            {
                Left = pad,
                Top = S(14),
                Width = innerW,
                Height = S(42),
                ForeColor = Theme.Dim,
                Font = F(12f),
                Text = "只让下方勾中的软件单独走该固定 IP；其余已代理软件仍走高速节点，未勾软件仍直连。\n默认经由现有高速节点链式加速，不修改原订阅，凭据仅在本机 DPAPI 加密保存。"
            };
            Controls.Add(lblIntro);

            // 步骤 1：一键粘贴
            var lblStep1 = new Label
            {
                Left = pad,
                Top = S(62),
                Width = innerW,
                Height = S(22),
                ForeColor = Theme.Fg,
                Font = F(13f, FontStyle.Bold),
                Text = "1. 一键粘贴 SOCKS5 参数（自动拆分）"
            };
            Controls.Add(lblStep1);

            _txtQuick = new TextBox
            {
                Left = pad,
                Top = S(88),
                Width = innerW,
                Height = S(46),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Card,
                ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = F(12.5f)
            };
            _txtQuick.TextChanged += (s, e) => OnQuickPasteChanged();
            Controls.Add(_txtQuick);

            _lblQuickHint = new Label
            {
                Left = pad,
                Top = S(138),
                Width = innerW,
                Height = S(20),
                ForeColor = Theme.Dim,
                Font = F(11.2f),
                Text = "支持 ip:port:user:pass、user:pass@ip:port、socks5://... 或多行键值复制"
            };
            Controls.Add(_lblQuickHint);

            // 步骤 2：四项核心连接参数
            var lblStep2 = new Label
            {
                Left = pad,
                Top = S(166),
                Width = innerW,
                Height = S(22),
                ForeColor = Theme.Fg,
                Font = F(13f, FontStyle.Bold),
                Text = "2. 连接参数"
            };
            Controls.Add(lblStep2);

            int row1Y = S(192);
            int gap = S(12);
            int portW = S(116);
            int hostW = innerW - portW - gap;

            Controls.Add(MakeFieldLabel("服务器地址（IP 或域名）", pad, row1Y, hostW));
            Controls.Add(MakeFieldLabel("端口", pad + hostW + gap, row1Y, portW));

            _txtHost = MakeInputBox(pad, row1Y + S(22), hostW);
            _txtPort = MakeInputBox(pad + hostW + gap, row1Y + S(22), portW);
            _txtHost.TextChanged += (s, e) => InvalidateVerifyIfEdited();
            _txtPort.TextChanged += (s, e) => InvalidateVerifyIfEdited();
            Controls.Add(_txtHost);
            Controls.Add(_txtPort);

            int row2Y = row1Y + S(58);
            int halfW = (innerW - gap) / 2;
            Controls.Add(MakeFieldLabel("用户名（可选）", pad, row2Y, halfW));
            Controls.Add(MakeFieldLabel("密码（可选）", pad + halfW + gap, row2Y, halfW));

            _txtUser = MakeInputBox(pad, row2Y + S(22), halfW);
            int eyeW = S(58);
            _txtPass = MakeInputBox(pad + halfW + gap, row2Y + S(22), halfW - eyeW - S(6));
            _txtPass.UseSystemPasswordChar = true;
            _txtUser.TextChanged += (s, e) => InvalidateVerifyIfEdited();
            _txtPass.TextChanged += (s, e) => InvalidateVerifyIfEdited();

            _btnShowPass = MakeFlatButton("显示", pad + halfW + gap + _txtPass.Width + S(6), row2Y + S(21), eyeW, S(28));
            _btnShowPass.Click += (s, e) =>
            {
                _txtPass.UseSystemPasswordChar = !_txtPass.UseSystemPasswordChar;
                _btnShowPass.Text = _txtPass.UseSystemPasswordChar ? "显示" : "隐藏";
            };
            Controls.Add(_txtUser);
            Controls.Add(_txtPass);
            Controls.Add(_btnShowPass);

            // 高级设置折叠开关
            _lnkAdv = new LinkLabel
            {
                Left = pad,
                Top = row2Y + S(58),
                AutoSize = true,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Accent,
                VisitedLinkColor = Theme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Font = F(12f),
                Text = "▸ 高级设置（前置加速跳板 / 直连模式 · 防 IPv6 泄漏 · 别名）"
            };
            _lnkAdv.LinkClicked += (s, e) =>
            {
                _advExpanded = !_advExpanded;
                _lnkAdv.Text = _advExpanded
                    ? "▾ 收起高级设置"
                    : "▸ 高级设置（前置加速跳板 / 直连模式 · 防 IPv6 泄漏 · 别名）";
                UpdateLayoutForAdvanced();
            };
            Controls.Add(_lnkAdv);

            // 高级设置面板（默认收起）
            _advPanel = new Panel
            {
                Left = pad,
                Top = row2Y + S(84),
                Width = innerW,
                Height = S(128),
                BackColor = Theme.Card,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };

            int ap = S(12);
            int aw = _advPanel.Width - ap * 2;
            int hopW = S(296);
            _advPanel.Controls.Add(MakeFieldLabel("连接方式 / 第一跳前置通道", ap, S(8), hopW));
            _advPanel.Controls.Add(MakeFieldLabel("界面别名", ap + hopW + S(12), S(8), aw - hopW - S(12)));

            _cmbHop = new ComboBox
            {
                Left = ap,
                Top = S(30),
                Width = hopW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Theme.Bg,
                ForeColor = Theme.Fg,
                Font = F(12f)
            };
            _cmbHop.Items.Add("链式：自动跟随当前高速节点（推荐）");
            _cmbHop.Items.Add("直连：直接连接该 SOCKS5（不经前置跳板）");
            foreach (var n in _availableNodes)
            {
                if (!string.IsNullOrWhiteSpace(n) && !StaticProxy.IsManagedName(n))
                    _cmbHop.Items.Add("链式锁定：" + n);
            }
            _cmbHop.SelectedIndexChanged += (s, e) => InvalidateVerifyIfEdited();
            _advPanel.Controls.Add(_cmbHop);

            _txtLabel = MakeInputBox(ap + hopW + S(12), S(30), aw - hopW - S(12));
            _txtLabel.MaxLength = 24;
            _advPanel.Controls.Add(_txtLabel);

            _chkIpv4 = new CheckBox
            {
                Left = ap,
                Top = S(68),
                Width = aw,
                Height = S(24),
                ForeColor = Theme.Fg,
                Font = F(11.8f),
                Text = "强制 IPv4 出口（ip-version: ipv4，防 IPv6 泄漏，推荐）",
                Checked = true
            };
            _advPanel.Controls.Add(_chkIpv4);

            _chkUdp = new CheckBox
            {
                Left = ap,
                Top = S(95),
                Width = aw,
                Height = S(24),
                ForeColor = Theme.Dim,
                Font = F(11.5f),
                Text = "允许 SOCKS5 UDP（默认关闭防 QUIC 卡顿；仅服务商明确支持时开启）",
                Checked = false
            };
            _advPanel.Controls.Add(_chkUdp);
            Controls.Add(_advPanel);

            // 步骤 3：直接勾选哪些软件单独使用该独立固定 IP
            _lblStep3 = new Label
            {
                Left = pad,
                Width = S(340),
                Height = S(22),
                ForeColor = Theme.Fg,
                Font = F(13f, FontStyle.Bold),
                Text = "3. 指定单独使用该独立 IP 的软件（可多选）"
            };
            Controls.Add(_lblStep3);

            _chkShowAllApps = new CheckBox
            {
                Left = pad + innerW - S(168),
                Width = S(168),
                Height = S(22),
                ForeColor = Theme.Accent,
                Font = F(11.5f),
                Text = "显示全部软件",
                TextAlign = ContentAlignment.MiddleRight,
                CheckAlign = ContentAlignment.MiddleRight,
                Checked = false
            };
            _chkShowAllApps.CheckedChanged += (s, e) => RebuildAppChecklist();
            Controls.Add(_chkShowAllApps);

            _chkApps = new CheckedListBox
            {
                Left = pad,
                Width = innerW,
                Height = S(118),
                CheckOnClick = true,
                IntegralHeight = false,
                BackColor = Theme.Card,
                ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = F(12.2f)
            };
            _chkApps.ItemCheck += OnAppItemCheck;
            Controls.Add(_chkApps);

            _lblAppSummary = new Label
            {
                Left = pad,
                Width = innerW,
                Height = S(20),
                ForeColor = Theme.Dim,
                Font = F(11.2f)
            };
            Controls.Add(_lblAppSummary);

            // 状态 / 体检结果栏
            _statusBox = new Panel
            {
                Left = pad,
                Width = innerW,
                Height = S(58),
                BackColor = Theme.Card,
                BorderStyle = BorderStyle.FixedSingle
            };
            _lblStatus = new Label
            {
                Left = S(12),
                Top = S(7),
                Width = _statusBox.Width - S(24),
                Height = _statusBox.Height - S(14),
                ForeColor = Theme.Dim,
                Font = F(11.8f),
                Text = "填好参数后可点「测试可用性」做零干扰核验，再点「保存并启用」立即生效。"
            };
            _statusBox.Controls.Add(_lblStatus);
            Controls.Add(_statusBox);

            // 底部按钮行
            _btnTest = MakeFlatButton("测试可用性", pad, 0, S(118), S(36));
            _btnTest.Click += async (s, e) => await RunTestAsync();
            Controls.Add(_btnTest);

            _btnDisable = MakeFlatButton("停用", pad + S(126), 0, S(72), S(36));
            _btnDisable.ForeColor = Theme.Danger;
            _btnDisable.Visible = _initial.Enabled;
            _btnDisable.Click += (s, e) => OnDisableClicked();
            Controls.Add(_btnDisable);

            _btnCancel = MakeFlatButton("取消", w - pad - S(80), 0, S(80), S(36));
            _btnCancel.Click += (s, e) => { Outcome = DialogOutcome.Cancelled; DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);

            _btnSave = new Button
            {
                Left = _btnCancel.Left - S(126) - S(10),
                Width = S(126),
                Height = S(36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                Font = F(12.5f, FontStyle.Bold),
                Text = _initial.Enabled ? "保存并生效" : "保存并启用",
                Cursor = Cursors.Hand
            };
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.Click += (s, e) => OnSaveEnableClicked();
            Controls.Add(_btnSave);

            ClientSize = new Size(w, S(560));
            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }

        Label MakeFieldLabel(string text, int x, int y, int width)
        {
            return new Label
            {
                Left = x,
                Top = y,
                Width = width,
                Height = S(20),
                ForeColor = Theme.Dim,
                Font = F(11.5f),
                Text = text
            };
        }

        TextBox MakeInputBox(int x, int y, int width)
        {
            return new TextBox
            {
                Left = x,
                Top = y,
                Width = width,
                Height = S(27),
                BackColor = Theme.Card,
                ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = F(12.5f)
            };
        }

        Button MakeFlatButton(string text, int x, int y, int width, int height)
        {
            var b = new Button
            {
                Left = x,
                Top = y,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Btn,
                ForeColor = Theme.Fg,
                Font = F(12f),
                Text = text,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Theme.Line;
            b.FlatAppearance.BorderSize = 1;
            return b;
        }

        void PopulateInitialValues()
        {
            _suppressQuickParse = true;
            _txtHost.Text = _initial.Host ?? "";
            _txtPort.Text = _initial.Port > 0 ? _initial.Port.ToString() : "";
            _txtUser.Text = _initial.Username ?? "";
            _txtPass.Text = _initial.Password ?? "";
            _txtLabel.Text = _initial.DisplayLabel;
            _chkIpv4.Checked = _initial.ForceIpv4;
            _chkUdp.Checked = _initial.AllowUdp;

            int hopIdx = 0;
            if (string.Equals(_initial.HopMode, StaticProxy.HopDirect, StringComparison.OrdinalIgnoreCase))
            {
                hopIdx = 1;
            }
            else if (string.Equals(_initial.HopMode, StaticProxy.HopPin, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(_initial.PinnedHopNode))
            {
                string target = "链式锁定：" + _initial.PinnedHopNode;
                int found = _cmbHop.Items.IndexOf(target);
                if (found < 0)
                {
                    _cmbHop.Items.Add(target);
                    found = _cmbHop.Items.Count - 1;
                }
                hopIdx = found;
            }
            if (_cmbHop.Items.Count > 0) _cmbHop.SelectedIndex = hopIdx;

            if (_initial.Enabled && !string.IsNullOrEmpty(_initial.LastVerifiedSummary))
            {
                SetStatusText((_initial.LastVerifiedOk ? "上次体检通过：" : "上次体检提示：") + _initial.LastVerifiedSummary,
                    _initial.LastVerifiedOk ? Theme.Ok : Theme.Warn);
            }
            _suppressQuickParse = false;
        }

        void RebuildAppChecklist()
        {
            _suppressAppCheck = true;
            _chkApps.BeginUpdate();
            _chkApps.Items.Clear();

            bool showAll = _chkShowAllApps.Checked;
            var filtered = _allApps
                .Where(a => showAll || a.Proxy || _checkedStaticExes.Contains(a.Exe))
                .OrderByDescending(a => _checkedStaticExes.Contains(a.Exe))
                .ThenByDescending(a => a.Proxy)
                .ThenByDescending(a => a.Running)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (!showAll && filtered.Count == 0 && _allApps.Count > 0)
            {
                // 若主列表目前还没勾任何代理软件，自动展示全部常用/运行中软件方便直接选
                filtered = _allApps
                    .OrderByDescending(a => a.Running)
                    .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                _chkShowAllApps.Checked = true;
            }

            foreach (var app in filtered)
            {
                bool isChk = _checkedStaticExes.Contains(app.Exe);
                _chkApps.Items.Add(app, isChk);
            }
            _chkApps.EndUpdate();
            _suppressAppCheck = false;
            UpdateAppSummaryText();
        }

        void OnAppItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressAppCheck || e.Index < 0 || e.Index >= _chkApps.Items.Count) return;
            var app = _chkApps.Items[e.Index] as AppChoice;
            if (app == null) return;
            if (e.NewValue == CheckState.Checked) _checkedStaticExes.Add(app.Exe);
            else _checkedStaticExes.Remove(app.Exe);
            BeginInvoke(new Action(UpdateAppSummaryText));
        }

        void UpdateAppSummaryText()
        {
            int n = _checkedStaticExes.Count;
            if (n == 0)
            {
                _lblAppSummary.ForeColor = Theme.Dim;
                _lblAppSummary.Text = "尚未指定软件（启用后也可随时在主界面「1. 分流」软件旁一键点切「高速 / 固定IP」）";
            }
            else
            {
                _lblAppSummary.ForeColor = Theme.Ok;
                _lblAppSummary.Text = "已指定 " + n + " 个软件单独锁定走独立固定 IP（其余代理软件继续走原高速节点，未选软件直连）";
            }
        }

        void UpdateLayoutForAdvanced()
        {
            // 每次展开/收起都先复位软件勾选框基准高度，避免小屏压扁后收起时无法还原
            _chkApps.Height = S(118);
            _advPanel.Visible = _advExpanded;
            int y = _advExpanded ? (_advPanel.Bottom + S(12)) : (_lnkAdv.Bottom + S(12));

            _lblStep3.Top = y;
            _chkShowAllApps.Top = y;
            _chkApps.Top = y + S(26);
            _lblAppSummary.Top = _chkApps.Bottom + S(4);

            _statusBox.Top = _lblAppSummary.Bottom + S(10);
            int btnY = _statusBox.Bottom + S(14);
            _btnTest.Top = btnY;
            _btnDisable.Top = btnY;
            _btnSave.Top = btnY;
            _btnCancel.Top = btnY;

            int wantH = btnY + S(36) + S(18);
            var wa = Screen.FromControl(Owner ?? this).WorkingArea;
            if (wantH > wa.Height - 40)
            {
                int shrink = wantH - (wa.Height - 40);
                int newListH = Math.Max(S(68), _chkApps.Height - shrink);
                int delta = _chkApps.Height - newListH;
                _chkApps.Height = newListH;
                _lblAppSummary.Top -= delta;
                _statusBox.Top -= delta;
                _btnTest.Top -= delta;
                _btnDisable.Top -= delta;
                _btnSave.Top -= delta;
                _btnCancel.Top -= delta;
                wantH -= delta;
            }
            ClientSize = new Size(ClientSize.Width, wantH);
        }

        void InvalidateVerifyIfEdited()
        {
            if (_suppressQuickParse) return;
            if (_lastVerifyOk || !string.IsNullOrEmpty(_lastVerifySummary))
            {
                _lastVerifyOk = false;
                _lastVerifySummary = "";
                _lastVerifyTicks = 0;
                SetStatusText("连接参数已修改，建议点击「测试可用性」重新核验后再启用。", Theme.Dim);
            }
        }

        void OnQuickPasteChanged()
        {
            if (_suppressQuickParse) return;
            string raw = (_txtQuick.Text ?? "").Trim();
            if (raw.Length == 0)
            {
                _lblQuickHint.ForeColor = Theme.Dim;
                _lblQuickHint.Text = "支持 ip:port:user:pass、user:pass@ip:port、socks5://... 或多行键值复制";
                return;
            }
            StaticProxy.ParsedEndpoint ep;
            string err;
            if (StaticProxy.ParseQuickInput(raw, out ep, out err))
            {
                _txtHost.Text = ep.Host;
                _txtPort.Text = ep.Port.ToString();
                _txtUser.Text = ep.Username;
                _txtPass.Text = ep.Password;
                _lblQuickHint.ForeColor = Theme.Ok;
                _lblQuickHint.Text = "已自动识别（" + ep.DetectedFormat + "），参数已填入下方";
            }
            else
            {
                _lblQuickHint.ForeColor = Theme.Warn;
                _lblQuickHint.Text = err;
            }
        }

        bool TryCollectConfig(bool enabled, out StaticProxy.StaticProxyConfig cfg, out string error)
        {
            cfg = null;
            error = "";
            int port;
            if (!int.TryParse((_txtPort.Text ?? "").Trim(), out port))
            {
                error = "请填写有效的端口号（1–65535）";
                return false;
            }
            string hopMode = StaticProxy.HopFollow;
            string pinned = "";
            string selHop = _cmbHop.SelectedItem as string ?? "";
            if (_cmbHop.SelectedIndex == 1 || selHop.StartsWith("直连：", StringComparison.Ordinal))
            {
                hopMode = StaticProxy.HopDirect;
            }
            else if (selHop.StartsWith("链式锁定：", StringComparison.Ordinal))
            {
                hopMode = StaticProxy.HopPin;
                pinned = selHop.Substring("链式锁定：".Length).Trim();
            }

            cfg = new StaticProxy.StaticProxyConfig
            {
                Enabled = enabled,
                Label = (_txtLabel.Text ?? "").Trim(),
                Host = (_txtHost.Text ?? "").Trim(),
                Port = port,
                Username = _txtUser.Text ?? "",
                Password = _txtPass.Text ?? "",
                HopMode = hopMode,
                PinnedHopNode = pinned,
                AllowUdp = _chkUdp.Checked,
                ForceIpv4 = _chkIpv4.Checked,
                FailClosed = true,
                LastVerifiedAtTicks = _lastVerifyTicks,
                LastVerifiedOk = _lastVerifyOk,
                LastVerifiedSummary = _lastVerifySummary
            };
            if (cfg.Label.Length == 0) cfg.Label = "独立固定IP";
            if (!cfg.IsValid(out error)) return false;
            return true;
        }

        void SetStatusText(string text, Color color)
        {
            if (IsDisposed) return;
            _lblStatus.ForeColor = color;
            _lblStatus.Text = text ?? "";
        }

        void SetBusyUi(bool busy)
        {
            _testing = busy;
            _btnTest.Enabled = !busy;
            _btnSave.Enabled = !busy;
            _btnDisable.Enabled = !busy;
            _txtQuick.Enabled = !busy;
            _txtHost.Enabled = !busy;
            _txtPort.Enabled = !busy;
            _txtUser.Enabled = !busy;
            _txtPass.Enabled = !busy;
            _chkApps.Enabled = !busy;
            _btnTest.Text = busy ? "检测中…" : "测试可用性";
        }

        async Task RunTestAsync()
        {
            if (_testing) return;
            StaticProxy.StaticProxyConfig cfg;
            string err;
            if (!TryCollectConfig(true, out cfg, out err))
            {
                SetStatusText("参数未就绪：" + err, Theme.Danger);
                return;
            }
            SetBusyUi(true);
            SetStatusText("正在启动零干扰旁路检测（不打断当前上网）…", Theme.Accent);
            try
            {
                var res = await Task.Run(() => StaticProxy.ProbeThroughSidecar(
                    cfg,
                    _knownConfigPath,
                    msg =>
                    {
                        if (!IsDisposed && IsHandleCreated)
                        {
                            try { BeginInvoke(new Action(() => SetStatusText(msg, Theme.Accent))); }
                            catch { }
                        }
                    }));
                if (IsDisposed) return;
                _lastVerifyTicks = DateTime.UtcNow.Ticks;
                _lastVerifyOk = res.Ok;
                _lastVerifySummary = res.Ok ? res.Summary : res.Error;
                if (res.Ok)
                {
                    string hopHint = res.HopDelayMs > 0 ? ("（第一跳 " + res.HopDelayMs + "ms）") : "（直连模式）";
                    SetStatusText("检测通过：" + res.Summary + hopHint, Theme.Ok);
                }
                else
                {
                    SetStatusText("检测未通过：" + res.Error, Theme.Danger);
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) SetStatusText("检测异常：" + ex.Message, Theme.Danger);
            }
            finally
            {
                if (!IsDisposed) SetBusyUi(false);
            }
        }

        void OnSaveEnableClicked()
        {
            StaticProxy.StaticProxyConfig cfg;
            string err;
            if (!TryCollectConfig(true, out cfg, out err))
            {
                SetStatusText("无法启用：" + err, Theme.Danger);
                return;
            }
            SelectedStaticExes = new HashSet<string>(_checkedStaticExes, StringComparer.OrdinalIgnoreCase);
            ResultConfig = cfg;
            Outcome = DialogOutcome.SavedAndEnabled;
            DialogResult = DialogResult.OK;
            Close();
        }

        void OnDisableClicked()
        {
            StaticProxy.StaticProxyConfig disabledCfg = _initial.Clone();
            disabledCfg.Enabled = false;
            ResultConfig = disabledCfg;

            var boundNames = _allApps.Where(a => a.Proxy && a.IsStatic).Select(a => a.Name).ToList();
            if (boundNames.Count == 0)
            {
                Outcome = DialogOutcome.DisabledToDirect;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            string sample = string.Join("、", boundNames.GetRange(0, Math.Min(3, boundNames.Count)));
            if (boundNames.Count > 3) sample += " 等 " + boundNames.Count + " 个软件";

            var choice = MessageBox.Show(
                this,
                "当前仍有 " + boundNames.Count + " 个软件锁定使用独立固定 IP（" + sample + "）。\n\n" +
                "停用独立固定 IP 后，这些软件要如何处理？\n\n" +
                "• 点「是(Y)」：改为直连（最严防风控推荐，避免暴露到普通共享节点）\n" +
                "• 点「否(N)」：切回普通高速代理\n" +
                "• 点「取消」：暂不停用",
                "停用独立固定 IP",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (choice == DialogResult.Cancel) return;
            Outcome = choice == DialogResult.Yes
                ? DialogOutcome.DisabledToDirect
                : DialogOutcome.DisabledToDefaultProxy;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
