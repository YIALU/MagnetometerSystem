using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// 连接工厂，根据配置创建对应的连接实例
/// </summary>
public interface IConnectionFactory
{
    IDeviceConnection Create(ConnectionConfig config);
}
