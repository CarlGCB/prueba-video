using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VideoCutter.Core;

namespace VideoCutter.Desktop;

public class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true;
    }
}

public enum QState { Waiting, Working, Error }

/// <summary>Un vídeo que espera (o está) convirtiéndose a MP4.</summary>
public class QueueItem : Bindable
{
    string _status = "En cola";
    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);
    public QState State { get; set; } = QState.Waiting;
    public string Status { get => _status; set => Set(ref _status, value); }
    public QueueItem(string path) { FilePath = path; }
}

/// <summary>Un MP4 listo para pasar al editor.</summary>
public class Mp4Item
{
    public MediaInfo Info { get; }
    public string Name => System.IO.Path.GetFileName(Info.Path);
    public Mp4Item(MediaInfo info) { Info = info; }
}

/// <summary>Evita que Windows se suspenda mientras se exporta.</summary>
public sealed class SleepGuard : IDisposable
{
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
    public SleepGuard() { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); }
    public void Dispose() { SetThreadExecutionState(ES_CONTINUOUS); }
}
