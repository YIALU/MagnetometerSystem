using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MagnetometerSystem.App.Helpers;

/// <summary>
/// 可整体替换内容的 ObservableCollection：一次 Reset 通知代替逐项增删，
/// 用于高频刷新的日志列表，避免每次刷新产生数百个集合通知堵塞界面线程。
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
