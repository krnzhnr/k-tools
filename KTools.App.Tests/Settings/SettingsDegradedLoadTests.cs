// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Тесты деградированной загрузки, честности InitializeDefaults, атомарной фиксации
/// и безопасного освобождения менеджера настроек.
/// </summary>
[TestClass]
public class SettingsDegradedLoadTests
{
    private const int DebounceSettleMilliseconds = 1200;
    private const int PersistGateFieldWaitMilliseconds = 3000;

    private Mock<IPathManager> _pathManagerMock = null!;
    private RecordingLogService _logService = null!;
    private string _tempSettingsDir = null!;
    private string _tempSettingsFile = null!;
    private readonly List<SettingsManager> _managers = new();

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _pathManagerMock = new Mock<IPathManager>();
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Degraded_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");
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
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    /// <summary>
    /// Проверяет, что повреждённый файл сохраняется в резервную копию
    /// с безопасным именем и остаётся на диске без изменений.
    /// </summary>
    [TestMethod]
    public void LoadSettings_CorruptedJson_CreatesBackupAndKeepsOriginalContent()
    {
        // Arrange
        const string corrupted = "{ \"General\": { broken ";
        File.WriteAllText(_tempSettingsFile, corrupted);

        // Act
        var manager = CreateManager();

        // Assert
        string[] backups = Directory.GetFiles(_tempSettingsDir, "settings.json.corrupt-*.bak");
        backups.Should().HaveCount(1, "исходный повреждённый файл сохраняется в резервную копию");
        File.ReadAllText(backups[0]).Should().Be(corrupted, "резервная копия содержит исходное содержимое");
        File.ReadAllText(_tempSettingsFile).Should().Be(corrupted, "повреждённый файл не перезаписывается значениями по умолчанию");
        manager.OverwriteExisting.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет безопасное имя резервной копии: только метка, метка времени и расширение,
    /// без производных от содержимого имён и без абсолютных путей.
    /// </summary>
    [TestMethod]
    public void LoadSettings_CorruptedJson_BackupNameIsSafeAndPathIsNotLogged()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "not json at all");
        var manager = CreateManager();

        // Act & Assert
        string[] backups = Directory.GetFiles(_tempSettingsDir, "*.bak");
        backups.Should().HaveCount(1);
        string name = Path.GetFileName(backups[0]);
        Regex.IsMatch(name, @"^settings\.json\.corrupt-\d{8}-\d{6}(-\d+)?\.bak$")
            .Should().BeTrue("имя резервной копии строится только из безопасных меток");

        IReadOnlyList<RecordedLogEvent> events = _logService.Events;
        events.Should().NotBeEmpty();
        foreach (RecordedLogEvent recorded in events)
        {
            recorded.Message.Should().NotContain(_tempSettingsDir, "путь настроек не попадает в журнал");
            if (recorded.Properties != null)
            {
                foreach (KeyValuePair<string, object?> property in recorded.Properties)
                {
                    property.Value?.ToString().Should().NotContain(_tempSettingsDir);
                }
            }
        }

        IReadOnlyList<RecordedLogEvent> backupsEvents = _logService.EventsById(SettingsEventIds.CorruptBackup);
        backupsEvents.Should().HaveCount(1);
        backupsEvents[0].GetProperty<string>("Reason").Should().Be("corrupt_backup_created");
        backupsEvents[0].GetProperty<bool>("Persisted").Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что InitializeDefaults не записывает значения по умолчанию
    /// поверх деградированной загрузки.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_AfterDegradedLoad_DoesNotOverwriteCorruptFile()
    {
        // Arrange
        const string corrupted = "{ \"General\": ";
        File.WriteAllText(_tempSettingsFile, corrupted);
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.InitializeDefaults(new List<AbstractScript>());

        // Assert
        result.IsFailure.Should().BeTrue("деградированная загрузка не превращается в успешную запись");
        result.Persisted.Should().BeFalse();
        result.IsDegraded.Should().BeTrue();
        result.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        result.UserSummary.Should().Be(PersistenceSummaries.DegradedLoad);
        File.ReadAllText(_tempSettingsFile).Should().Be(corrupted, "повреждённый файл не перезаписан значениями по умолчанию");
        _logService.EventsById(SettingsEventIds.DefaultsPersisted).Should().BeEmpty();
        _logService.EventsById(SettingsEventIds.DefaultsDeferred).Should().HaveCount(1);
    }

    /// <summary>
    /// Проверяет, что единственный сценарий, снимающий защиту, — подтверждённый сброс:
    /// после успешной записи значения по умолчанию снова пишутся на диск.
    /// </summary>
    [TestMethod]
    public void ResetToDefaults_AfterDegradedLoad_PersistsAndClearsGuard()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"General\": broken");
        var manager = CreateManager();

        var defaults = new List<KeyValuePair<string, object?>>
        {
            new("General/Theme", "Dark"),
            new("Shell/IsContextMenuEnabled", false)
        };

        // Act
        PersistenceResult result = manager.ResetToDefaults(new List<AbstractScript>(), defaults);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue("явный сброс подтверждается записью на диск");
        File.ReadAllText(_tempSettingsFile).Should().NotContain("broken");
        manager.GetSetting("Shell", "IsContextMenuEnabled", true).Should().BeFalse();

        PersistenceResult afterReset = manager.SetSetting("General", "Theme", "Light");
        afterReset.ErrorCode.Should().NotBe(PersistenceErrorCodes.DegradedLoad, "защита снята");
        afterReset.HasChanges.Should().BeTrue();
        manager.SaveSettings().Persisted.Should().BeTrue("после подтверждённого сброса запись снова разрешена");
        File.ReadAllText(_tempSettingsFile).Should().Contain("Light");

        // Повторная инициализация по умолчанию больше не блокируется деградацией
        PersistenceResult second = manager.InitializeDefaults(new List<AbstractScript>());
        second.ErrorCode.Should().NotBe(PersistenceErrorCodes.DegradedLoad);
    }

    /// <summary>
    /// Проверяет, что неудачный явный сброс восстанавливает защиту:
    /// следующая обычная запись снова не перезаписывает повреждённый файл.
    /// </summary>
    [TestMethod]
    public void ResetToDefaults_FailedReset_RestoresDegradedGuard()
    {
        // Arrange
        const string corrupt = "{ \"General\": broken";
        File.WriteAllText(_tempSettingsFile, corrupt);
        var manager = CreateManager();

        using (new FileStream(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Act
            PersistenceResult failed = manager.ResetToDefaults(
                new List<AbstractScript>(),
                new List<KeyValuePair<string, object?>> { new("General/Theme", "Dark") });

            // Assert
            failed.IsFailure.Should().BeTrue();
            failed.Persisted.Should().BeFalse();
        }

        // Act — после неудачного сброса обычная запись снова блокируется
        PersistenceResult blocked = manager.SetSetting("General", "Theme", "Light");
        blocked.IsFailure.Should().BeTrue("неудачный сброс не должен снимать защиту");
        blocked.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        File.ReadAllText(_tempSettingsFile).Should().Be(corrupt);
    }

    /// <summary>
    /// Проверяет choke point защиты: после деградированной загрузки первое же
    /// изменение настройки не перезаписывает повреждённый файл.
    /// </summary>
    [TestMethod]
    public void SetSetting_AfterDegradedLoad_IsBlockedAndKeepsCorruptFile()
    {
        // Arrange
        const string corrupt = "{ \"General\": broken";
        File.WriteAllText(_tempSettingsFile, corrupt);
        var manager = CreateManager();
        string[] backupsBefore = Directory.GetFiles(_tempSettingsDir, "*.bak");
        _logService.Clear();

        // Act — первое переключение пользователя
        PersistenceResult first = manager.SetSetting("General", "OverwriteExisting", true);
        PersistenceResult second = manager.SetSetting("General", "Theme", "Light");

        // Assert
        first.IsFailure.Should().BeTrue();
        first.Persisted.Should().BeFalse();
        first.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        first.UserSummary.Should().Be(PersistenceSummaries.DegradedLoadBlocked);
        second.IsFailure.Should().BeTrue();
        File.ReadAllText(_tempSettingsFile).Should().Be(corrupt, "первый toggle не перезаписывает повреждённый файл");

        string[] backupsAfter = Directory.GetFiles(_tempSettingsDir, "*.bak");
        backupsAfter.Should().BeEquivalentTo(backupsBefore, "резервная копия оригинала не перезаписывается");
        backupsAfter.Should().ContainSingle();
        File.ReadAllText(backupsAfter[0]).Should().Be(corrupt);
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что ни переключение пользователя, ни закрытие сеанса
    /// не перезаписывают повреждённый файл: оригинал остаётся только в резервной копии.
    /// </summary>
    [TestMethod]
    public void ToggleAndClose_AfterDegradedLoad_NeverOverwriteCorruptFile()
    {
        // Arrange
        const string corrupt = "{ \"Logging\": broken";
        File.WriteAllText(_tempSettingsFile, corrupt);
        var manager = CreateManager();
        string[] backupsBefore = Directory.GetFiles(_tempSettingsDir, "*.bak");

        // Act
        PersistenceResult toggle = manager.SetSetting("Logging", "ShowLogsTab", true);
        Thread.Sleep(DebounceSettleMilliseconds);
        PersistenceResult close = manager.SaveSettings();

        // Assert
        toggle.IsFailure.Should().BeTrue();
        toggle.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        close.IsFailure.Should().BeTrue();
        close.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        File.ReadAllText(_tempSettingsFile).Should().Be(corrupt, "ни toggle, ни закрытие сеанса не перезаписывают файл");
        File.ReadAllText(backupsBefore[0]).Should().Be(corrupt, "оригинал сохранён в резервной копии");
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что SaveSettings после деградированной загрузки возвращает
    /// типизированный отказ и не уничтожает оригинал.
    /// </summary>
    [TestMethod]
    public void SaveSettings_AfterDegradedLoad_DoesNotOverwriteCorruptFile()
    {
        // Arrange
        const string corrupt = "{ \"General\": ";
        File.WriteAllText(_tempSettingsFile, corrupt);
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");

        // Act
        PersistenceResult result = manager.SaveSettings();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Persisted.Should().BeFalse();
        result.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        result.IsDegraded.Should().BeTrue();
        File.ReadAllText(_tempSettingsFile).Should().Be(corrupt);
        File.Exists(_tempSettingsFile + ".tmp").Should().BeFalse("временный файл не создаётся при блокировке записи");
    }

    /// <summary>
    /// Проверяет, что ветка «без изменений» не заявляет persisted=true,
    /// когда в кэше остались несохранённые или отложенные изменения.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_NoSchemaChangesButDirtyCache_ReturnsPendingNotPersisted()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();
        manager.InitializeDefaults(new List<AbstractScript>());

        // Блокируем файл, чтобы отложенная пакетная запись не могла завершиться успехом
        using FileStream locked = new(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        manager.SetSetting("Script_Dirty", "crf", 18);
        PersistenceResult result = manager.InitializeDefaults(new List<AbstractScript>());

        // Assert
        result.Persisted.Should().BeFalse("диск не совпадает с кэшем — persisted был бы ложью");
        result.IsSuccess.Should().BeFalse();
        result.ChangedKeys.Should().Contain("Script_Dirty/crf");
    }

    /// <summary>
    /// Проверяет, что согласованный кэш честно сообщает подтверждённую запись.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_CleanCacheAfterPersist_ReportsPersisted()
    {
        // Arrange
        var manager = CreateManager();
        manager.InitializeDefaults(new List<AbstractScript>());

        // Act
        PersistenceResult result = manager.InitializeDefaults(new List<AbstractScript>());

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue();
        result.Source.Should().Be(PersistenceSource.Disk);
    }

    /// <summary>
    /// Проверяет, что перечитывание файла отменяет отложенную запись,
    /// а отменённые изменения фиксируются в журнале как потерянные.
    /// </summary>
    [TestMethod]
    public void LoadSettings_WithPendingChanges_CancelsTimerAndReportsDiscardedCount()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();
        manager.SetSetting("Script_Reload", "alpha", 1);
        _logService.Clear();

        // Act
        PersistenceResult result = manager.LoadSettings();
        Thread.Sleep(DebounceSettleMilliseconds);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _logService.EventsById(SettingsEventIds.PendingDiscarded).Should().HaveCount(1);
        _logService.EventsById(SettingsEventIds.PendingDiscarded)[0]
            .GetProperty<int>("Count").Should().Be(1);
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty(
            "отложенная запись отменена перечитыванием и не выполняется позже по таймеру");
    }

    /// <summary>
    /// Проверяет, что неуспешный пакет остаётся повторяемым: следующая фиксация
    /// записывает и ранее не сохранённые ключи.
    /// </summary>
    [TestMethod]
    public void SetSetting_FailedDebouncedBatch_RemainsRetryable()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();
        manager.SetSetting("Script_Retry", "alpha", 1);
        manager.SaveSettings();

        FileStream? blocker = new(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // Act — пакетная запись падает
            manager.SetSetting("Script_Retry", "beta", 2);
            PersistenceResult failed = manager.SaveSettings();
            failed.IsFailure.Should().BeTrue();

            // Assert — изменения не выброшены из кэша
            manager.GetSetting("Script_Retry", "beta", 0).Should().Be(2);
        }
        finally
        {
            blocker.Dispose();
        }

        // Act — повторная попытка после снятия блокировки
        PersistenceResult retry = manager.SaveSettings();

        // Assert
        retry.IsSuccess.Should().BeTrue("неуспешный пакет повторяем после устранения причины");
        retry.Persisted.Should().BeTrue();
        var reloaded = CreateManager();
        reloaded.GetSetting("Script_Retry", "beta", 0).Should().Be(2);
    }

    /// <summary>
    /// Проверяет, что неатомарная копировальная фиксация удалена из менеджера,
    /// а успешная запись подтверждается только атомарными стратегиями.
    /// </summary>
    [TestMethod]
    public void Commit_UsesOnlyAtomicStrategies_NoCopyFallbackRemains()
    {
        // Assert
        typeof(SettingsManager)
            .GetMethod("TryCommitWithCopy", BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().BeNull("неатомарный File.Copy+Delete больше не является стратегией фиксации");
        typeof(SettingsManager)
            .GetMethod("TryCommitWithReplace", BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().NotBeNull();
    }

    /// <summary>
    /// Проверяет, что при невозможности атомарной фиксации результат не сообщает
    /// о записи, а временный файл не остаётся на диске.
    /// </summary>
    [TestMethod]
    public void SaveSettings_BothAtomicStrategiesFail_ReturnsFailureWithoutTempLeftover()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");
        manager.SaveSettings();
        manager.SetSetting("General", "Theme", "Dark");
        _logService.Clear();

        // Act
        PersistenceResult result;
        using (new FileStream(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = manager.SaveSettings();
        }

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Persisted.Should().BeFalse("успешная деградация не может быть отчитана как запись");
        result.IsDegraded.Should().BeFalse("деградации нет: операция просто провалилась");
        File.Exists(_tempSettingsFile + ".tmp").Should().BeFalse();
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что установка null по отсутствующему ключу даёт типизированную
    /// ошибку, а не ложное «без изменений».
    /// </summary>
    [TestMethod]
    public void SetSetting_NullForMissingKey_ReturnsTypedNoResult()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.SetSetting("General", "NotExisting", (string?)null);

        // Assert
        result.IsFailure.Should().BeTrue("null по отсутствующему ключу — это отказ, а не отсутствие изменений");
        result.ErrorCode.Should().Be(PersistenceErrorCodes.NoResult);
        result.ChangedCount.Should().Be(0);
    }

    /// <summary>
    /// Проверяет, что удаление существующего ключа через null остаётся поддержанным.
    /// </summary>
    [TestMethod]
    public void SetSetting_NullForExistingKey_RemovesValue()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Removable", "value");
        manager.SaveSettings();

        // Act
        PersistenceResult result = manager.SetSetting("General", "Removable", (string?)null);
        manager.SaveSettings();

        // Assert
        result.HasChanges.Should().BeTrue();
        var reloaded = CreateManager();
        reloaded.GetSetting("General", "Removable", "default").Should().Be("default");
    }

    /// <summary>
    /// Проверяет, что Dispose ждёт завершения уже начавшейся записи
    /// и только затем освобождает внутренние блокировки.
    /// </summary>
    [TestMethod]
    public void Dispose_WithPersistInFlight_WaitsForGateBeforeReleasingLock()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");
        manager.SaveSettings();

        FieldInfo gate = typeof(SettingsManager)
            .GetField("_persistInFlight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        gate.SetValue(manager, 1);

        using var completed = new ManualResetEventSlim(false);
        Task dispose = Task.Run(() =>
        {
            manager.Dispose();
            completed.Set();
        });

        // Assert
        completed.Wait(PersistGateFieldWaitMilliseconds).Should().BeFalse(
            "освобождение не должно завершаться, пока идёт запись");

        // Act
        gate.SetValue(manager, 0);
        completed.Wait(PersistGateFieldWaitMilliseconds).Should().BeTrue();

        // Assert
        dispose.IsCompletedSuccessfully.Should().BeTrue();
        File.Exists(_tempSettingsFile + ".tmp").Should().BeFalse("после освобождения временный файл не остаётся");
        manager.SaveSettings().ErrorCode.Should().Be(PersistenceErrorCodes.Disposed);
    }

    /// <summary>
    /// Проверяет, что конкурентные запись и освобождение не приводят
    /// к исключениям, остаточным временным файлам или записи после освобождения.
    /// </summary>
    [TestMethod]
    public void Dispose_ConcurrentWithSetSetting_LeavesNoTempFileAndNoWriteAfterDispose()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting("General", "Theme", "Light");
        manager.SaveSettings();
        _logService.Clear();
        long stampAfterSetup = new FileInfo(_tempSettingsFile).LastWriteTimeUtc.Ticks;

        // Act
        Task writer = Task.Run(() =>
        {
            for (int index = 0; index < 200; index++)
            {
                try
                {
                    manager.SetSetting("Script_Race", "value_" + index, index);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });

        Thread.Sleep(5);
        manager.Dispose();
        long stampAfterDispose = new FileInfo(_tempSettingsFile).LastWriteTimeUtc.Ticks;
        Thread.Sleep(DebounceSettleMilliseconds);
        long stampAfterSettle = new FileInfo(_tempSettingsFile).LastWriteTimeUtc.Ticks;
        writer.Wait(TimeSpan.FromSeconds(30));

        // Assert
        File.Exists(_tempSettingsFile + ".tmp").Should().BeFalse("временный файл не остаётся после освобождения");
        stampAfterSettle.Should().Be(stampAfterDispose, "после освобождения файл не переписывается");
        stampAfterDispose.Should().BeGreaterThanOrEqualTo(stampAfterSetup);
    }

    /// <summary>
    /// Проверяет, что повторное освобождение безопасно и не приводит к исключению.
    /// </summary>
    [TestMethod]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        Action dispose = () =>
        {
            manager.Dispose();
            manager.Dispose();
        };

        // Assert
        dispose.Should().NotThrow();
    }

    /// <summary>
    /// Проверяет, что запрошенный каталог журналирования сравнивается с фактическим
    /// и событие не сообщает об успехе, когда каталог не применён.
    /// </summary>
    [TestMethod]
    public void Constructor_LogDirNotApplied_ReportsFailureNotSuccess()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"Logging\": { \"LogDir\": \"D:\\\\ktools-logs\" } }");
        _logService.EffectiveLogDirectory = "C:\\default-logs";

        // Act
        CreateManager();

        // Assert
        IReadOnlyList<RecordedLogEvent> events = _logService.EventsById(SettingsEventIds.DirectoryConfigured);
        events.Should().HaveCount(1);
        events[0].Status.Should().Be(LogStatus.Failed, "неприменённый каталог не может быть успехом");
        events[0].GetProperty<bool>("Succeeded").Should().BeFalse();
        events[0].GetProperty<string>("Status").Should().Be("Failed");
        events[0].GetProperty<string>("Reason").Should().Be("log_dir_not_applied");
        events[0].Message.Should().NotContain("ktools-logs");
        events[0].Message.Should().NotContain("C:\\");
    }

    /// <summary>
    /// Проверяет, что событие загрузки не пишется до выбора каталога журналирования.
    /// </summary>
    [TestMethod]
    public void Constructor_BuffersLoadEventUntilLogDirectorySelected()
    {
        // Arrange & Act
        _logService.EffectiveLogDirectory = "C:\\default-logs";
        CreateManager();

        // Assert
        List<RecordedLogEvent> events = _logService.Events.ToList();
        int configuredIndex = events.FindIndex(e => e.EventId == SettingsEventIds.DirectoryConfigured);
        int loadIndex = events.FindIndex(e => e.EventId == SettingsEventIds.LoadCompleted);
        configuredIndex.Should().BeGreaterThanOrEqualTo(0);
        loadIndex.Should().BeGreaterThan(configuredIndex, "событие загрузки буферизуется до выбора каталога");
    }

    /// <summary>
    /// Проверяет, что даже при повреждённом файле ни одна запись в журнал
    /// не выполняется до выбора каталога журналирования.
    /// </summary>
    [TestMethod]
    public void Constructor_CorruptedSettings_WritesNoLogEntryBeforeLogDirectorySelection()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"General\": ");

        // Act
        _logService.EffectiveLogDirectory = "C:\\default-logs";
        CreateManager();

        // Assert
        IReadOnlyList<string> order = _logService.CallOrder;
        order.Should().NotBeEmpty();
        int initIndex = order.ToList().FindIndex(entry => entry.StartsWith("init:", StringComparison.Ordinal));
        initIndex.Should().Be(0, "первым вызовом всегда является выбор каталога журналирования");
        order.Should().OnlyContain(entry =>
            entry.StartsWith("init:", StringComparison.Ordinal) || entry.StartsWith("event:", StringComparison.Ordinal));
        _logService.EventsById(SettingsEventIds.CorruptBackup).Should().HaveCount(1);
    }

    /// <summary>
    /// Проверяет, что применённый каталог журналирования подтверждается успехом
    /// и в журнал не попадает сам путь.
    /// </summary>
    [TestMethod]
    public void Constructor_LogDirApplied_ReportsSuccessWithoutRawPath()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"Logging\": { \"LogDir\": \"D:\\\\ktools-logs\" } }");
        _logService.EffectiveLogDirectory = "D:\\ktools-logs";

        // Act
        CreateManager();

        // Assert
        IReadOnlyList<RecordedLogEvent> events = _logService.EventsById(SettingsEventIds.DirectoryConfigured);
        events.Should().HaveCount(1);
        events[0].Status.Should().Be(LogStatus.Succeeded);
        events[0].GetProperty<bool>("Succeeded").Should().BeTrue();
        events[0].Message.Should().NotContain("ktools-logs");
        events[0].GetProperty<string>("ValueHash")!.StartsWith(PersistenceValueHash.Prefix, StringComparison.Ordinal)
            .Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что пустой запрос каталога (режим по умолчанию) считается применённым,
    /// когда сервис журналирования сообщил фактический каталог.
    /// </summary>
    [TestMethod]
    public void Constructor_DefaultLogDirectory_ReportsSuccess()
    {
        // Arrange & Act
        _logService.EffectiveLogDirectory = "C:\\default-logs";
        CreateManager();

        // Assert
        IReadOnlyList<RecordedLogEvent> events = _logService.EventsById(SettingsEventIds.DirectoryConfigured);
        events.Should().HaveCount(1);
        events[0].Status.Should().Be(LogStatus.Succeeded);
        events[0].GetProperty<string>("Reason").Should().Be("configured");
    }

    /// <summary>
    /// Проверяет, что резервная копия создаётся только при повреждении содержимого:
    /// транзиентная занятость файла не порождает ни копии, ни сообщения о копии.
    /// </summary>
    [TestMethod]
    public void LoadSettings_LockedSettingsFile_CreatesNoBackupAndNoBackupMessage()
    {
        // Arrange
        const string valid = "{ \"General\": { \"Theme\": \"Light\" } }";
        File.WriteAllText(_tempSettingsFile, valid);

        using (new FileStream(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Act
            CreateManager();

            // Assert
            _logService.EventsById(SettingsEventIds.CorruptBackup).Should().BeEmpty(
                "сообщение о резервной копии не выдаётся, если копии нет");
            _logService.EventsById(SettingsEventIds.LoadDegraded).Should().HaveCount(1);
        }

        Directory.GetFiles(_tempSettingsDir, "*.bak").Should().BeEmpty(
            "транзиентная занятость файла не требует резервной копии");
        File.ReadAllText(_tempSettingsFile).Should().Be(valid);
    }

    /// <summary>
    /// Проверяет, что занятый файл тоже включает защиту от перезаписи:
    /// данные не потеряны молча, а запись не выполняется поверх исходника.
    /// </summary>
    [TestMethod]
    public void LoadSettings_LockedSettingsFile_StillGuardsWrites()
    {
        // Arrange
        const string valid = "{ \"General\": { \"Theme\": \"Light\" } }";
        File.WriteAllText(_tempSettingsFile, valid);

        PersistenceResult firstToggle;
        using (new FileStream(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var manager = CreateManager();
            firstToggle = manager.SetSetting("General", "Theme", "Dark");
        }

        // Assert
        firstToggle.IsFailure.Should().BeTrue();
        firstToggle.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        File.ReadAllText(_tempSettingsFile).Should().Be(valid, "исходный файл не перезаписан");
    }

    /// <summary>
    /// Проверяет ограниченность накопления резервных копий по количеству.
    /// </summary>
    [TestMethod]
    public void LoadSettings_RepeatedCorruption_PrunesBackupRetention()
    {
        // Arrange
        const string corrupt = "{ \"General\": broken";
        File.WriteAllText(_tempSettingsFile, corrupt);
        var manager = CreateManager();

        // Act
        for (int attempt = 0; attempt < 12; attempt++)
        {
            manager.LoadSettings();
        }

        // Assert
        string[] backups = Directory.GetFiles(_tempSettingsDir, "*.bak");
        backups.Should().HaveCount(5, "количество резервных копий ограничено");
        foreach (string backup in backups)
        {
            File.ReadAllText(backup).Should().Be(corrupt);
        }
    }

    /// <summary>
    /// Проверяет, что слишком старые резервные копии удаляются политикой хранения.
    /// </summary>
    [TestMethod]
    public void LoadSettings_Corruption_PrunesBackupsOlderThanRetentionAge()
    {
        // Arrange
        const string corrupt = "{ \"General\": broken";
        File.WriteAllText(_tempSettingsFile, corrupt);
        var manager = CreateManager();
        manager.LoadSettings();

        string[] backups = Directory.GetFiles(_tempSettingsDir, "*.bak");
        backups.Should().NotBeEmpty();
        foreach (string backup in backups)
        {
            File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddDays(-CorruptBackupRetentionDaysProbe));
        }

        // Act
        manager.LoadSettings();

        // Assert
        string[] remaining = Directory.GetFiles(_tempSettingsDir, "*.bak");
        remaining.Should().NotBeEmpty("свежая копия остаётся");
        remaining.Should().HaveCount(1, "устаревшие копии вытесняются политикой хранения");
    }

    private const int CorruptBackupRetentionDaysProbe = 90;

    /// <summary>
    /// Проверяет, что Pending действительно означает взведённый таймер:
    /// отложенная запись реально происходит в пределах окна дебаунса.
    /// </summary>
    [TestMethod]
    public void SetSetting_PendingResult_ImpliesArmedDeferredWrite()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();

        // Act
        PersistenceResult result = manager.SetSetting("General", "Theme", "Dark");
        WaitFor(() => _logService.EventsById(SettingsEventIds.Changed).Count > 0, 5000);

        // Assert
        result.IsPending.Should().BeTrue();
        _logService.EventsById(SettingsEventIds.Changed).Should().HaveCount(1,
            "Pending обещает ровно одну отложенную запись");
        _logService.EventsById(SettingsEventIds.Changed)[0].GetProperty<bool>("Persisted").Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что таймер не взводится после освобождения менеджера:
    /// обещанной отложенной записи не будет, поэтому результат не может быть Pending.
    /// </summary>
    [TestMethod]
    public void ScheduleDeferredSave_AfterDispose_DoesNotArmTimer()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();
        manager.Dispose();

        MethodInfo schedule = typeof(SettingsManager)
            .GetMethod("ScheduleDeferredSave", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Act
        bool armed = (bool)schedule.Invoke(manager, null)!;

        // Assert
        armed.Should().BeFalse("после освобождения отложенная запись невозможна");
        manager.SetSetting("General", "Theme", "Light").IsCancelled.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что неуспешный отложенный пакет повторяется один раз с ограниченной задержкой.
    /// </summary>
    [TestMethod]
    public void SetSetting_FailedDebouncedBatch_RetriesOnceAfterBackoff()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();

        FileStream? blocker = new(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // Act — первая пакетная запись падает
            manager.SetSetting("Script_RetryBackoff", "alpha", 1);
            WaitFor(() => _logService.EventsById(SettingsEventIds.PersistenceFailed).Count > 0, 5000);
        }
        finally
        {
            blocker.Dispose();
        }

        // Act — причина устранена, повтор должен записать пакет
        WaitFor(() => _logService.EventsById(SettingsEventIds.Changed).Count > 0, 8000);

        // Assert
        _logService.EventsById(SettingsEventIds.Changed).Should().NotBeEmpty(
            "неуспешный отложенный пакет повторяется после устранения причины");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_tempSettingsFile));
        document.RootElement.GetProperty("Script_RetryBackoff").GetProperty("alpha").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что повтор не превращается в бесконечный цикл записи:
    /// при постоянной недоступности файла подтверждённых записей не больше одной.
    /// </summary>
    [TestMethod]
    public void SetSetting_PersistentlyFailingBatch_RetriesOnlyOnce()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();

        using FileStream blocker = new(_tempSettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        manager.SetSetting("Script_NoLoop", "alpha", 1);
        Thread.Sleep(DebounceSettleMilliseconds * 5);

        // Assert
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty("подтверждённых записей нет");
        _logService.EventsById(SettingsEventIds.PersistenceFailed).Count
            .Should().BeLessThanOrEqualTo(2, "повтор ограничен одной попыткой");
    }

    /// <summary>
    /// Проверяет, что редактирование настройки даёт ровно одну запись на диск:
    /// подтверждённых событий сохранения столько же, сколько правок.
    /// </summary>
    [TestMethod]
    public void SetSetting_SingleEdit_PerformsSingleDiskWrite()
    {
        // Arrange
        var manager = CreateManager();
        manager.SaveSettings();
        _logService.Clear();

        // Act
        manager.SetSetting("General", "SearchTemplates", new List<TemplateItem>
        {
            new() { Pattern = "one", Description = "первый" }
        });
        WaitFor(() => _logService.EventsById(SettingsEventIds.Changed).Count > 0, 5000);

        // Assert
        _logService.EventsById(SettingsEventIds.Changed).Should().HaveCount(1,
            "одна правка не должна вызывать повторную запись всего файла");
        _logService.EventsById(SettingsEventIds.Changed)[0].GetProperty<int>("Count").Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что одинаковая по содержанию правка не порождает второй записи на диск.
    /// </summary>
    [TestMethod]
    public void SetSetting_RepeatedIdenticalEdit_PerformsSingleDiskWrite()
    {
        // Arrange
        var manager = CreateManager();
        var templates = new List<TemplateItem> { new() { Pattern = "same", Description = "одинаково" } };
        manager.SetSetting("General", "SearchTemplates", templates);
        manager.SaveSettings();
        _logService.Clear();

        // Act
        PersistenceResult first = manager.SetSetting("General", "SearchTemplates", templates);
        PersistenceResult second = manager.SetSetting(
            "General",
            "SearchTemplates",
            new List<TemplateItem> { new() { Pattern = "same", Description = "одинаково" } });

        // Assert
        first.ChangedCount.Should().Be(0);
        second.ChangedCount.Should().Be(0);
        _logService.EventsById(SettingsEventIds.Changed).Should().BeEmpty("повторное значение не пишет файл");
    }

    /// <summary>
    /// Проверяет, что большие значения, различающиеся за пределами усечённого снимка,
    /// никогда не признаются неизменными.
    /// </summary>
    [TestMethod]
    public void AreEqual_TruncatedSnapshot_FailsClosed()
    {
        // Arrange — два больших списка, различающихся за пределами окна усечения
        var left = new List<string>();
        var right = new List<string>();
        for (int index = 0; index < 20000; index++)
        {
            left.Add("item_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            right.Add("item_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        right[^1] = "changed-tail";

        // Act
        bool equal = SettingsValueSnapshot.AreEqual(left, right);
        bool described = SettingsValueSnapshot.TryDescribe(left, out string? text, out bool truncated);

        // Assert
        described.Should().BeFalse("снимок усечён границами и не может использоваться для сравнения");
        truncated.Should().BeTrue();
        text!.Length.Should().BeLessThanOrEqualTo(SettingsValueSnapshot.MaxCanonicalLength + 4096);
        equal.Should().BeFalse("два разных больших значения не могут дать «без изменений»");
    }

    /// <summary>
    /// Проверяет, что одинаковые большие значения трактуются консервативно:
    /// fail-closed возвращает «изменено», что безопасно и не теряет правку.
    /// </summary>
    [TestMethod]
    public void AreEqual_IdenticalOversizedValues_AreReportedAsChanged()
    {
        // Arrange
        var left = new List<string>();
        var right = new List<string>();
        for (int index = 0; index < 20000; index++)
        {
            string value = "item_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            left.Add(value);
            right.Add(value);
        }

        // Act & Assert
        SettingsValueSnapshot.AreEqual(left, right).Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что большая строка сверх границ не даёт ложного совпадения.
    /// </summary>
    [TestMethod]
    public void AreEqual_OversizedStringsNeverCompareEqual()
    {
        // Arrange
        string left = new string('a', SettingsValueSnapshot.MaxCanonicalLength + 10);
        string right = new string('a', SettingsValueSnapshot.MaxCanonicalLength + 10) + "b";

        // Act & Assert
        SettingsValueSnapshot.TryDescribe(left, out _, out bool truncated).Should().BeFalse();
        truncated.Should().BeTrue();
        SettingsValueSnapshot.AreEqual(left, right).Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что значения в пределах границ по-прежнему сравниваются точно.
    /// </summary>
    [TestMethod]
    public void AreEqual_ValuesWithinBounds_StillCompareExactly()
    {
        // Arrange
        var left = new List<TemplateItem> { new() { Pattern = "a", Description = "1" } };
        var same = new List<TemplateItem> { new() { Pattern = "a", Description = "1" } };
        var other = new List<TemplateItem> { new() { Pattern = "b", Description = "1" } };

        // Act & Assert
        SettingsValueSnapshot.TryDescribe(left, out _, out bool truncated).Should().BeTrue();
        truncated.Should().BeFalse();
        SettingsValueSnapshot.AreEqual(left, same).Should().BeTrue();
        SettingsValueSnapshot.AreEqual(left, other).Should().BeFalse();
    }

    private static void WaitFor(Func<bool> condition, int timeoutMilliseconds)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        _managers.Add(manager);
        return manager;
    }
}
