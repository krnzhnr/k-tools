// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Контрактные тесты persistence-слоя настроек: типизированный результат,
/// приватность журналирования и совместимость событий с редактором журнала.
/// </summary>
[TestClass]
public class SettingsPersistenceDiagnosticsTests
{
    [TestMethod]
    public void PersistenceResult_ExposesTypedStatusAndNoPublicException()
    {
        // Act
        PropertyInfo[] publicProperties = typeof(PersistenceResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Assert
        publicProperties.Should().Contain(p => p.Name == nameof(PersistenceResult.Status));
        publicProperties.Should().Contain(p => p.Name == nameof(PersistenceResult.ErrorCode));
        publicProperties.Should().Contain(p => p.Name == nameof(PersistenceResult.IsSuccess));
        publicProperties.Should().Contain(p => p.Name == nameof(PersistenceResult.IsPending));
        publicProperties.Should().Contain(p => p.Name == nameof(PersistenceResult.ChangedKeys));
        publicProperties.Should().Contain(p => p.Name == nameof(PersistenceResult.ChangedCount));
        publicProperties.Should().Contain(p => p.Name == nameof(PersistenceResult.ExceptionInfo));
        publicProperties.Should().NotContain(
            p => p.PropertyType == typeof(Exception),
            "сырое исключение не должно быть частью публичного API");
    }

    [TestMethod]
    public void ISettingsManager_PersistenceMethods_ReturnTypedResult()
    {
        // Act
        MethodInfo save = typeof(ISettingsManager).GetMethod(nameof(ISettingsManager.SaveSettings))!;
        MethodInfo initialize = typeof(ISettingsManager).GetMethod(nameof(ISettingsManager.InitializeDefaults))!;
        MethodInfo setLogDirectory = typeof(ISettingsManager).GetMethod(nameof(ISettingsManager.SetLogDirectory))!;
        MethodInfo setSetting = typeof(ISettingsManager)
            .GetMethods()
            .Single(m => m.Name == nameof(ISettingsManager.SetSetting) && m.IsGenericMethodDefinition);

        // Assert
        save.ReturnType.Should().Be<PersistenceResult>();
        initialize.ReturnType.Should().Be<PersistenceResult>();
        setLogDirectory.ReturnType.Should().Be<PersistenceResult>();
        setSetting.ReturnType.Should().Be<PersistenceResult>();
        typeof(ISettingsManager).Should().BeAssignableTo<IDisposable>("менеджер должен освобождать таймер отложенной записи");
    }

    [TestMethod]
    public void PersistenceResult_Failed_KeepsSingleExceptionOwnerAsSafeInfo()
    {
        // Arrange
        var exception = new UnauthorizedAccessException("raw path C:\\secret\\settings.json");

        // Act
        PersistenceResult result = PersistenceResult.Failed(
            PersistenceErrorCodes.AccessDenied,
            PersistenceSummaries.WriteFailure,
            exception);

        // Assert
        result.ExceptionInfo.Should().NotBeNull();
        result.ExceptionInfo!.Type.Should().Contain(nameof(UnauthorizedAccessException));
        result.UserSummary.Should().Be(PersistenceSummaries.WriteFailure);
        result.UserSummary.Should().NotContain("C:\\secret");
    }

    [TestMethod]
    public void PersistenceResult_WithChangedKeys_PreservesFailureState()
    {
        // Arrange
        PersistenceResult failure = PersistenceResult.Failed(
            PersistenceErrorCodes.WriteFailed,
            PersistenceSummaries.WriteFailure,
            null);

        // Act
        PersistenceResult updated = failure.WithChangedKeys(new[] { "General/Theme" });

        // Assert
        updated.IsFailure.Should().BeTrue();
        updated.Persisted.Should().BeFalse();
        updated.ChangedCount.Should().Be(1);
        updated.ErrorCode.Should().Be(PersistenceErrorCodes.WriteFailed);
    }

    [TestMethod]
    public void PersistenceResult_NullCandidate_BecomesTypedFailure()
    {
        // Act
        PersistenceResult result = PersistenceResult.Normalize(null);

        // Assert
        result.IsSuccess.Should().BeFalse("пустой результат не должен читаться как успех");
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PersistenceErrorCodes.NoResult);
        result.Persisted.Should().BeFalse();
    }

    [TestMethod]
    public void SettingsEventIds_AreValidForStructuredJournal()
    {
        // Arrange
        string[] ids = typeof(SettingsEventIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        // Assert
        ids.Should().NotBeEmpty();
        foreach (string id in ids)
        {
            LogRedactor.IsValidEventId(id).Should().BeTrue("идентификатор события " + id + " должен соответствовать схеме");
        }
    }

    [TestMethod]
    public void SettingsPersistenceProperties_AreAllowedByRedactor()
    {
        // Arrange
        string[] usedProperties =
        {
            "Group", "Key", "ValueHash", "Persisted", "Count", "Changed", "Status",
            "ErrorCode", "Failed", "Succeeded", "Reason", "CancelReason"
        };

        // Assert
        foreach (string property in usedProperties)
        {
            LogRedactor.Default.IsAllowedPropertyKey(property)
                .Should().BeTrue("свойство " + property + " должно попадать в журнал, а не отбрасываться");
        }
    }

    [TestMethod]
    public void ValueHash_SurvivesRedactionWithoutRawValue()
    {
        // Arrange
        const string secret = "token=super-secret";
        string hash = PersistenceValueHash.Compute(secret);

        // Act
        string redacted = LogRedactor.Default.RedactToken(hash);
        Dictionary<string, object?> properties = new(StringComparer.Ordinal) { ["ValueHash"] = hash };
        IReadOnlyDictionary<string, object?> redactedProperties = LogRedactor.Default.RedactProperties(properties);

        // Assert
        redacted.Should().Be(hash, "хэш не должен искажаться редактором журнала");
        redactedProperties.Should().ContainKey("ValueHash");
        redactedProperties["ValueHash"].ToString().Should().Be(hash);
        redactedProperties["ValueHash"].ToString().Should().NotContain("super-secret");
    }

    [TestMethod]
    public void SetSetting_JournalEvent_ContainsNoRawValueProperties()
    {
        // Arrange
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Group"] = "Download",
            ["Key"] = "AdditionalArgs",
            ["Changed"] = true,
            ["Persisted"] = true,
            ["Count"] = 1,
            ["ValueHash"] = PersistenceValueHash.Compute("--cookies secret")
        };

        // Act
        IReadOnlyDictionary<string, object?> redacted = LogRedactor.Default.RedactProperties(properties);
        string rendered = LogEvent.Format(
            new LogEvent
            {
                EventId = SettingsEventIds.Changed,
                Level = LogLevel.Info,
                Status = LogStatus.Changed,
                Source = SettingsEventIds.Source,
                Message = "Настройка сохранена на диск",
                Properties = redacted
            },
            includeDetail: true);

        // Assert
        redacted.Should().ContainKey("Group").And.ContainKey("Key").And.ContainKey("Persisted");
        rendered.Should().NotContain("secret");
        rendered.Should().Contain(SettingsEventIds.Changed);
        rendered.Should().Contain("Persisted=true");
    }
}
