// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text;

using KTools_App.Core;
using KTools_App.Models;

namespace KTools_App.Diagnostics;

/// <summary>
/// Ограниченный буфер UI-канала журнала. Держит не более capacity элементов и
/// maxBytes байт, дедуплицирует по (SessionId, Sequence) и отдаёт пачками.
/// Все структуры хранения переиспользуются: добавление события не создаёт
/// словарь на элемент и не форматирует ключ корреляции заново.
/// </summary>
public sealed class LogUiBuffer
{
    public const int DefaultCapacity = 4096;
    public const int DefaultMaxBytes = 4 * 1024 * 1024;

    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly List<LogItem> _items = new();
    private readonly List<long> _itemBytes = new();
    private readonly HashSet<LogItemKey> _seen = new();
    private readonly Queue<LogItemKey> _seenOrder = new();
    private readonly object _lock = new();
    private readonly long _maxItemBytes;
    private readonly int _seenLimit;

    private long _bytes;
    private long _dropped;

    public LogUiBuffer(int capacity = DefaultCapacity, long maxBytes = DefaultMaxBytes, int seenLimit = 8192)
    {
        _capacity = Math.Max(16, capacity);
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _maxItemBytes = Math.Max(64 * 1024, Math.Min(_maxBytes, 4L * 1024 * 1024));
        _seenLimit = Math.Max(64, seenLimit);
    }

    public int Capacity => _capacity;

    public long MaxBytes => _maxBytes;

    public long MaxItemBytes => _maxItemBytes;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public long DroppedCount
    {
        get
        {
            lock (_lock)
            {
                return _dropped;
            }
        }
    }

    public bool Add(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return false;
        }

        long estimate = EstimateSize(logEvent);
        lock (_lock)
        {
            LogItemKey key;
            try
            {
                key = new LogItemKey(logEvent.SessionId, logEvent.Sequence);
                if (!_seen.Add(key))
                {
                    return false;
                }

                RememberSeen(key);
            }
            catch (Exception)
            {
                _dropped++;
                return false;
            }

            if (estimate > _maxItemBytes)
            {
                _dropped++;
                return false;
            }

            try
            {
                LogItem item = LogItem.FromEvent(logEvent);
                long itemSize = Math.Max(estimate, EstimateSize(item.Message) + 128L);
                if (itemSize > _maxItemBytes)
                {
                    _dropped++;
                    return false;
                }

                _items.Add(item);
                _itemBytes.Add(itemSize);
                _bytes += itemSize;
                TrimLocked();
                return true;
            }
            catch (Exception)
            {
                _dropped++;
                return false;
            }
        }
    }

    public void RegisterSnapshot(IEnumerable<LogEvent> events)
    {
        if (events is null)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                foreach (LogEvent logEvent in events)
                {
                    if (logEvent is null)
                    {
                        continue;
                    }

                    LogItemKey key = new(logEvent.SessionId, logEvent.Sequence);
                    if (_seen.Add(key))
                    {
                        RememberSeen(key);
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }

    public int PurgeSnapshotDuplicates(IEnumerable<string> correlationKeys)
    {
        if (correlationKeys is null)
        {
            return 0;
        }

        int purged = 0;
        lock (_lock)
        {
            try
            {
                foreach (string key in correlationKeys)
                {
                    if (!TryParseKey(key, out LogItemKey target))
                    {
                        continue;
                    }

                    int index = IndexOfKey(target);
                    if (index < 0)
                    {
                        continue;
                    }

                    _bytes -= _itemBytes[index];
                    _items.RemoveAt(index);
                    _itemBytes.RemoveAt(index);
                    purged++;
                }
            }
            catch (Exception)
            {
            }
        }

        return purged;
    }

    public IReadOnlyList<LogItem> Drain(out long dropped)
    {
        lock (_lock)
        {
            List<LogItem> batch = new(_items.Count);
            if (_items.Count > 0)
            {
                batch.AddRange(_items);
                _items.Clear();
                _itemBytes.Clear();
                _bytes = 0;
            }

            dropped = _dropped;
            _dropped = 0;
            return batch;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _items.Clear();
            _itemBytes.Clear();
            _seen.Clear();
            _seenOrder.Clear();
            _bytes = 0;
            _dropped = 0;
        }
    }

    public static long EstimateSize(string? value) =>
        value is null ? 64 : (long)Encoding.UTF8.GetByteCount(value) + 64;

    public static long EstimateSize(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return 0;
        }

        try
        {
            long estimate = LogService.EstimateEventBytes(logEvent);
            estimate += EstimateSize(logEvent.Message);
            estimate += EstimateSize(logEvent.EventId);
            estimate += EstimateSize(logEvent.Source);
            return estimate;
        }
        catch (Exception)
        {
            return long.MaxValue;
        }
    }

    private void TrimLocked()
    {
        while (_items.Count > _capacity || (_bytes > _maxBytes && _items.Count > 1))
        {
            _bytes -= _itemBytes[0];
            _items.RemoveAt(0);
            _itemBytes.RemoveAt(0);
            if (_bytes < 0)
            {
                _bytes = 0;
            }

            _dropped++;
        }
    }

    private void RememberSeen(LogItemKey key)
    {
        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > _seenLimit)
        {
            _seen.Remove(_seenOrder.Dequeue());
        }
    }

    private int IndexOfKey(LogItemKey key)
    {
        for (int index = 0; index < _items.Count; index++)
        {
            LogEvent? item = _items[index].Event;
            if (item is not null && item.SessionId == key.SessionId && item.Sequence == key.Sequence)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryParseKey(string? correlationKey, out LogItemKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(correlationKey))
        {
            return false;
        }

        int separator = correlationKey.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == correlationKey.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParseExact(correlationKey[..separator], "N", out Guid session))
        {
            return false;
        }

        if (!long.TryParse(
                correlationKey.AsSpan(separator + 1),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long sequence))
        {
            return false;
        }

        key = new LogItemKey(session, sequence);
        return true;
    }

    private readonly struct LogItemKey : IEquatable<LogItemKey>
    {
        private readonly Guid _sessionId;
        private readonly long _sequence;

        public LogItemKey(Guid sessionId, long sequence)
        {
            _sessionId = sessionId;
            _sequence = sequence;
        }

        public Guid SessionId => _sessionId;

        public long Sequence => _sequence;

        public bool Equals(LogItemKey other) =>
            _sequence == other._sequence && _sessionId.Equals(other._sessionId);

        public override bool Equals(object? obj) => obj is LogItemKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_sessionId, _sequence);
    }
}
