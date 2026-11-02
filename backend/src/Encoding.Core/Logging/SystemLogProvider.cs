using System.Collections.Concurrent;
using Encoding.Contracts.Logging;
using Microsoft.Extensions.Logging;

namespace Encoding.Core.Logging;

public sealed class SystemLogOptions
{
    public bool AllowRoutineDiagnostics { get; set; }
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

public sealed class SystemLogProvider : ILoggerProvider
{
    private readonly SystemLogRingBuffer _ringBuffer;
    private readonly SystemLogStreamBroadcaster _broadcaster;
    private readonly Func<ISystemLogNodeContext?>? _nodeContextAccessor;
    private readonly Func<ISystemLogSink?>? _sinkAccessor;
    private readonly SystemLogOptions _options;
    private readonly ConcurrentDictionary<string, SystemLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public SystemLogProvider(
        SystemLogRingBuffer ringBuffer,
        SystemLogStreamBroadcaster broadcaster,
        ISystemLogNodeContext? nodeContext = null,
        ISystemLogSink? sink = null,
        SystemLogOptions? options = null)
        : this(ringBuffer, broadcaster, () => nodeContext, () => sink, options)
    {
    }

    public SystemLogProvider(
        SystemLogRingBuffer ringBuffer,
        SystemLogStreamBroadcaster broadcaster,
        Func<ISystemLogNodeContext?>? nodeContextAccessor,
        Func<ISystemLogSink?>? sinkAccessor,
        SystemLogOptions? options = null)
    {
        _ringBuffer = ringBuffer ?? throw new ArgumentNullException(nameof(ringBuffer));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _nodeContextAccessor = nodeContextAccessor;
        _sinkAccessor = sinkAccessor;
        _options = options ?? new SystemLogOptions();
    }

    public SystemLogRingBuffer RingBuffer => _ringBuffer;
    public SystemLogStreamBroadcaster Broadcaster => _broadcaster;

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, category => new SystemLogger(
            category,
            _ringBuffer,
            _broadcaster,
            _nodeContextAccessor,
            _sinkAccessor,
            _options));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

public sealed class SystemLogger : ILogger
{
    [ThreadStatic]
    private static bool _isLogging;

    private readonly string _category;
    private readonly SystemLogRingBuffer _ringBuffer;
    private readonly SystemLogStreamBroadcaster _broadcaster;
    private readonly Func<ISystemLogNodeContext?>? _nodeContextAccessor;
    private readonly Func<ISystemLogSink?>? _sinkAccessor;
    private readonly SystemLogOptions _options;

    public SystemLogger(
        string category,
        SystemLogRingBuffer ringBuffer,
        SystemLogStreamBroadcaster broadcaster,
        ISystemLogNodeContext? nodeContext,
        ISystemLogSink? sink,
        SystemLogOptions options)
        : this(category, ringBuffer, broadcaster, () => nodeContext, () => sink, options)
    {
    }

    public SystemLogger(
        string category,
        SystemLogRingBuffer ringBuffer,
        SystemLogStreamBroadcaster broadcaster,
        Func<ISystemLogNodeContext?>? nodeContextAccessor,
        Func<ISystemLogSink?>? sinkAccessor,
        SystemLogOptions options)
    {
        _category = category;
        _ringBuffer = ringBuffer;
        _broadcaster = broadcaster;
        _nodeContextAccessor = nodeContextAccessor;
        _sinkAccessor = sinkAccessor;
        _options = options;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
            return false;

        // On autorise à être capturé si >= MinimumLevel ou si Warning/Error
        return logLevel >= _options.MinimumLevel || logLevel >= LogLevel.Warning;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        if (_isLogging)
            return; // Anti-réentrance

        _isLogging = true;
        try
        {
            var rawMessage = formatter(state, exception);
            if (!SystemLogFilterPolicy.ShouldCapture(_category, logLevel, rawMessage, _options.AllowRoutineDiagnostics))
                return;

            var sanitizedMessage = SystemLogSanitizer.Sanitize(rawMessage);
            var sanitizedException = SystemLogSanitizer.SanitizeException(exception);
            var systemLevel = MapLogLevel(logLevel);

            var nodeContext = _nodeContextAccessor?.Invoke();
            var entry = new SystemLogEntry(
                Id: Guid.NewGuid().ToString("N"),
                Timestamp: DateTimeOffset.UtcNow,
                Level: systemLevel,
                Category: _category,
                Message: sanitizedMessage,
                Exception: sanitizedException,
                NodeId: nodeContext?.NodeId,
                NodeName: nodeContext?.NodeName,
                EventId: eventId.Id != 0 ? eventId.Id : null);

            _ringBuffer.Add(entry);
            _broadcaster.Publish(entry);

            if (logLevel >= LogLevel.Warning)
            {
                var sink = _sinkAccessor?.Invoke();
                if (sink != null) _ = sink.EmitAsync(entry);
            }
        }
        finally
        {
            _isLogging = false;
        }
    }

    private static SystemLogLevel MapLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => SystemLogLevel.Trace,
        LogLevel.Debug => SystemLogLevel.Debug,
        LogLevel.Information => SystemLogLevel.Information,
        LogLevel.Warning => SystemLogLevel.Warning,
        LogLevel.Error => SystemLogLevel.Error,
        LogLevel.Critical => SystemLogLevel.Critical,
        _ => SystemLogLevel.Information
    };

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
