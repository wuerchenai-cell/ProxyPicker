using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ProxyPicker.App.ViewModels;

namespace ProxyPicker.App.Views
{
    /// <summary>
    /// 主工作台壳窗口（严格保持极薄 View 代码，零任何底层内核/规则逻辑）。
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        bool _allowRealClose;

        public MainWindow(ShellViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // 用 IntEqualsToBoolConverter 输出 bool 映射页面可见性改为显式绑定同步，确保 100% 可靠
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.TrafficSamplePushed += (p, s, d) => PageApps.PushTrafficPoint(p, s, d);
            viewModel.ToastNotification += (t, b, tone) =>
            {
                _toastTimer.Stop();
                _toastTimer.Start();
            };

            _toastTimer.Tick += (_, __) =>
            {
                _toastTimer.Stop();
                if (DataContext is ShellViewModel vm) vm.ToastVisible = false;
            };

            SyncPageVisibility(viewModel.SelectedNavIndex);
        }

        void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.SelectedNavIndex) && sender is ShellViewModel vm)
            {
                SyncPageVisibility(vm.SelectedNavIndex);
                if (vm.SelectedNavIndex == 5) vm.LoadAuditLogs();
            }
        }

        public void SyncPageVisibility(int idx)
        {
            PageApps.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageNodes.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageConns.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageDiag.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageAuto.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = idx == 5 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void AllowRealClose() => _allowRealClose = true;

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowRealClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }

        void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void OnMaximizeClick(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        void OnHideToTrayClick(object sender, RoutedEventArgs e) => Hide();

        void OnDismissToastClick(object sender, RoutedEventArgs e)
        {
            _toastTimer.Stop();
            if (DataContext is ShellViewModel vm) vm.ToastVisible = false;
        }
    }
}
