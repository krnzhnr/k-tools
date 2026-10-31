// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using Moq;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Проверки структурированных вызовов журнала для Moq-моков ILogService.
/// Учитывают обе формы записи: Write(LogEvent) и явную перегрузку Write(eventId, ...),
/// чтобы проверка не проходила вхолостую при смене формы вызова.
/// </summary>
public static class LogVerify
{
    /// <summary>
    /// Проверить, что событие с указанным уровнем и фрагментом сообщения записано.
    /// </summary>
    public static void VerifyEvent(
        this Mock<ILogService> mock,
        LogLevel level,
        string messageFragment,
        Times times)
    {
        mock.AssertWriteTimes(
            call => call.Level == level
                && call.Message.Contains(messageFragment, StringComparison.Ordinal),
            times);
    }

    /// <summary>
    /// Проверить, что событие с указанным EventId и уровнем записано.
    /// </summary>
    public static void VerifyEventId(
        this Mock<ILogService> mock,
        string eventId,
        LogLevel level,
        Times times)
    {
        mock.AssertWriteTimes(
            call => call.EventId == eventId && call.Level == level,
            times);
    }

    /// <summary>
    /// Проверить, что событие с указанным EventId, уровнем и фрагментом сообщения записано.
    /// </summary>
    public static void VerifyEvent(
        this Mock<ILogService> mock,
        string eventId,
        LogLevel level,
        string messageFragment,
        Times times)
    {
        mock.AssertWriteTimes(
            call => call.EventId == eventId
                && call.Level == level
                && call.Message.Contains(messageFragment, StringComparison.Ordinal),
            times);
    }

    /// <summary>
    /// Проверить, что событие с указанным уровнем и фрагментом сообщения записано ровно один раз
    /// и исключение передано типизированным владельцем <c>LogEvent.Exception</c>/параметром
    /// <c>exception</c>, а не встроено в текст сообщения.
    /// </summary>
    public static void VerifyEventWithException(
        this Mock<ILogService> mock,
        LogLevel level,
        string messageFragment)
    {
        mock.AssertWriteTimes(
            call => call.Level == level
                && call.Message.Contains(messageFragment, StringComparison.Ordinal)
                && call.HasTypedExceptionOwner
                && !CarriesForeignExceptionText(call),
            Moq.Times.Once());
    }

    /// <summary>
    /// Текст исключения не должен попадать в сообщение: типизированный владелец
    /// <see cref="LogEvent.Exception"/> уже несёт message и stack.
    /// Проверка сравнивает с содержимым реально переданного исключения, а не с
    /// запрещённым словом «Exception», которое встречается и в безобидных формулировках.
    /// </summary>
    private static bool CarriesForeignExceptionText(WriteCall call)
    {
        return call.OwnedExceptionMessage is { Length: > 0 } owned
            && call.Message.Contains(owned, StringComparison.Ordinal);
    }

    /// <summary>
    /// Проверить, что ни одного структурированного события не записано.
    /// </summary>
    public static void VerifyNoEvents(this Mock<ILogService> mock)
    {
        mock.AssertWriteTimes(_ => true, Moq.Times.Never());
    }

    /// <summary>
    /// Проверить, что ни одно событие с уровнем не ниже указанного не содержит фрагмент сообщения.
    /// Используется для проверки отсутствия шумных сообщений.
    /// </summary>
    public static void VerifyNoEventAtOrAbove(
        this Mock<ILogService> mock,
        LogLevel level,
        string messageFragment)
    {
        mock.AssertWriteTimes(
            call => call.Level >= level
                && call.Message.Contains(messageFragment, StringComparison.Ordinal),
            Moq.Times.Never());
    }

    private static void AssertWriteTimes(
        this Mock<ILogService> mock,
        Func<WriteCall, bool> predicate,
        Times times)
    {
        List<WriteCall> calls = EnumerateWrites(mock).Where(predicate).ToList();
        int actual = calls.Count;

        if (times == Moq.Times.Never())
        {
            actual.Should().Be(0, "структурированных вызовов Write с таким признаком быть не должно");
            return;
        }

        if (actual == 0)
        {
            mock.Invocations
                .Where(invocation => invocation.Method.Name == nameof(ILogService.Write))
                .Should()
                .NotBeEmpty(
                    "проверка структурированных вызовов должна видеть реальные вызовы Write, иначе она проходит вхолостую");
        }

        if (times == Moq.Times.Once())
        {
            actual.Should().Be(1, "ожидался ровно один структурированный вызов Write");
            return;
        }

        if (times == Moq.Times.AtLeastOnce())
        {
            actual.Should().BeGreaterThanOrEqualTo(1, "ожидался хотя бы один структурированный вызов Write");
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(times),
            times,
            "LogVerify поддерживает только Times.Never(), Times.Once() и Times.AtLeastOnce()");
    }

    private static IEnumerable<WriteCall> EnumerateWrites(Mock<ILogService> mock)
    {
        foreach (IInvocation invocation in mock.Invocations)
        {
            if (!string.Equals(invocation.Method.Name, nameof(ILogService.Write), StringComparison.Ordinal) ||
                invocation.Arguments.Count == 0)
            {
                continue;
            }

            if (invocation.Arguments[0] is LogEvent logEvent)
            {
                yield return new WriteCall(
                    logEvent.EventId,
                    logEvent.Level,
                    logEvent.Status,
                    logEvent.Message,
                    logEvent.Source,
                    logEvent.Properties,
                    logEvent.Exception is not null,
                    logEvent.Exception?.Message);
                continue;
            }

            if (invocation.Arguments.Count < 8 || invocation.Arguments[0] is not string eventId)
            {
                continue;
            }

            if (invocation.Arguments[2] is string message)
            {
                yield return new WriteCall(
                    eventId,
                    (LogLevel)invocation.Arguments[1]!,
                    (LogStatus)invocation.Arguments[4]!,
                    message,
                    (string)invocation.Arguments[3]!,
                    invocation.Arguments[6] as IReadOnlyDictionary<string, object?>,
                    invocation.Arguments[7] is Exception,
                    (invocation.Arguments[7] as Exception)?.Message);
                continue;
            }

            yield return new WriteCall(
                eventId,
                (LogLevel)invocation.Arguments[1]!,
                (LogStatus)invocation.Arguments[2]!,
                (string)invocation.Arguments[3]!,
                (string)invocation.Arguments[5]!,
                invocation.Arguments[7] as IReadOnlyDictionary<string, object?>,
                invocation.Arguments[4] is Exception,
                (invocation.Arguments[4] as Exception)?.Message);
        }
    }

    private sealed record WriteCall(
        string EventId,
        LogLevel Level,
        LogStatus Status,
        string Message,
        string Source,
        IReadOnlyDictionary<string, object?>? Properties,
        bool HasTypedExceptionOwner,
        string? OwnedExceptionMessage);
}
