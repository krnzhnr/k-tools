// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Тесты потребления типизированных результатов на границах приложения:
/// стартовая инициализация настроек в реестре скриптов и преобразование
/// результата сохранения в результат завершения без рефлексии и разбора строк.
/// </summary>
[TestClass]
public class SettingsStartupContractTests
{
    private RecordingLogService _logService = null!;

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
    }

    /// <summary>
    /// Проверяет, что реестр скриптов не отбрасывает результат инициализации настроек:
    /// неуспех фиксируется как типизированная ошибка, а не остаётся молчаливым.
    /// </summary>
    [TestMethod]
    public void RecordDefaultsOutcome_DegradedFailure_IsLoggedAsFailure()
    {
        // Act
        PersistenceResult consumed = ScriptRegistry.RecordDefaultsOutcome(
            PersistenceResult.Failed(
                PersistenceErrorCodes.DegradedLoad,
                PersistenceSummaries.DegradedLoad,
                new JsonException("raw parse detail must not surface"),
                Array.Empty<string>(),
                degraded: true,
                source: PersistenceSource.Fallback),
            _logService);

        // Assert
        consumed.IsFailure.Should().BeTrue();
        IReadOnlyList<RecordedLogEvent> events = _logService.EventsById(SettingsEventIds.DefaultsDeferred);
        events.Should().HaveCount(1, "результат старта не может быть отброшен молча");
        events[0].Level.Should().Be(LogLevel.Error);
        events[0].Status.Should().Be(LogStatus.Failed);
        events[0].GetProperty<bool>("Persisted").Should().BeFalse();
        events[0].GetProperty<string>("ErrorCode").Should().Be(PersistenceErrorCodes.DegradedLoad);
        events[0].Message.Should().Be(PersistenceSummaries.DegradedLoad);
        events[0].Message.Should().NotContain("raw parse detail");
        _logService.EventsById(SettingsEventIds.DefaultsPersisted).Should().BeEmpty(
            "событие «записано на диск» не используется для неуспешного исхода");
    }

    /// <summary>
    /// Проверяет, что отложенный результат инициализации фиксируется как предупреждение,
    /// а не как записанное на диск состояние.
    /// </summary>
    [TestMethod]
    public void RecordDefaultsOutcome_PendingResult_IsLoggedAsWarning()
    {
        // Act
        PersistenceResult consumed = ScriptRegistry.RecordDefaultsOutcome(
            PersistenceResult.Pending(new[] { "General/Theme" }),
            _logService);

        // Assert
        consumed.IsPending.Should().BeTrue();
        RecordedLogEvent recorded = _logService.EventsById(SettingsEventIds.DefaultsDeferred).Single();
        recorded.Level.Should().Be(LogLevel.Warning);
        recorded.Status.Should().Be(LogStatus.Changed);
        recorded.GetProperty<bool>("Persisted").Should().BeFalse();
        recorded.GetProperty<int>("Count").Should().Be(1);
        _logService.EventsById(SettingsEventIds.DefaultsPersisted).Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что успешный, но не подтверждённый записью результат
    /// не публикуется как запись на диск.
    /// </summary>
    [TestMethod]
    public void RecordDefaultsOutcome_SuccessWithoutPersisted_IsNotReportedAsPersisted()
    {
        // Act
        PersistenceResult consumed = ScriptRegistry.RecordDefaultsOutcome(
            PersistenceResult.Succeeded(
                Array.Empty<string>(),
                PersistenceSource.Defaults,
                persisted: false),
            _logService);

        // Assert
        consumed.IsSuccess.Should().BeTrue();
        consumed.Persisted.Should().BeFalse();
        _logService.EventsById(SettingsEventIds.DefaultsPersisted).Should().BeEmpty();
        _logService.EventsById(SettingsEventIds.DefaultsDeferred).Should().HaveCount(1);
    }

    /// <summary>
    /// Проверяет, что подтверждённая запись настроек по умолчанию фиксируется как успех.
    /// </summary>
    [TestMethod]
    public void RecordDefaultsOutcome_PersistedResult_IsLoggedAsSuccess()
    {
        // Act
        PersistenceResult consumed = ScriptRegistry.RecordDefaultsOutcome(
            PersistenceResult.Succeeded(source: PersistenceSource.Disk, persisted: true),
            _logService);

        // Assert
        consumed.IsSuccess.Should().BeTrue();
        RecordedLogEvent recorded = _logService.EventsById(SettingsEventIds.DefaultsPersisted).Single();
        recorded.Level.Should().Be(LogLevel.Info);
        recorded.Status.Should().Be(LogStatus.Succeeded);
        recorded.GetProperty<bool>("Persisted").Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что пустой результат не превращается в ложный успех на старте.
    /// </summary>
    [TestMethod]
    public void RecordDefaultsOutcome_NullResult_BecomesTypedFailure()
    {
        // Act
        PersistenceResult consumed = ScriptRegistry.RecordDefaultsOutcome(null, _logService);

        // Assert
        consumed.IsFailure.Should().BeTrue();
        consumed.ErrorCode.Should().Be(PersistenceErrorCodes.NoResult);
        _logService.EventsById(SettingsEventIds.DefaultsDeferred)
            .Should().ContainSingle()
            .Which.GetProperty<bool>("Persisted").Should().BeFalse();
        _logService.EventsById(SettingsEventIds.DefaultsPersisted).Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что преобразование результата сохранения для завершения приложения
    /// не использует рефлексию и разбор строк по именам членов.
    /// </summary>
    [TestMethod]
    public void SettingsPersistenceInvoker_UsesTypedContractWithoutReflection()
    {
        // Assert
        foreach (MethodInfo method in typeof(SettingsPersistenceInvoker).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            method.ReturnType.Should().Be<SettingsPersistenceResult>();
        }

        MethodInfo read = typeof(SettingsPersistenceInvoker)
            .GetMethod(nameof(SettingsPersistenceInvoker.ReadResult), BindingFlags.Public | BindingFlags.Static)!;
        read.GetParameters().Should().HaveCount(1);
        read.GetParameters()[0].ParameterType.Should().Be<PersistenceResult>();
        typeof(SettingsPersistenceInvoker).GetMethod("InvokeSaveSettings").Should().BeNull("мёртвый код удалён");
        typeof(SettingsPersistenceInvoker).GetMethod("InvokeInitializeDefaults").Should().BeNull("мёртвый код удалён");
    }

    /// <summary>
    /// Проверяет, что завершение приложения считает настройки сохранёнными
    /// только при подтверждённой записи на диск.
    /// </summary>
    [TestMethod]
    public void ReadResult_PersistedOnlyForConfirmedWrite()
    {
        // Act
        SettingsPersistenceResult persisted = SettingsPersistenceInvoker.ReadResult(
            PersistenceResult.Succeeded());
        SettingsPersistenceResult pending = SettingsPersistenceInvoker.ReadResult(
            PersistenceResult.Pending(new[] { "Window/Width" }));
        SettingsPersistenceResult failed = SettingsPersistenceInvoker.ReadResult(
            PersistenceResult.Failed(
                PersistenceErrorCodes.AccessDenied,
                PersistenceSummaries.WriteFailure,
                null));
        SettingsPersistenceResult missing = SettingsPersistenceInvoker.ReadResult(null);

        // Assert
        persisted.Persisted.Should().BeTrue();
        persisted.IsKnown.Should().BeTrue();
        pending.Persisted.Should().BeFalse();
        pending.IsKnown.Should().BeTrue();
        failed.Persisted.Should().BeFalse();
        failed.ErrorCode.Should().Be(PersistenceErrorCodes.AccessDenied);
        missing.Persisted.Should().BeFalse();
        missing.ErrorCode.Should().Be(PersistenceErrorCodes.NoResult);
    }

    /// <summary>
    /// Проверяет, что неуспешное сохранение настроек не позволяет сообщить
    /// об успешном завершении приложения.
    /// </summary>
    [TestMethod]
    public void ShutdownPolicy_RequiresPersistedSettings()
    {
        // Act
        MainWindowShutdownResult success = MainWindowShutdownPolicy.Evaluate(
            SettingsPersistenceInvoker.ReadResult(PersistenceResult.Succeeded()),
            new ProcessTerminationSummary(0, 0, 0, 0),
            true,
            true);

        MainWindowShutdownResult pending = MainWindowShutdownPolicy.Evaluate(
            SettingsPersistenceInvoker.ReadResult(PersistenceResult.Pending()),
            new ProcessTerminationSummary(0, 0, 0, 0),
            true,
            true);

        // Assert
        success.Succeeded.Should().BeTrue();
        pending.Succeeded.Should().BeFalse("отложенная запись настроек не является успешным завершением");
        pending.Status.Should().Be(LogStatus.Failed);
    }
}
