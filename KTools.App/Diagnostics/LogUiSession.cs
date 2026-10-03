// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

using KTools_App.Models;

namespace KTools_App.Diagnostics;

public sealed class LogUiSession
{
    private readonly LogUiBuffer _buffer;
    private readonly object _lock = new();
    private long _generation;
    private bool _isActive;
    private bool _timerStartPending;

    public LogUiSession(
        int capacity = LogUiBuffer.DefaultCapacity,
        long maxBytes = LogUiBuffer.DefaultMaxBytes,
        int seenLimit = 8192)
    {
        _buffer = new LogUiBuffer(capacity, maxBytes, seenLimit);
    }

    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _isActive;
            }
        }
    }

    public long Activate()
    {
        lock (_lock)
        {
            _generation = _generation == long.MaxValue ? 1 : _generation + 1;
            _isActive = true;
            _timerStartPending = false;
            _buffer.Reset();
            return _generation;
        }
    }

    public void Deactivate(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }

            _isActive = false;
            _timerStartPending = false;
            _buffer.Reset();
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_lock)
        {
            return _isActive && generation == _generation;
        }
    }

    public bool TryAdd(long generation, LogEvent logEvent)
    {
        if (logEvent is null)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_isActive || generation != _generation)
            {
                return false;
            }

            _buffer.Add(logEvent);
            return true;
        }
    }

    public bool RegisterSnapshot(long generation, IEnumerable<LogEvent> events)
    {
        if (events is null)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_isActive || generation != _generation)
            {
                return false;
            }

            try
            {
                _buffer.RegisterSnapshot(events);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public int PurgeSnapshotDuplicates(long generation, IEnumerable<string> correlationKeys)
    {
        if (correlationKeys is null)
        {
            return 0;
        }

        lock (_lock)
        {
            if (!_isActive || generation != _generation)
            {
                return 0;
            }

            try
            {
                return _buffer.PurgeSnapshotDuplicates(correlationKeys);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public bool TryBeginBatch(long generation)
    {
        lock (_lock)
        {
            if (!_isActive || generation != _generation)
            {
                return false;
            }

            if (_timerStartPending)
            {
                return false;
            }

            _timerStartPending = true;
            return true;
        }
    }

    public void EndBatch(long generation)
    {
        lock (_lock)
        {
            if (generation == _generation)
            {
                _timerStartPending = false;
            }
        }
    }

    public bool TryDrain(
        long generation,
        out IReadOnlyList<LogItem> items,
        out long dropped)
    {
        lock (_lock)
        {
            if (!_isActive || generation != _generation)
            {
                items = Array.Empty<LogItem>();
                dropped = 0;
                return false;
            }

            items = _buffer.Drain(out dropped);
            _timerStartPending = false;
            return true;
        }
    }
}
