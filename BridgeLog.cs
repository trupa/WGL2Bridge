using Microsoft.Extensions.Logging;

namespace WGL2Bridge;

/// <summary>
/// Level-aware logging facade used throughout the bridge. Wraps the configured
/// Microsoft.Extensions.Logging sinks so call sites express intent (Info/Debug/Warning/Error)
/// instead of encoding the level in the message text.
/// </summary>
public sealed class BridgeLog(ILogger logger)
{
    public void Info(string message) => logger.LogInformation("{Message}", message);

    public void Debug(string message) => logger.LogDebug("{Message}", message);

    public void Warning(string message) => logger.LogWarning("{Message}", message);

    public void Error(string message) => logger.LogError("{Message}", message);
}
