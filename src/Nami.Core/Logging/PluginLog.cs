using Nami.Sdk;

namespace Nami.Core.Logging;

/// <summary>An <see cref="ILog"/> scoped to a single plugin; records carry the plugin id as source.</summary>
public sealed class PluginLog(string pluginId, LogHub hub) : ILog
{
    public void Log(LogLevel level, string message) => hub.Log(pluginId, level, message);
}

/// <summary>An <see cref="ILog"/> that routes to a hub with a fixed source tag.</summary>
public sealed class TaggedLog(string source, LogHub hub) : ILog
{
    public void Log(LogLevel level, string message) => hub.Log(source, level, message);
}
