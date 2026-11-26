using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProxyPicker.App.ViewModels;

namespace ProxyPicker.App.Views.Pages
{
    public partial class AutomationView : UserControl
    {
        public AutomationView()
        {
            InitializeComponent();
        }

        void OnPickFollowAppClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要跟随的主软件 (.exe)",
                Filter = "应用程序 (*.exe)|*.exe"
            };
            if (dlg.ShowDialog() == true && DataContext is ShellViewModel vm)
                vm.SetFollowTargetFromPath(dlg.FileName);
        }

        void OnAddLaunchAppClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "添加随 ProxyPicker 自动启动与同步关闭的软件 (.exe)",
                Filter = "应用程序 (*.exe)|*.exe"
            };
            if (dlg.ShowDialog() == true && DataContext is ShellViewModel vm)
                vm.AddLaunchAppFromPath(dlg.FileName);
        }
    }
}
