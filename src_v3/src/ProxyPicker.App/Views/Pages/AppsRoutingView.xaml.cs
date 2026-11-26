using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProxyPicker.App.ViewModels;

namespace ProxyPicker.App.Views.Pages
{
    public partial class AppsRoutingView : UserControl
    {
        public AppsRoutingView()
        {
            InitializeComponent();
        }

        public void PushTrafficPoint(double proxyBps, double staticBps, double directBps)
        {
            TrafficSparkline?.PushSample(proxyBps, staticBps, directBps);
        }

        async void OnAddCustomAppClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要加入分流列表的应用程序",
                Filter = "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true && DataContext is ShellViewModel vm)
                await vm.AddCustomAppByPathAsync(dlg.FileName);
        }
    }
}
