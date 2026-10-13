using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KuroMon.ViewModels;

/// <summary>Short messages shown one at a time at the bottom of the main window.</summary>
public sealed partial class ToastQueue : ObservableObject
{
    private readonly Queue<string> _pending = new();
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isOpen;

    public ToastQueue(TimeSpan duration)
    {
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => Next();
    }

    public void Enqueue(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _pending.Enqueue(message);
        if (!IsOpen) Next();
    }

    private void Next()
    {
        _timer.Stop();
        if (_pending.Count == 0)
        {
            IsOpen = false;
            return;
        }
        Message = _pending.Dequeue();
        IsOpen = true;
        _timer.Start();
    }
}
