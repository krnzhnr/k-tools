// -*- coding: utf-8 -*-
using System;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogUiSessionTests
{
    private static LogEvent CreateEvent(long sequence, string text = "событие")
    {
        return new LogEvent
        {
            TimestampUtc = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero).AddSeconds(sequence),
            Sequence = sequence,
            SessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            EventId = "test.event",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Source = "TestSource",
            Message = text
        };
    }

    [TestMethod]
    public void Activate_NewGeneration_RejectsEventsFromPreviousGeneration()
    {
        var session = new LogUiSession();
        long first = session.Activate();
        session.TryAdd(first, CreateEvent(1)).Should().BeTrue();

        long second = session.Activate();

        session.TryAdd(first, CreateEvent(2)).Should().BeFalse();
        session.TryAdd(second, CreateEvent(2)).Should().BeTrue();
        session.TryDrain(second, out var items, out long dropped).Should().BeTrue();
        items.Should().ContainSingle(item => item.Event!.Sequence == 2);
        dropped.Should().Be(0);
    }

    [TestMethod]
    public void Deactivate_StaleGeneration_DoesNotResetCurrentGeneration()
    {
        var session = new LogUiSession();
        long first = session.Activate();
        long second = session.Activate();

        session.Deactivate(first);
        session.TryAdd(second, CreateEvent(3)).Should().BeTrue();

        session.TryDrain(second, out var items, out _).Should().BeTrue();
        items.Should().ContainSingle(item => item.Event!.Sequence == 3);
    }

    [TestMethod]
    public void BatchState_IsClaimedOncePerGeneration()
    {
        var session = new LogUiSession();
        long generation = session.Activate();

        session.TryBeginBatch(generation).Should().BeTrue();
        session.TryBeginBatch(generation).Should().BeFalse();
        session.EndBatch(generation);
        session.TryBeginBatch(generation).Should().BeTrue();
    }

    [TestMethod]
    public void TryDrain_OversizedEvent_ReportsDropForMarker()
    {
        var session = new LogUiSession(capacity: 16, maxBytes: 64 * 1024);
        long generation = session.Activate();
        LogEvent oversized = CreateEvent(1, new string('я', 100_000));

        session.TryAdd(generation, oversized).Should().BeFalse("сброс виден вызывающему коду, потеря измеряется счётчиком");
        session.TryBeginBatch(generation).Should().BeTrue();
        session.TryDrain(generation, out var items, out long dropped).Should().BeTrue();

        items.Should().BeEmpty();
        dropped.Should().Be(1);
    }

    [TestMethod]
    public void TryAdd_DroppedEvent_ReturnsFalseAndKeepsDropCounter()
    {
        var session = new LogUiSession(capacity: 16, maxBytes: 64 * 1024);
        long generation = session.Activate();
        LogEvent oversized = CreateEvent(1, new string('я', 100_000));

        session.TryAdd(generation, oversized).Should().BeFalse("сброшенное событие обязано отличаться от доставленного");
        session.TryDrain(generation, out var items, out long dropped).Should().BeTrue();
        items.Should().BeEmpty();
        dropped.Should().Be(1, "потеря измеряется счётчиком даже при честном результате TryAdd");
    }

    [TestMethod]
    public void TryAdd_DuplicateEvent_ReturnsFalse()
    {
        var session = new LogUiSession();
        long generation = session.Activate();

        session.TryAdd(generation, CreateEvent(1)).Should().BeTrue();
        session.TryAdd(generation, CreateEvent(1)).Should().BeFalse("повтор не доставляется заново");
        session.TryDrain(generation, out var items, out _).Should().BeTrue();
        items.Should().HaveCount(1);
    }
}
