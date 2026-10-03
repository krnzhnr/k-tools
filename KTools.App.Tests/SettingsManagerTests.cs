// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для менеджера настроек SettingsManager.
/// Проверяют атомарную запись, типизированные результаты persistence-слоя,
/// отсутствие ложного успеха, приватность журналирования и дебаунс пакетных изменений.
/// </summary>
[TestClass]
public class SettingsManagerTests
{
    private const int DebounceWaitMilliseconds = 3000;
    private const int DebounceSettleMilliseconds = 900;

    private Mock<IPathManager> _pathManagerMock = null!;
    private RecordingLogService _logService = null!;
    private string _tempSettingsDir = null!;
    private string _tempSettingsFile = null!;
    private string _tempSettingsTempFile = null!;
    private readonly List<SettingsManager> _managers = new();

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _pathManagerMock = new Mock<IPathManager>();

        // Создаем временную директорию для настроек, чтобы тесты не влияли на реальные файлы
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");
        _tempSettingsTempFile = _tempSettingsFile + ".tmp";

        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempSettingsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (SettingsManager manager in _managers)
        {
            try
            {
                manager.Dispose();
            }
            catch (Exception)
            {
                // Тест уже проверяет освобождение отдельно.
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    /// <summary>
    /// Проверяет, что при отсутствии файла настроек используются значения по умолчанию.
    /// </summary>
    [TestMethod]
    public void SettingsManager_WhenNoFileExists_UsesDefaultValues()
    {
        // Act
        var manager = CreateManager();

        // Assert
        manager.OverwriteExisting.Should().BeFalse();
        manager.Theme.Should().Be("Dark");
        manager.DefaultOutputSubfolder.Should().Be("KTools_Result");
        manager.UseAutoSubfolder.Should().BeFalse();
        manager.BackdropType.Should().Be("Mica");
        manager.EnableParallel.Should().BeTrue();
        manager.ClearListOnAdd.Should().BeFalse();
        manager.AutoCheckUpdates.Should().BeTrue();
        manager.IncludePreReleases.Should().Be(SettingsManager.IsPreviewBuild);
        manager.DebugSimulateOldVersion.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет чтение и запись логических параметров в менеджере настроек.
    /// </summary>
    [TestMethod]
    public void SetSetting_BooleanValue_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.OverwriteExisting = true;

        // Assert
        manager.OverwriteExisting.Should().BeTrue();
        File.Exists(_tempSettingsFile).Should().BeTrue();

        // Проверяем перезагрузку с диска
        var newManager = CreateManager();
        newManager.OverwriteExisting.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет чтение и запись строковых параметров в менеджере настроек.
    /// </summary>
    [TestMethod]
    public void SetSetting_StringValue_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.Theme = "Light";

        // Assert
        manager.Theme.Should().Be("Light");

        // Проверяем перезагрузку
        var newManager = CreateManager();
        newManager.Theme.Should().Be("Light");
    }

    /// <summary>
    /// Проверяет чтение и запись целочисленных параметров в менеджере настроек.
    /// </summary>
    [TestMethod]
    public void SetSetting_IntValue_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.MaxParallelTasks = 8;

        // Assert
        manager.MaxParallelTasks.Should().Be(8);

        // Проверяем перезагрузку
        var newManager = CreateManager();
        newManager.MaxParallelTasks.Should().Be(8);
    }

    /// <summary>
    /// Проверяет чтение и запись списков сложных объектов (шаблонов) в настройках.
    /// </summary>
    [TestMethod]
    public void SetSetting_ListTemplateItem_SavesAndReadsCorrectly()
    {
        // Arrange
        var manager = CreateManager();
        var customTemplates = new List<TemplateItem>
        {
            new() { Pattern = "test_pattern", Description = "тестовый шаблон" }
        };

        // Act
        manager.SearchTemplates = customTemplates;

        // Assert
        manager.SearchTemplates.Should().HaveCount(1);
        manager.SearchTemplates[0].Pattern.Should().Be("test_pattern");
        manager.SearchTemplates[0].Description.Should().Be("тестовый шаблон");

        // Проверяем перезагрузку
        var newManager = CreateManager();
        newManager.SearchTemplates.Should().HaveCount(1);
        newManager.SearchTemplates[0].Pattern.Should().Be("test_pattern");
    }

    /// <summary>
    /// Проверяет, что при повреждённом JSON применяются значения по умолчанию.
    /// </summary>
    [TestMethod]
    public void LoadSettings_CorruptedJson_FallbacksToEmptyCache()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ corrupted json: ");

        // Act
        var manager = CreateManager();

        // Assert
        manager.OverwriteExisting.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет нормализацию имени группы для секций JSON.
    /// </summary>
    [TestMethod]
    public void GetSafeGroupName_VariousCharacters_ReturnsNormalizedString()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        string result = manager.GetSafeGroupName("Test Script → Speed");

        // Assert
        result.Should().Be("Script_Test_Script___Speed");
    }

    /// <summary>
    /// Проверяет, что повреждённый JSON не скрывается как пустой успех:
    /// возвращается типизированная деградированная ошибка и пишется error-событие.
    /// </summary>
    [TestMethod]
    public void LoadSettings_MalformedJson_ReturnsDegradedFailure()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"General\": { broken ");
        var manager = CreateManager();
        _logService.Clear();

        // Act
        PersistenceResult result = manager.LoadSettings();

        // Assert
        result.IsSuccess.Should().BeFalse("ошибка разбора не должна выглядеть как успех");
        result.IsDegraded.Should().BeTrue();
        result.Source.Should().Be(PersistenceSource.Fallback);
        result.ErrorCode.Should().Be(PersistenceErrorCodes.ParseFailed);
        result.Persisted.Should().BeFalse();
        result.ExceptionInfo.Should().NotBeNull("исключение-владелец доступно в безопасном виде");

        IReadOnlyList<RecordedLogEvent> failures = _logService.EventsById(SettingsEventIds.LoadDegraded);
        failures.Should().HaveCount(1);
        failures[0].Level.Should().Be(LogLevel.Error);
        _logService.EventsById(SettingsEventIds.LoadCompleted).Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что успешное сохранение возвращает подтверждённый результат
    /// и оставляет после себя только целостный settings.json без временных файлов.
    /// </summary>
    [TestMethod]
    public void SaveSettings_ValidCache_PersistsFileAndRemovesTempFile()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");
        File.Delete(_tempSettingsFile);

        // Act
        PersistenceResult result = manager.SaveSettings();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue();
        result.ErrorCode.Should().Be(PersistenceErrorCodes.None);
        File.Exists(_tempSettingsFile).Should().BeTrue();
        File.Exists(_tempSettingsTempFile).Should().BeFalse("атомарная запись не оставляет временный файл");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_tempSettingsFile));
        document.RootElement.GetProperty("General").GetProperty("Theme").GetString().Should().Be("Light");
    }

    /// <summary>
    /// Проверяет, что отложенное сохранение не оставляет мусорный временный файл
    /// даже при сбое записи (повреждённый путь).
    /// </summary>
    [TestMethod]
    public void SaveSettings_InvalidTargetPath_CleansUpTempFileAndReturnsTypedFailure()
    {
        // Arrange
        string blockedPath = Path.Combine(_tempSettingsDir, "blocked");
        File.WriteAllText(blockedPath, "not a directory");
        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(Path.Combine(blockedPath, "settings"));

        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");

        // Act
        PersistenceResult result = manager.SaveSettings();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Persisted.Should().BeFalse();
        result.ErrorCode.Should().NotBeNullOrEmpty();
        result.UserSummary.Should().NotContain(blockedPath, "пользовательское сообщение не раскрывает путь");
        result.UserSummary.Should().Be(PersistenceSummaries.WriteFailure);
        File.Exists(Path.Combine(blockedPath, "settings", "settings.json.tmp")).Should().BeFalse();
        _logService.EventsById(SettingsEventIds.PersistenceFailed).Should().HaveCountGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Проверяет, что занятый целевой файл даёт типизированную ошибку без ложного успеха:
    /// error-событие с Persisted=false и ни одного подтверждённого settings.changed.
    /// </summary>
    [TestMethod]
    public void SaveSettings_LockedSettingsFile_ReturnsTypedFailureWithoutSuccessEvent()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");
        File.Delete(_tempSettingsFile);
        File.WriteAllText(_tempSettingsFile, "{}");

        int successEventsBefore = _logService.EventsById(SettingsEventIds.Changed).Count;

        PersistenceResult result;
        using (new FileStream(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Act
            result = manager.SaveSettings();

            // Assert
            result.IsFailure.Should().BeTrue();
            result.Persisted.Should().BeFalse();
            result.ErrorCode.Should().BeOneOf(
                PersistenceErrorCodes.FileLocked,
                PersistenceErrorCodes.AccessDenied);
            result.UserSummary.Should().Be(PersistenceSummaries.WriteFailure, "текст исключения не попадает в сообщение");
        }

        IReadOnlyList<RecordedLogEvent> failures = _logService.EventsById(SettingsEventIds.PersistenceFailed);
        failures.Should().HaveCount(1);
        failures[0].GetProperty<bool>("Persisted").Should().BeFalse();
        failures[0].GetProperty<string>("ErrorCode").Should().Be(result.ErrorCode);
        failures[0].Exception.Should().NotBeNull("исключение имеет единственного владельца — событие журнала");
        _logService.EventsById(SettingsEventIds.Changed).Count.Should().Be(
            successEventsBefore,
            "неуспешная запись не подтверждает сохранение");
    }

    /// <summary>
    /// Проверяет, что параллельные записи не теряют ключи: финальный файл содержит все значения.
    /// </summary>
    [TestMethod]
    public void SetSetting_ConcurrentWrites_AllKeysSurviveInFile()
    {
        // Arrange
        var manager = CreateManager();
        const int threadCount = 8;
        const int keysPerThread = 25;

        // Act
        Parallel.For(0, threadCount, thread =>
        {
            for (int index = 0; index < keysPerThread; index++)
            {
                manager.SetSetting("Concurrent", "key_" + thread + "_" + index, index);
            }
        });

        PersistenceResult saveResult = manager.SaveSettings();

        // Assert
        saveResult.IsSuccess.Should().BeTrue();
        var reloaded = CreateManager();
        for (int thread = 0; thread < threadCount; thread++)
        {
            for (int index = 0; index < keysPerThread; index++)
            {
                reloaded.GetSetting("Concurrent", "key_" + thread + "_" + index, -1)
                    .Should().Be(index, "параллельные записи не должны терять ключи");
            }
        }
    }

    /// <summary>
    /// Проверяет дебаунс: серия быстрых изменений даёт ровно одно событие ожидания
    /// и одно подтверждённое событие сохранения пакета.
    /// </summary>
    [TestMethod]
    public void SetSetting_RapidBurst_EmitsSinglePendingBatchAndSinglePersistedEvent()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();

        // Act
        for (int index = 0; index < 9; index++)
        {
            manager.SetSetting("Script_Debounce", "key_" + index, index);
        }

        // Assert — ожидание фиксируется один раз на окно дебаунса
        _logService.EventsById(SettingsEventIds.ChangedPending).Should().HaveCount(1);
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty("запись ещё не подтверждена");

        WaitFor(() => _logService.EventsById(SettingsEventIds.Changed).Count > 0, DebounceWaitMilliseconds);

        IReadOnlyList<RecordedLogEvent> changed = _logService.EventsById(SettingsEventIds.Changed);
        changed.Should().HaveCount(1, "пакет фиксируется одним событием, а не на каждый keystroke");
        changed[0].GetProperty<int>("Count").Should().Be(9);
        changed[0].GetProperty<bool>("Persisted").Should().BeTrue();
        changed[0].Status.Should().Be(LogStatus.Changed);
    }

    /// <summary>
    /// Проверяет порядок событий: сначала одно пакетное ожидание, затем подтверждённое сохранение.
    /// </summary>
    [TestMethod]
    public void SetSetting_DeferredSave_PendingEventPrecedesPersistedEvent()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();
        manager.SetSetting("Script_Order", "alpha", 1);

        // Act
        WaitFor(() => _logService.EventsById(SettingsEventIds.Changed).Count > 0, DebounceWaitMilliseconds);

        // Assert
        List<RecordedLogEvent> all = _logService.Events.ToList();
        int pendingIndex = all.FindIndex(e => e.EventId == SettingsEventIds.ChangedPending);
        int changedIndex = all.FindIndex(e => e.EventId == SettingsEventIds.Changed);

        pendingIndex.Should().BeGreaterThanOrEqualTo(0);
        changedIndex.Should().BeGreaterThan(pendingIndex, "ожидание precedes подтверждённой записи");
        all[changedIndex].GetProperty<string>("Key").Should().Be("alpha");
        all[changedIndex].GetProperty<string>("Group").Should().Be("Script_Order");
    }

    /// <summary>
    /// Проверяет приватность журналирования: сырое значение параметра не попадает
    /// ни в сообщения, ни в свойства событий, только безопасный хэш.
    /// </summary>
    [TestMethod]
    public void SetSetting_SensitiveValue_NeverLogsRawValue()
    {
        // Arrange
        const string secret = "ya29.super-secret-token-value";
        var manager = CreateManager();

        // Act
        manager.SetSetting("Download", "AdditionalArgs", secret);

        // Assert
        _logService.Events.Should().NotBeEmpty();
        foreach (RecordedLogEvent recorded in _logService.Events)
        {
            recorded.Message.Should().NotContain(secret);
            if (recorded.Properties != null)
            {
                foreach (KeyValuePair<string, object?> property in recorded.Properties)
                {
                    property.Value?.ToString().Should().NotContain(secret);
                }
            }
        }

        IReadOnlyList<RecordedLogEvent> changed = _logService.EventsById(SettingsEventIds.Changed);
        changed.Should().HaveCount(1);
        string? valueHash = changed[0].GetProperty<string>("ValueHash");
        valueHash.Should().NotBeNull();
        valueHash.Should().StartWith(PersistenceValueHash.Prefix);
        valueHash.Should().NotContain(secret);
    }

    /// <summary>
    /// Проверяет, что одинаковые значения дают стабильный хэш, а разные — разный.
    /// </summary>
    [TestMethod]
    public void PersistenceValueHash_StableForSameValueAndDistinctForDifferent()
    {
        // Act
        string first = PersistenceValueHash.Compute("payload");
        string second = PersistenceValueHash.Compute("payload");
        string other = PersistenceValueHash.Compute("payload-2");
        string numeric = PersistenceValueHash.Compute(42);
        string numericText = PersistenceValueHash.Compute("42");

        // Assert
        first.Should().Be(second, "хэш обязан быть стабильным между вызовами");
        first.Should().NotBe(other);
        numeric.Should().NotBe(numericText, "тип значения входит в хэш");
        first.Should().HaveLength(PersistenceValueHash.Prefix.Length + 16);
        PersistenceValueHash.Compute(null).Should().NotBe(first);
        PersistenceValueHash.Combine(Array.Empty<KeyValuePair<string, string>>())
            .Should().Be(PersistenceValueHash.EmptyHash);
    }

    /// <summary>
    /// Проверяет, что инициализация настроек по умолчанию подтверждается только после записи на диск
    /// и журналируется как обычное информационное событие, а не предупреждение.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_EmptyCache_PersistsDefaultsWithInfoEvent()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.InitializeDefaults(new List<AbstractScript>());

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue();
        File.Exists(_tempSettingsFile).Should().BeTrue();

        IReadOnlyList<RecordedLogEvent> defaults = _logService.EventsById(SettingsEventIds.DefaultsPersisted);
        defaults.Should().HaveCount(1);
        defaults[0].Level.Should().Be(LogLevel.Info, "нормальная инициализация не должна быть предупреждением");
        defaults[0].GetProperty<bool>("Persisted").Should().BeTrue();

        var reloaded = CreateManager();
        reloaded.Theme.Should().Be("Dark");
    }

    /// <summary>
    /// Проверяет, что сбой записи при инициализации возвращает ошибку и не создаёт событие успеха.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_WriteFailure_ReturnsFailureWithoutDefaultsSuccessEvent()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();

        using (new FileStream(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Act
            PersistenceResult result = manager.InitializeDefaults(new List<AbstractScript>());

            // Assert
            result.IsFailure.Should().BeTrue();
            result.Persisted.Should().BeFalse();
        }

        _logService.EventsById(SettingsEventIds.DefaultsPersisted).Should().BeEmpty();
        _logService.EventsById(SettingsEventIds.PersistenceFailed).Should().NotBeEmpty();
    }

    /// <summary>
    /// Проверяет, что смена LogDir во время сессии не переинициализирует активный каталог журналов,
    /// а применённый каталог подтверждается сверкой с фактическим путём сервиса журналирования.
    /// </summary>
    [TestMethod]
    public void LogDir_ChangedAtRuntime_DoesNotReinitializeLogSession()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"Logging\": { \"LogDir\": \"saved-dir\" } }");
        _logService.EffectiveLogDirectory = "saved-dir";
        var manager = CreateManager();

        // Assert — каталог настроен один раз при загрузке сохранённого значения
        _logService.InitializeLogFileCalls.Should().HaveCount(1);
        _logService.InitializeLogFileCalls[0].Should().Be("saved-dir");
        _logService.EventsById(SettingsEventIds.DirectoryConfigured).Should().HaveCount(1);
        _logService.EventsById(SettingsEventIds.DirectoryConfigured)[0]
            .Status.Should().Be(LogStatus.Succeeded);
        _logService.EventsById(SettingsEventIds.DirectoryConfigured)[0]
            .Message.Should().NotContain("saved-dir", "путь каталога не попадает в журнал");

        // Act
        manager.LogDir = "runtime-dir";

        // Assert
        manager.LogDir.Should().Be("runtime-dir");
        _logService.InitializeLogFileCalls.Should().HaveCount(1, "изменение настройки не переоткрывает сессию журнала");
        _logService.EventsById(SettingsEventIds.DirectoryConfigured).Should().HaveCount(1);
    }

    /// <summary>
    /// Проверяет, что освобождение менеджера отменяет отложенную запись.
    /// </summary>
    [TestMethod]
    public void Dispose_WithPendingSave_CancelsTimerAndSkipsWrite()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();
        manager.SetSetting("Script_Dispose", "crf", 18);

        // Act
        manager.Dispose();
        Thread.Sleep(DebounceSettleMilliseconds);

        // Assert
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty("отложенная запись отменена");
        File.Exists(_tempSettingsTempFile).Should().BeFalse("временный файл удаляется при освобождении");
        File.ReadAllText(_tempSettingsFile).Should().NotContain("crf");
        manager.SetSetting("Script_Dispose", "crf", 19).IsCancelled.Should().BeTrue();
        manager.SaveSettings().IsCancelled.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что отмена фиксируется только когда фактическая запись не выполнялась.
    /// </summary>
    [TestMethod]
    public void SaveSettings_AfterDispose_ReturnsCancelledResultWithoutWrite()
    {
        // Arrange
        var manager = CreateManager();
        manager.Dispose();
        long stampBefore = File.Exists(_tempSettingsFile) ? new FileInfo(_tempSettingsFile).LastWriteTimeUtc.Ticks : 0L;

        // Act
        PersistenceResult result = manager.SaveSettings();

        // Assert
        result.IsCancelled.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.Persisted.Should().BeFalse();
        result.ErrorCode.Should().Be(PersistenceErrorCodes.Disposed);
        long stampAfter = File.Exists(_tempSettingsFile) ? new FileInfo(_tempSettingsFile).LastWriteTimeUtc.Ticks : 0L;
        stampAfter.Should().Be(stampBefore, "отменённая операция не должна писать файл");
    }

    /// <summary>
    /// Проверяет, что повторное значение не создаёт ни записи, ни события изменения.
    /// </summary>
    [TestMethod]
    public void SetSetting_SameValue_ReturnsUnchangedWithoutDiskWrite()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");
        long stamp = new FileInfo(_tempSettingsFile).LastWriteTimeUtc.Ticks;

        // Act
        PersistenceResult result = manager.SetSetting("General", "Theme", "Light");

        // Assert
        result.ChangedCount.Should().Be(0);
        result.HasChanges.Should().BeFalse();
        new FileInfo(_tempSettingsFile).LastWriteTimeUtc.Ticks.Should().Be(stamp, "повторное значение не пишет файл");
    }

    /// <summary>
    /// Проверяет, что быстрые изменения возвращают Pending, а не ложный успех записи.
    /// </summary>
    [TestMethod]
    public void SetSetting_WithinDebounceWindow_ReturnsPendingResult()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();

        // Act
        PersistenceResult result = manager.SetSetting("General", "Theme", "Dark");

        // Assert
        result.IsPending.Should().BeTrue();
        result.IsSuccess.Should().BeFalse("отложенная запись не является подтверждённым успехом");
        result.Persisted.Should().BeFalse();
        result.ChangedKeys.Should().Contain("General/Theme");
    }

    /// <summary>
    /// Проверяет, что пустая группа или ключ отклоняются типизированной ошибкой.
    /// </summary>
    [TestMethod]
    public void SetSetting_EmptyGroupOrKey_ReturnsInvalidArgument()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.SetSetting(string.Empty, "key", 1);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PersistenceErrorCodes.InvalidArgument);
    }

    /// <summary>
    /// Проверяет поведение при каталоге, доступном только для чтения:
    /// запись не подтверждается, временный файл не остаётся, успех не логируется.
    /// </summary>
    [TestMethod]
    public void SaveSettings_TargetPathIsDirectory_ReturnsFailureAndCleansTempFile()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");
        File.Delete(_tempSettingsFile);
        Directory.CreateDirectory(_tempSettingsFile);

        try
        {
            // Act
            PersistenceResult result = manager.SaveSettings();

            // Assert
            result.IsFailure.Should().BeTrue("каталог не может использоваться как settings.json");
            result.Persisted.Should().BeFalse();
            result.ErrorCode.Should().BeOneOf(
                PersistenceErrorCodes.AccessDenied,
                PersistenceErrorCodes.InvalidPath,
                PersistenceErrorCodes.WriteFailed,
                PersistenceErrorCodes.FileLocked);
            File.Exists(_tempSettingsTempFile).Should().BeFalse();
            Directory.Exists(_tempSettingsFile).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(_tempSettingsFile, recursive: true);
        }
    }

    /// <summary>
    /// Проверяет, что GetAllSettingsInGroup не отдаёт значения, которых нет в кэше.
    /// </summary>
    [TestMethod]
    public void GetAllSettingsInGroup_UnknownGroup_ReturnsEmptyDictionary()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");

        // Act
        Dictionary<string, object> values = manager.GetAllSettingsInGroup("Unknown");

        // Assert
        values.Should().BeEmpty();
        manager.GetAllSettingsInGroup("General").Should().ContainKey("Theme");
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        _managers.Add(manager);
        return manager;
    }

    private static void WaitFor(Func<bool> condition, int timeoutMilliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }
    }
}
