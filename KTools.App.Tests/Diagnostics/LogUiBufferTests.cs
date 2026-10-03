// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogUiBufferTests
{
    private static LogEvent CreateEvent(long sequence, Guid? session = null, string text = "событие")
    {
        return new LogEvent
        {
            TimestampUtc = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero).AddSeconds(sequence),
            Sequence = sequence,
            SessionId = session ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
            EventId = "test.event",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Source = "TestSource",
            Message = text
        };
    }

    [TestMethod]
    public void Add_SameEventTwice_IsDeduplicated()
    {
        var buffer = new LogUiBuffer();
        LogEvent logEvent = CreateEvent(1);

        buffer.Add(logEvent).Should().BeTrue();
        buffer.Add(logEvent).Should().BeFalse("повторное событие с тем же ключом корреляции отбрасывается");

        buffer.Count.Should().Be(1);
        buffer.Drain(out _).Should().HaveCount(1);
    }

    [TestMethod]
    public void Add_DifferentSessionsWithSameSequence_AreNotDeduplicated()
    {
        var buffer = new LogUiBuffer();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        buffer.Add(CreateEvent(1, first)).Should().BeTrue();
        buffer.Add(CreateEvent(1, second)).Should().BeTrue();

        buffer.Count.Should().Be(2, "разные сессии дают разные ключи дедупликации");
    }

    [TestMethod]
    public void RegisterSnapshot_RemovesAlreadyLoadedEventsFromPending()
    {
        var buffer = new LogUiBuffer();
        buffer.Add(CreateEvent(1));
        buffer.Add(CreateEvent(2));
        buffer.Add(CreateEvent(3));

        IReadOnlyList<LogEvent> snapshot = new[] { CreateEvent(1), CreateEvent(2) };
        buffer.RegisterSnapshot(snapshot);
        int purged = buffer.PurgeSnapshotDuplicates(snapshot.Select(e => e.CorrelationKey));

        purged.Should().Be(2, "события уже присутствующие в снимке удаляются из очереди интерфейса");
        IReadOnlyList<LogItem> batch = buffer.Drain(out _);
        batch.Should().HaveCount(1);
        batch[0].Event!.Sequence.Should().Be(3);
    }

    [TestMethod]
    public void RegisterSnapshot_BlocksLaterDuplicates()
    {
        var buffer = new LogUiBuffer();
        buffer.RegisterSnapshot(new[] { CreateEvent(7) });

        buffer.Add(CreateEvent(7)).Should().BeFalse("событие из снимка не добавляется повторно из live-потока");
        buffer.Count.Should().Be(0);
    }

    [TestMethod]
    public void Add_BeyondCountCapacity_KeepsNewestAndCountsDropped()
    {
        var buffer = new LogUiBuffer(capacity: 16, maxBytes: 1024 * 1024);
        for (int index = 1; index <= 100; index++)
        {
            buffer.Add(CreateEvent(index));
        }

        buffer.Count.Should().Be(16);
        buffer.DroppedCount.Should().Be(84, "переполнение очереди интерфейса измеряется");
        IReadOnlyList<LogItem> batch = buffer.Drain(out long dropped);
        batch.Should().HaveCount(16);
        batch[^1].Event!.Sequence.Should().Be(100, "сохраняются последние события");
        dropped.Should().Be(84);
        buffer.Drain(out long afterDrain).Should().HaveCount(0);
        afterDrain.Should().Be(0);
    }

    [TestMethod]
    public void Add_BeyondByteCapacity_KeepsNewestAndCountsDropped()
    {
        var buffer = new LogUiBuffer(capacity: 4096, maxBytes: 64 * 1024);
        string payload = new('я', 2000);
        for (int index = 1; index <= 200; index++)
        {
            buffer.Add(CreateEvent(index, null, payload + " " + index));
        }

        buffer.Count.Should().BeLessThan(100, "очередь ограничена по байтам");
        buffer.DroppedCount.Should().BeGreaterThan(0, "переполнение по байтам измеряется");
        IReadOnlyList<LogItem> batch = buffer.Drain(out _);
        batch[^1].Event!.Sequence.Should().Be(200);
    }

    [TestMethod]
    public void Add_EventLargerThanItemBudget_IsDroppedBeforeMaterialization()
    {
        var buffer = new LogUiBuffer(capacity: 16, maxBytes: 64 * 1024);
        LogEvent oversized = CreateEvent(1, text: new string('я', 100_000));

        buffer.Add(oversized).Should().BeFalse();
        buffer.Count.Should().Be(0);
        buffer.DroppedCount.Should().Be(1);
        buffer.Drain(out long dropped).Should().BeEmpty();
        dropped.Should().Be(1);
    }

    [TestMethod]
    public void Reset_ClearsPendingSeenAndDropped()
    {
        var buffer = new LogUiBuffer();
        buffer.Add(CreateEvent(1));
        buffer.RegisterSnapshot(new[] { CreateEvent(2) });

        buffer.Reset();

        buffer.Count.Should().Be(0);
        buffer.DroppedCount.Should().Be(0);
        buffer.Add(CreateEvent(1)).Should().BeTrue("после сброса дедупликация начинается заново");
    }

    [TestMethod]
    public void Drain_EmitsOnlyStructuredItems()
    {
        var buffer = new LogUiBuffer();
        buffer.Add(CreateEvent(5));

        IReadOnlyList<LogItem> batch = buffer.Drain(out _);

        batch.Should().OnlyContain(item => item.Kind == LogItemKind.Event && item.HasStructuredEvent);
        batch[0].Message.Should().Contain("событие");
    }
}
