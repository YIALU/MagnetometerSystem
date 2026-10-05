using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.Infrastructure.Update;

/// <summary>保留旧调用入口；应用使用 MultiPlatformUpdateService。</summary>
public sealed class GiteeUpdateService : ReleaseUpdateService
{
    public GiteeUpdateService(UpdateOptions options) : base(options, UpdateSource.Gitee) { }
    internal GiteeUpdateService(UpdateOptions options, HttpMessageHandler handler)
        : base(options, UpdateSource.Gitee, handler) { }
}
