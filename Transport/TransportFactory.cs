using WGL2Bridge.Config;

namespace WGL2Bridge.Transport;

/// <summary>Selects the transport implementation for the configured encapsulation mode.</summary>
public static class TransportFactory
{
    /// <summary>Creates a transport instance from configuration.</summary>
    public static IBridgeTransport Create(BridgeConfig config) => config.TransportMode switch
    {
        TransportMode.Raw => new RawTransport(config),
        TransportMode.Vxlan => new VxlanTransport(config),
        TransportMode.GreTap => new GreTapTransport(config),
        _ => throw new ArgumentOutOfRangeException(nameof(config), "Unknown transport mode."),
    };
}
