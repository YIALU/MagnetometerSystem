using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// 连接工厂实现
/// </summary>
public class ConnectionFactory : IConnectionFactory
{
    public IDeviceConnection Create(ConnectionConfig config)
    {
        return config.Type switch
        {
            ConnectionType.Serial => new SerialDeviceConnection(config),
            ConnectionType.Tcp => new TcpDeviceConnection(config),
            _ => throw new ArgumentException($"不支持的连接类型: {config.Type}")
        };
    }
}
