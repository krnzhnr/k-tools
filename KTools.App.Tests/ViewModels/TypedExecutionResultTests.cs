// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Типизированные контракты результата выполнения: статусы, коды ошибок, очистка,
/// границы сообщений и корреляция элемента с операцией.
/// </summary>
[TestClass]
public sealed class TypedExecutionResultTests
{
    [TestMethod]
    public void ItemResult_Failed_CarriesTypedErrorContract()
    {
        // Act
        ItemResult result = ItemResult.Create(
            "item-typed-1",
            ExecutionStatus.Failed,
            new[] { "Ошибка декодирования" },
            "decode-failed",
            exitCode: 1,
            retryable: true,
            cleanupState: CleanupState.Completed);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("decode-failed");
        result.ExitCode.Should().Be(1);
        result.Retryable.Should().BeTrue();
        result.CleanupState.Should().Be(CleanupState.Completed);
        result.IsFailure.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.IsTerminal.Should().BeTrue();
        result.IsPartial.Should().BeFalse();
        result.MessageCount.Should().Be(1);
    }

    [TestMethod]
    public void ItemResult_LogStatus_MapsEveryExecutionStatus()
    {
        // Arrange
        (ExecutionStatus Status, LogStatus Expected)[] cases =
        {
            (ExecutionStatus.Succeeded, LogStatus.Succeeded),
            (ExecutionStatus.Failed, LogStatus.Failed),
            (ExecutionStatus.Cancelled, LogStatus.Cancelled),
            (ExecutionStatus.Skipped, LogStatus.Skipped),
            (ExecutionStatus.PartiallySucceeded, LogStatus.PartiallySucceeded)
        };

        // Act / Assert
        foreach ((ExecutionStatus status, LogStatus expected) in cases)
        {
            ExecutionContext context = ExecutionContext.CreateBatch("test", 1).ForItem(0);
            ExecutionResult result = ExecutionResult.Create(context, status);

            result.LogStatus.Should().Be(expected, $"статус '{status}' обязан иметь типизированный уровень журнала");
        }
    }

    [TestMethod]
    public void ItemResult_Messages_AreBounded()
    {
        // Arrange
        const int maxMessages = 100;
        const int maxMessageLength = 2048;

        // Act
        ItemResult result = ItemResult.Create(
            "item-typed-bounds",
            ExecutionStatus.Failed,
            Enumerable.Range(0, maxMessages + 50)
                .Select(index => new string('x', maxMessageLength + 10) + index),
            "bounds-check");

        // Assert
        result.Messages.Should().HaveCountLessThanOrEqualTo(maxMessages);
        result.Messages.Should().OnlyContain(message => message.Length <= maxMessageLength,
            "каждое сообщение результата обязано иметь конечную длину");
    }

    [TestMethod]
    public void ItemResult_OutputExists_FallsBackToFileSystemVerification()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        string existing = scope.CreateFile("result.mkv", "data");
        ExecutionContext context = ExecutionContext.CreateBatch("test", 2).ForItem(0);

        // Act
        ExecutionResult verified = ExecutionResult.Succeeded(context, "Готово", existing);
        ExecutionResult missing = ExecutionResult.Succeeded(context, "Готово", scope.GetFullPath("absent.mkv"));

        // Assert
        verified.OutputExists.Should().BeTrue();
        missing.OutputExists.Should().BeFalse("результат без артефакта не должен считаться успешным по факту наличия файла");
    }

    [TestMethod]
    public void ItemResult_FromException_KeepsStructuredExceptionInfo()
    {
        // Arrange
        ExecutionContext context = ExecutionContext.CreateBatch("test", 1).ForItem(0);
        var failure = new InvalidOperationException("причина сбоя", new ArgumentException("внутренняя причина"));

        // Act
        ExecutionResult result = ExecutionResult.FromException(
            context,
            failure,
            new[] { "Критическая ошибка выполнения." },
            errorCode: "execution-exception");

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("execution-exception");
        result.Exception.Should().BeSameAs(failure);
        result.ExceptionInfo.Should().NotBeNull();
        result.ExceptionInfo!.Type.Should().Be(typeof(InvalidOperationException).FullName);
        result.ExceptionInfo.Inner.Should().NotBeNull("цепочка вложенных исключений сохраняется структурно");
        result.Messages.Should().ContainSingle();
    }

    [TestMethod]
    public void ExecutionResult_AliasFactories_ProduceSameTypedOutcome()
    {
        // Arrange
        ExecutionContext context = ExecutionContext.CreateBatch("test", 1).ForItem(0);

        // Act
        ExecutionResult success = ExecutionResult.Success(context, new[] { "Готово" });
        ExecutionResult failure = ExecutionResult.Failure(context, new[] { "Ошибка" }, "decode-failed");
        ExecutionResult cancelled = ExecutionResult.Cancel(context, new[] { "Отменено" });
        ExecutionResult skipped = ExecutionResult.Skip(context, new[] { "Пропущено" });
        ExecutionResult partial = ExecutionResult.PartialSuccess(context, new[] { "Частично" }, "source-cleanup-failed");

        // Assert
        success.Status.Should().Be(ExecutionStatus.Succeeded);
        success.IsSuccess.Should().BeTrue();
        success.Messages.Should().ContainSingle().Which.Should().Be("Готово");
        failure.Status.Should().Be(ExecutionStatus.Failed);
        failure.ErrorCode.Should().Be("decode-failed");
        cancelled.Status.Should().Be(ExecutionStatus.Cancelled);
        cancelled.CleanupState.Should().Be(CleanupState.Completed);
        skipped.Status.Should().Be(ExecutionStatus.Skipped);
        partial.Status.Should().Be(ExecutionStatus.PartiallySucceeded);
        partial.IsPartial.Should().BeTrue();
        partial.CleanupState.Should().Be(CleanupState.Partial);
    }

    [TestMethod]
    public void ExecutionResult_ExposesMessagesAsReadOnlyList()
    {
        // Arrange
        ExecutionContext context = ExecutionContext.CreateBatch("test", 1).ForItem(0);
        ExecutionResult result = ExecutionResult.Succeeded(context, new[] { "Первое", "Второе" });

        // Assert
        result.Count.Should().Be(2);
        result[0].Should().Be("Первое");
        result.ToExecutionResult().Should().BeSameAs(result);
        result.ToString().Should().Contain(result.ItemId);
    }

    [TestMethod]
    public void ExecutionContext_ForItem_SharesOperationIdAndKeepsQueueSize()
    {
        // Arrange
        ExecutionContext batch = ExecutionContext.CreateBatch("VideoEncodingScript", 3);

        // Act
        ExecutionContext first = batch.ForItem(0);
        ExecutionContext third = batch.ForItem(2);

        // Assert
        batch.IsBatchContext.Should().BeTrue();
        first.IsBatchContext.Should().BeFalse();
        first.OperationId.Should().Be(batch.OperationId);
        third.OperationId.Should().Be(batch.OperationId);
        first.OperationId.Should().NotBe(third.ItemId, "идентификатор элемента не должен совпадать с операцией");
        first.ItemNumber.Should().Be(1);
        third.ItemNumber.Should().Be(3);
        first.Total.Should().Be(3);
        third.ScriptId.Should().Be("VideoEncodingScript");
    }

    [TestMethod]
    public void ExecutionContext_UnsafeIdentifier_IsReplacedWithStableFingerprint()
    {
        // Act
        ExecutionContext context = ExecutionContext.CreateItem(
            "operation-unique",
            0,
            1,
            "test",
            itemId: @"C:\Users\ivanov\secret.mkv");

        // Assert
        context.ItemId.Should().NotContain("ivanov");
        context.ItemId.Should().NotContain(@"\");
        context.ItemId.Should().StartWith("item-");
        context.ItemId.Should().Be(
            ExecutionContext.CreateItem("operation-unique", 0, 1, "test", itemId: @"C:\Users\ivanov\secret.mkv").ItemId,
            "небезопасный идентификатор заменяется стабильным отпечатком");
    }

    [TestMethod]
    public void ExecutionContext_ToLogContext_CarriesOperationAndItem()
    {
        // Arrange
        ExecutionContext item = ExecutionContext.CreateBatch("test", 1).ForItem(0);

        // Act
        LogContext logContext = item.ToLogContext();
        IReadOnlyDictionary<string, object?> properties = item.ToLogProperties();

        // Assert
        logContext.ResolveOperationId().Should().Be(item.OperationId);
        logContext.ResolveItemId().Should().Be(item.ItemId);
        logContext.IsEmpty.Should().BeFalse();
        properties.Should().ContainKey("OperationId");
        properties.Should().ContainKey("ItemId");
        properties.Should().ContainKey("Index");
    }

    [TestMethod]
    public void ExecutionContext_ForItem_RejectsOutOfRangeIndex()
    {
        // Arrange
        ExecutionContext batch = ExecutionContext.CreateBatch("test", 2);

        // Act
        Action negative = () => batch.ForItem(-1);
        Action beyond = () => batch.ForItem(2);

        // Assert
        negative.Should().Throw<ArgumentOutOfRangeException>();
        beyond.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ExecutionContext_CreateBatch_RejectsNegativeTotal()
    {
        // Act
        Action create = () => ExecutionContext.CreateBatch("test", -1);

        // Assert
        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
