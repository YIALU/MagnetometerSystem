using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var services = new ServiceCollection();

        // 注册服务
        services.AddSingleton<IConnectionFactory, ConnectionFactory>();
        services.AddSingleton<DataBus>();

        // 注册 ViewModels
        services.AddTransient<ConnectionViewModel>();
        services.AddTransient<RealtimeChartViewModel>();
        services.AddTransient<MainViewModel>();

        Services = services.BuildServiceProvider();

        var mainWindow = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainViewModel>()
        };
        mainWindow.Show();
    }
}
