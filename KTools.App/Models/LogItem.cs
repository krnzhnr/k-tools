// -*- coding: utf-8 -*-
using System;

using KTools_App.Core;
using KTools_App.Diagnostics;

namespace KTools_App.Models;

public enum LogItemKind
{
    Event = 0,
    Marker = 1
}

public sealed class LogItem
{
    public string Message { get; set; } = string.Empty;

    public LogLevel Level { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public long Sequence { get; set; }

    public Guid SessionId { get; set; }

    public string EventId { get; set; } = string.Empty;

    public LogStatus Status { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? OperationId { get; set; }

    public string? ItemId { get; set; }

    public LogEvent? Event { get; set; }

    public LogItemKind Kind { get; set; } = LogItemKind.Event;

    public bool HasStructuredEvent => Event is not null;

    public static LogItem FromEvent(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        return new LogItem
        {
            Message = logEvent.ToDisplayString(),
            Level = logEvent.Level,
            TimestampUtc = logEvent.TimestampUtc,
            Sequence = logEvent.Sequence,
            SessionId = logEvent.SessionId,
            EventId = logEvent.EventId,
            Status = logEvent.Status,
            Source = logEvent.Source,
            OperationId = logEvent.OperationId,
            ItemId = logEvent.ItemId,
            Event = logEvent,
            Kind = LogItemKind.Event
        };
    }

    public static LogItem CreateMarker(string message, LogLevel level, long droppedCount, Guid sessionId, string eventId)
    {
        return new LogItem
        {
            Message = message,
            Level = level,
            Sequence = 0,
            SessionId = sessionId,
            EventId = eventId,
            Source = "LogService",
            Status = LogStatus.PartiallySucceeded,
            Kind = LogItemKind.Marker
        };
    }
}
