// -*- coding: utf-8 -*-
using System;
using System.Globalization;

using KTools_App.Core;

namespace KTools_App.Diagnostics;

/// <summary>
/// Снимок счётчиков журналирования для диагностической поверхности приложения.
/// Значения снимаются без блокировки очереди записи и пригодны только для отображения.
/// </summary>
public sealed record LogServiceStatus(
    LogLevel MinLevel,
    long QueuedEvents,
    long QueuedBytes,
    long DroppedEvents,
    long WriteErrors,
    long RotationErrors,
    long RejectedEvents,
    long SubscriberErrors,
    long SubscriberDropped,
    long DisposeErrors,
    long ReadErrors)
{
    /// <summary>
    /// Записи аварийного канала, реально созданные экземпляром сервиса.
    /// </summary>
    public long EmergencyWritten { get; init; }

    /// <summary>
    /// Записи аварийного канала, потерянные самим каналом (нет каталога, превышен размер,
    /// повторяющийся CrashId).
    /// </summary>
    public long EmergencyDropped { get; init; }

    /// <summary>
    /// Аварийные записи, подавленные исчерпанием бюджета <c>MaxEmergencyReports</c>.
    /// Без этого счётчика потеря аварийного следа выглядит как «ошибок не было».
    /// </summary>
    public long EmergencySuppressed { get; init; }

    /// <summary>
    /// Неудачные попытки запланировать периодический сброс файла журнала.
    /// </summary>
    public long FlushScheduleFailures { get; init; }

    /// <summary>
    /// Признак наличия потерь или отказов, требующих внимания при разборе инцидента.
    /// </summary>
    public bool HasLoss =>
        DroppedEvents > 0
        || RejectedEvents > 0
        || WriteErrors > 0
        || RotationErrors > 0
        || SubscriberErrors > 0
        || SubscriberDropped > 0
        || DisposeErrors > 0
        || ReadErrors > 0
        || EmergencyDropped > 0
        || EmergencySuppressed > 0
        || FlushScheduleFailures > 0;

    /// <summary>
    /// Компактное представление счётчиков для диагностического события журнала.
    /// </summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"minLevel={MinLevel};queued={QueuedEvents};queuedBytes={QueuedBytes};dropped={DroppedEvents};writeErrors={WriteErrors};rotationErrors={RotationErrors};rejected={RejectedEvents};subscriberErrors={SubscriberErrors};subscriberDropped={SubscriberDropped};disposeErrors={DisposeErrors};readErrors={ReadErrors};emergencyWritten={EmergencyWritten};emergencyDropped={EmergencyDropped};emergencySuppressed={EmergencySuppressed};flushScheduleFailures={FlushScheduleFailures};loss={HasLoss}");
}
