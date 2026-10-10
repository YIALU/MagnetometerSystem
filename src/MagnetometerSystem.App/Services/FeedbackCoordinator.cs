using System.Reflection;
using System.Windows;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Feedback;
using MagnetometerSystem.Infrastructure.Feedback;

namespace MagnetometerSystem.App.Services;

public sealed class FeedbackCoordinator(IFeedbackClient client, FeedbackDraftStore drafts, IFeedbackLogSource logs)
{
    public static Uri Endpoint
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("MAGNETOMETER_FEEDBACK_ENDPOINT")
                ?? typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "FeedbackEndpoint")?.Value;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                ? uri : new Uri("https://localhost/api/feedback");
        }
    }
    public void Show(Window owner)
    {
        if (Endpoint.Host == "localhost")
        {
            MessageBox.Show(owner, "当前构建未配置反馈服务，请联系项目维护者。", "反馈与建议"); return;
        }
        var existing = Application.Current.Windows.OfType<FeedbackDialog>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new FeedbackDialog(new FeedbackViewModel(client, drafts, logs)) { Owner = owner }.Show();
    }
}
