using System;
using System.Windows;
using System.Windows.Input;
using ProxyPicker.App.ViewModels;

namespace ProxyPicker.App.Views
{
    public partial class CompactOverlayWindow : Window
    {
        public event Action ExpandRequested;

        public CompactOverlayWindow(ShellViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            Left = SystemParameters.WorkArea.Right - Width - 20;
            Top = SystemParameters.WorkArea.Bottom - Height - 20;
        }

        void OnDragMove(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        void OnExpandClick(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();
    }
}
