// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogEventTests
{
    private static LogEvent CreateSample(long sequence, Guid session)
    {
        return new LogEvent
        {
            TimestampUtc = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            Sequence = sequence,
            SessionId = session,
            EventId = "exec.item.succeeded",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Source = "VideoEncodingScript",
            Message = "Видео закодировано"
        };
    }

    [TestMethod]
    public void LogEvent_DefaultShape_KeepsCorrelationIndependent()
    {
        // Arrange
        LogEvent logEvent = CreateSample(41, Guid.NewGuid());

        // Act
        LogEvent process = logEvent with
        {
            ProcessId = "process-01",
            Pid = 1234
        };

        // Assert
        process.Sequence.Should().Be(41);
        process.ProcessId.Should().Be("process-01", "внутренний идентификатор запуска");
        process.Pid.Should().Be(1234, "идентификатор процесса ОС");
        process.ProcessId.Should().NotBe(process.Pid?.ToString());
        logEvent.ProcessId.Should().BeNull("record неизменяем: with создаёт копию");
    }

    [TestMethod]
    public void LogEvent_TimestampUtc_IsIso8601WithZMarker()
    {
        // Arrange
        LogEvent logEvent = CreateSample(1, Guid.NewGuid());

        // Act
        string display = logEvent.ToDisplayString();

        // Assert
        logEvent.TimestampUtc.Offset.Should().Be(TimeSpan.Zero);
        display.Should().StartWith("2026-09-25T12:00:00.000Z");
    }

    [TestMethod]
    public void LogEvent_CorrelationKey_IsStablePerSessionAndSequence()
    {
        // Arrange
        Guid session = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        LogEvent first = CreateSample(7, session);
        LogEvent second = CreateSample(7, Guid.NewGuid());

        // Act
        string firstKey = first.CorrelationKey;
        string secondKey = second.CorrelationKey;

        // Assert
        firstKey.Should().Be((second with { SessionId = session }).CorrelationKey);
        firstKey.Should().NotBe(secondKey, "разные сессии дают разные ключи дедупликации");
        firstKey.Should().Be(session.ToString("N") + ":7");
    }

    [TestMethod]
    public void LogEvent_IsLegacy_DetectedByStablePrefixOnly()
    {
        // Arrange
        LogEvent legacy = new LogEvent { EventId = "legacy.settingsmanager.error" };
        LogEvent structured = new LogEvent { EventId = "settings.changed" };

        // Act / Assert
        legacy.IsLegacy.Should().BeTrue();
        structured.IsLegacy.Should().BeFalse();
    }

    [TestMethod]
    public void LogEvent_DisplayString_ContainsTypedFieldsAndNoRawJson()
    {
        // Arrange
        LogEvent logEvent = new LogEvent
        {
            TimestampUtc = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            Sequence = 12,
            SessionId = Guid.NewGuid(),
            EventId = "process.exit",
            Level = LogLevel.Error,
            Status = LogStatus.Failed,
            Source = "Eac3toRunner",
            OperationId = "op-1",
            ItemId = "item-4",
            ProcessId = "process-07",
            Pid = 4242,
            Attempt = 2,
            Message = "Сдвиг аудио не выполнен",
            Properties = new Dictionary<string, object?> { ["ErrorCode"] = "EXTERNAL_PROCESS_FAILED", ["ExitCode"] = 1 },
            ExitCode = 1,
            DurationMs = 9001,
            Tool = "eac3to",
            ErrorCode = "EXTERNAL_PROCESS_FAILED"
        };

        // Act
        string display = logEvent.ToDisplayString();

        // Assert
        display.Should().Contain("ERROR");
        display.Should().Contain("process.exit");
        display.Should().Contain("op=op-1");
        display.Should().Contain("item=item-4");
        display.Should().Contain("process=process-07");
        display.Should().Contain("pid=4242");
        display.Should().Contain("attempt=2");
        display.Should().Contain("exit=1");
        display.Should().Contain("durationMs=9001");
        display.Should().Contain("ErrorCode=EXTERNAL_PROCESS_FAILED");
        display.Should().NotContain("\"message\":", "отображение не должно содержать сырой JSON");
        display.Should().NotContain("\n", "одно событие — одна строка отображения");
    }

    [TestMethod]
    public void LogContext_WithOperations_CreatesChildrenWithoutMutation()
    {
        // Arrange
        LogContext root = LogContext.Empty.WithOperation("op-1");

        // Act
        LogContext child = root.ToChild().WithItem("item-1").WithProcess("process-1").WithAttempt(2);
        LogContext sibling = root.ToChild().WithItem("item-2");

        // Assert
        child.ResolveOperationId().Should().Be("op-1", "наследование операции от родителя");
        child.ResolveItemId().Should().Be("item-1");
        child.ResolveProcessId().Should().Be("process-1");
        child.ResolveAttempt().Should().Be(2);
        sibling.ResolveItemId().Should().Be("item-2");
        root.ResolveItemId().Should().BeNull("родительский контекст не мутируется");
        root.ResolveProcessId().Should().BeNull();
        root.ResolveAttempt().Should().BeNull();
        child.Parent.Should().NotBeNull();
        LogContext cursor = child;
        while (cursor.Parent is not null)
        {
            cursor = cursor.Parent;
        }

        cursor.Should().BeSameAs(LogContext.Empty, "цепочка контекста ведёт к корню");
    }

    [TestMethod]
    public void LogContext_ToProperties_ContainsResolvedCorrelation()
    {
        // Arrange
        LogContext context = LogContext.Empty
            .WithOperation("op-9")
            .ToChild()
            .WithItem("item-9")
            .WithTool("ffmpeg");

        // Act
        IReadOnlyDictionary<string, object?> properties = context.ToProperties();

        // Assert
        properties["OperationId"].Should().Be("op-9");
        properties["ItemId"].Should().Be("item-9");
        properties["Tool"].Should().Be("ffmpeg");
        properties.ContainsKey("Attempt").Should().BeFalse();
        context.IsEmpty.Should().BeFalse();
        LogContext.Empty.IsEmpty.Should().BeTrue();
        LogContext.Empty.ToProperties().Should().BeEmpty();
    }

    [TestMethod]
    public void LogContext_WithBlankValues_NormalizesToNull()
    {
        // Arrange
        LogContext context = LogContext.Empty.WithOperation("   ");

        // Act / Assert
        context.IsEmpty.Should().BeTrue();
        LogContext.Empty.WithAttempt(-5).ResolveAttempt().Should().BeNull("попытка вне диапазона отбрасывается");
        LogContext.Empty.WithAttempt(0).ResolveAttempt().Should().BeNull("попытка нулевая недопустима");
        LogContext.Empty.WithAttempt(1).ResolveAttempt().Should().Be(1);
        LogContext.Empty.WithProcess(@"C:\Users\ivan\proc.exe").ResolveProcessId().Should().BeNull();
        LogContext.Empty.WithItem("https://host/item").ResolveItemId().Should().BeNull();
    }

    [TestMethod]
    public void ExceptionInfo_FromException_KeepsInnerChainWithoutDuplicatingMessage()
    {
        // Arrange
        Exception inner;
        try
        {
            try
            {
                throw new ArgumentException("внутренняя причина");
            }
            catch (Exception caught)
            {
                inner = caught;
                throw new InvalidOperationException("внешняя причина", inner);
            }
        }
        catch (Exception caught)
        {
            var root = caught;
            try
            {
                throw new ApplicationException("верхний сбой", root);
            }
            catch (Exception top)
            {
                // Act
                ExceptionInfo info = ExceptionInfo.FromException(top);

                // Assert
                info.Type.Should().Be("System.ApplicationException");
                info.Message.Should().Be("верхний сбой");
                info.Inner.Should().NotBeNull();
                info.Inner!.Type.Should().Be("System.InvalidOperationException");
                info.Inner.Message.Should().Be("внешняя причина");
                info.Inner.Inner!.Message.Should().Be("внутренняя причина");
                info.ChainLength.Should().Be(3);
                info.EnumerateChain().Should().HaveCount(3);
                info.Message.Should().NotContain("внешняя причина", "сообщение внутреннего исключения не дублируется в summary");
            }
        }
    }

    [TestMethod]
    public void ExceptionInfo_FromException_RespectsMaxDepth()
    {
        // Arrange
        Exception exception = BuildDeepChain(20);

        // Act
        ExceptionInfo info = ExceptionInfo.FromException(exception, 4);

        // Assert
        info.ChainLength.Should().Be(4);
    }

    [TestMethod]
    public void ExceptionInfo_DeepChain_RecordsOriginalAndKeptCounts()
    {
        Exception exception = BuildDeepChain(6);

        ExceptionInfo info = ExceptionInfo.FromException(exception, 2);

        info.ExceptionTruncated.Should().BeTrue();
        info.OriginalChainLength.Should().Be(6);
        info.KeptChainLength.Should().Be(2);
        info.ChainLength.Should().Be(2);
    }

    [TestMethod]
    public void ExceptionInfo_HResultAndStack_AreCaptured()
    {
        // Arrange
        Exception exception;
        try
        {
            throw new InvalidOperationException("сбой с трассировкой");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        // Act
        ExceptionInfo info = ExceptionInfo.FromException(exception);

        // Assert
        info.HResult.Should().Be(exception.HResult);
        info.StackTrace.Should().Contain(nameof(ExceptionInfo_HResultAndStack_AreCaptured));
        info.StackTrace.Should().NotBeNull();
    }

    private static Exception BuildDeepChain(int depth)
    {
        Exception current = new InvalidOperationException("уровень 0");
        for (int index = 1; index < depth; index++)
        {
            current = new InvalidOperationException("уровень " + index, current);
        }

        return current;
    }
}
