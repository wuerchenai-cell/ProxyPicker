using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ProxyPicker.App.ViewModels
{
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(name);
            return true;
        }

        protected void RaisePropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class RelayCommand : ICommand
    {
        readonly Action<object> _execute;
        readonly Func<object, bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
        public void Execute(object parameter) => _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public static void InvalidateAll() => CommandManager.InvalidateRequerySuggested();
    }

    public sealed class AsyncRelayCommand : ICommand
    {
        readonly Func<object, Task> _execute;
        readonly Func<object, bool> _canExecute;
        bool _busy;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public AsyncRelayCommand(Func<object, Task> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => !_busy && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            _busy = true;
            CommandManager.InvalidateRequerySuggested();
            try { await _execute(parameter); }
            catch { }
            finally
            {
                _busy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
