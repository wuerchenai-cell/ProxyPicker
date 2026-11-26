using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProxyPicker.App.ViewModels;

namespace ProxyPicker.App.Views.Pages
{
    public partial class SettingsAuditView : UserControl
    {
        public SettingsAuditView()
        {
            InitializeComponent();
        }

        void OnExportBundleClick(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is ShellViewModel vm)) return;
            string pass = PromptPassphrase("加密导出配置迁移包", "请输入导出加密口令（留空则不导出固定 IP 凭据）：");
            if (pass == null) return;
            var dlg = new SaveFileDialog
            {
                Title = "保存 ProxyPicker v3.0 加密配置迁移包",
                Filter = "ProxyPicker 配置包 (*.ppbundle.json)|*.ppbundle.json|JSON 文件 (*.json)|*.json",
                FileName = "ProxyPicker-backup-" + DateTime.Now.ToString("yyyyMMdd") + ".ppbundle.json"
            };
            if (dlg.ShowDialog() == true)
            {
                string json = vm.ExportConfigBundle(pass, !string.IsNullOrEmpty(pass));
                File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
                vm.ShowToast("配置包导出成功", Path.GetFileName(dlg.FileName), "ok");
            }
        }

        async void OnImportBundleClick(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is ShellViewModel vm)) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择 ProxyPicker 配置迁移包",
                Filter = "ProxyPicker 配置包 (*.ppbundle.json;*.json)|*.ppbundle.json;*.json"
            };
            if (dlg.ShowDialog() != true) return;
            string pass = PromptPassphrase("导入配置迁移包", "如导出时加密了固定 IP 凭据，请输入解密口令（否则留空）：");
            if (pass == null) return;
            string json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
            await vm.ImportConfigBundleAsync(json, pass);
        }

        static string PromptPassphrase(string title, string prompt)
        {
            var win = new Window
            {
                Title = title,
                Width = 420,
                Height = 185,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = System.Windows.Application.Current?.MainWindow
            };
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var pb = new PasswordBox { Height = 30, Padding = new Thickness(6, 4, 6, 4) };
            root.Children.Add(pb);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            string result = null;
            var okBtn = new Button { Content = "确定", Width = 78, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 78, Height = 30, IsCancel = true };
            okBtn.Click += (_, __) => { result = pb.Password ?? ""; win.DialogResult = true; };
            btns.Children.Add(okBtn);
            btns.Children.Add(cancelBtn);
            root.Children.Add(btns);
            win.Content = root;
            return win.ShowDialog() == true ? result : null;
        }
    }
}
