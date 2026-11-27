// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using KTools_App.Diagnostics;

namespace KTools_App.Core;

/// <summary>
/// Хаб подписчиков журнала (ишью 140): выделен из LogService God-object.
/// Владеет списком приёмников, bounded-очередями доставки и насосами;
/// файловый канал и очередь записи его не касаются. Ошибки доставки
/// сообщает наружу через колбэки, счётчики остаются в LogService.
/// </summary>
internal sealed class LogSubscriberHub
{
    private readonly object _gate = new();
    private readonly List<SubscriberSink> _sinks = new();
    private readonly object _sender;
    private readonly Action _reportError;
    private readonly Action _reportDropped;

    public LogSubscriberHub(object sender, Action reportError, Action reportDropped)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _reportDropped = reportDropped ?? throw new ArgumentNullException(nameof(reportDropped));
    }

    public void Add(EventHandler<LogEvent> handler, int capacity, long maxBytes)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                _sinks.Add(new SubscriberSink(_sender, handler, capacity, maxBytes, _reportError, _reportDropped));
            }
        }
        catch (Exception)
        {
        }
    }

    public void Remove(EventHandler<LogEvent> handler)
    {
        if (handler is null)
        {
            return;
        }

        SubscriberSink? sink = null;
        try
        {
            lock (_gate)
            {
                int index = _sinks.FindIndex(candidate => candidate.Matches(handler));
                if (index >= 0)
                {
                    sink = _sinks[index];
                    _sinks.RemoveAt(index);
                }
            }
        }
        catch (Exception)
        {
        }

        sink?.Complete();
    }

    /// <summary>
    /// Доставка подписчикам не бросает исключений: сбой одного подписчика не должен
    /// прерывать ни сериализацию, ни файловую запись остальных событий батча.
    /// </summary>
    public void Publish(LogEvent logEvent, int estimatedBytes)
    {
        try
        {
            SubscriberSink[] sinks;
            lock (_gate)
            {
                if (_sinks.Count == 0)
                {
                    return;
                }

                sinks = _sinks.ToArray();
            }

            LogEvent published = FreezeForSubscribers(logEvent);
            foreach (SubscriberSink sink in sinks)
            {
                sink.TryEnqueue(new SubscriberItem(published, estimatedBytes));
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Завершает приёмники без ожидания насосов (вызов с потока писателя:
    /// ожидание здесь заблокировало бы файловый канал).
    /// </summary>
    public void CompleteWithoutWait()
    {
        try
        {
            lock (_gate)
            {
                foreach (SubscriberSink sink in _sinks)
                {
                    sink.Complete();
                }

                _sinks.Clear();
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Завершает приёмники и ждёт насосов в пределах единого лимита.
    /// Ошибку ожидания сообщает через колбэк, чтобы LogService учёл DisposeError.
    /// </summary>
    public void CompleteAndWait(TimeSpan timeout, Action onTimeout, Action<Exception> onError)
    {
        SubscriberSink[] sinks;
        try
        {
            lock (_gate)
            {
                sinks = _sinks.ToArray();
                _sinks.Clear();
            }
        }
        catch (Exception ex)
        {
            onError(ex);
            return;
        }

        List<Task> pumps = new(sinks.Length);
        foreach (SubscriberSink sink in sinks)
        {
            sink.Complete();
            pumps.Add(sink.Pump);
        }

        if (pumps.Count == 0)
        {
            return;
        }

        try
        {
            if (!Task.WaitAll(pumps.ToArray(), timeout))
            {
                onTimeout();
            }
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    /// <summary>
    /// Подписчики получают один и тот же неизменяемый экземпляр события:
    /// свойства после redaction уже иммутабельны, поэтому копировать словарь
    /// на каждого подписчика не требуется.
    /// </summary>
    private static LogEvent FreezeForSubscribers(LogEvent logEvent) =>
        logEvent.Properties is LogPropertyCollection
            ? logEvent
            : logEvent with { Properties = LogPropertyCollection.Freeze(logEvent.Properties) };

    private readonly struct SubscriberItem
    {
        public SubscriberItem(LogEvent logEvent, int estimatedBytes)
        {
            LogEvent = logEvent;
            EstimatedBytes = estimatedBytes;
        }

        public LogEvent LogEvent { get; }

        public int EstimatedBytes { get; }
    }

    private sealed class SubscriberSink : IDisposable
    {
        private readonly object _sender;
        private readonly EventHandler<LogEvent> _handler;
        private readonly Channel<SubscriberItem> _channel;
        private readonly long _maxBytes;
        private readonly Action _reportError;
        private readonly Action _reportDropped;
        private long _queuedBytes;
        private int _disposed;

        public SubscriberSink(
            object sender,
            EventHandler<LogEvent> handler,
            int capacity,
            long maxBytes,
            Action reportError,
            Action reportDropped)
        {
            _sender = sender;
            _handler = handler;
            _maxBytes = Math.Max(64 * 1024, maxBytes);
            _reportError = reportError;
            _reportDropped = reportDropped;
            _channel = Channel.CreateBounded<SubscriberItem>(new BoundedChannelOptions(Math.Max(16, capacity))
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            Pump = Task.Factory.StartNew(
                Run,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public Task Pump { get; }

        public bool Matches(EventHandler<LogEvent> handler) => _handler.Equals(handler);

        public bool TryEnqueue(SubscriberItem item)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            int estimate = item.EstimatedBytes;
            if (!Reserve(estimate))
            {
                _reportDropped();
                return false;
            }

            try
            {
                if (_channel.Writer.TryWrite(item))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }

            Release(estimate);
            _reportDropped();
            return false;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _channel.Writer.TryComplete();
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            Complete();
        }

        private bool Reserve(int estimate)
        {
            long current = Interlocked.Read(ref _queuedBytes);
            while (true)
            {
                long next = current + estimate;
                if (next > _maxBytes)
                {
                    return false;
                }

                long actual = Interlocked.CompareExchange(ref _queuedBytes, next, current);
                if (actual == current)
                {
                    return true;
                }

                current = actual;
            }
        }

        private void Release(int estimate)
        {
            if (estimate > 0)
            {
                Interlocked.Add(ref _queuedBytes, -estimate);
            }
        }

        private void Run()
        {
            try
            {
                ChannelReader<SubscriberItem> reader = _channel.Reader;
                while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (reader.TryRead(out SubscriberItem item))
                    {
                        Release(item.EstimatedBytes);
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            continue;
                        }

                        try
                        {
                            _handler(_sender, item.LogEvent);
                        }
                        catch (Exception)
                        {
                            _reportError();
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
