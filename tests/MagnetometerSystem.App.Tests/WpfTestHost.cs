using System.Windows;
using System.Windows.Threading;

namespace MagnetometerSystem.App.Tests;

/// <summary>所有 WPF 测试共用一个 STA Dispatcher；不运行生产 App 启动流程。</summary>
public static class WpfTestHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<Task<Dispatcher>> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/MagnetometerSystem.App;component/Themes/Workspace.xaml", UriKind.Relative),
                });
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "WPF tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    });

    public static async Task RunAsync(Func<Task> action)
    {
        await Gate.WaitAsync();
        try
        {
            var dispatcher = await Ui.Value.WaitAsync(TimeSpan.FromSeconds(15));
            await dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally { Gate.Release(); }
    }

    public static async Task PumpAsync() => await Dispatcher.Yield(DispatcherPriority.Background);
}
