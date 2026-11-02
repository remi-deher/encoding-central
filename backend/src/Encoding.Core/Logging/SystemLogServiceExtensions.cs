using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Encoding.Core.Logging;

public static class SystemLogServiceExtensions
{
    public static IServiceCollection AddSystemLogging(
        this IServiceCollection services,
        Action<SystemLogOptions>? configure = null)
    {
        services.AddSingleton<SystemLogRingBuffer>();
        services.AddSingleton<SystemLogStreamBroadcaster>();
        services.AddSingleton(sp =>
        {
            var options = new SystemLogOptions();
            configure?.Invoke(options);
            var ringBuffer = sp.GetRequiredService<SystemLogRingBuffer>();
            var broadcaster = sp.GetRequiredService<SystemLogStreamBroadcaster>();
            return new SystemLogProvider(
                ringBuffer,
                broadcaster,
                () => sp.GetService<ISystemLogNodeContext>(),
                () => sp.GetService<ISystemLogSink>(),
                options);
        });
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<SystemLogProvider>());
        return services;
    }
}
