// -*- coding: utf-8 -*-
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Менеджер пользовательских настроек приложения K-Tools.
/// Сохраняет все параметры программы и настроек скриптов в локальный файл JSON.
/// Полностью потокобезопасен, поддерживает горячую синхронизацию и атомарную запись
/// с типизированным результатом операции (см. <see cref="PersistenceResult"/>).
/// </summary>
public sealed class SettingsManager : ISettingsManager
{
    private const int SaveDebounceMilliseconds = 300;
    private const int FailedBatchRetryDelayMilliseconds = 1000;
    private const int MaxFailedBatchRetries = 1;
    private const int MaxSettingsFileBytes = 8 * 1024 * 1024;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const string SettingsFileName = "settings.json";
    private const string TempFileSuffix = ".tmp";
    private const string CorruptBackupInfix = ".corrupt-";
    private const string CorruptBackupSuffix = ".bak";
    private const int CorruptBackupNameAttempts = 8;
    private const int CorruptBackupRetentionCount = 5;
    private const int CorruptBackupMaxAgeDays = 30;
    private const long CorruptBackupMaxTotalBytes = 4L * 1024 * 1024;
    private const int PersistQuiescencePollMilliseconds = 25;
    private const int PersistQuiescenceTimeoutMilliseconds = 5000;

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly object _saveGate = new();
    private readonly object _saveTimerLock = new();
    private readonly string _settingsFilePath;
    private readonly string _settingsTempFilePath;
    private readonly Dictionary<string, Dictionary<string, object>> _cache = new();
    private readonly Dictionary<string, string> _pendingChanges = new(StringComparer.Ordinal);

    private Timer? _saveTimer;
    private bool _pendingBatchReported;
    private long _lastDiskSaveTick;
    private int _disposed;
    private int _degradedLoad;
    private int _persistInFlight;
    private int _resetWriteAuthorized;
    private int _failedBatchRetries;
    private long _cacheVersion;
    private long _persistedVersion;

    /// <summary>
    /// Инициализирует новый экземпляр класса SettingsManager с внедрением зависимостей.
    /// </summary>
    public SettingsManager(ILogService logService, IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));

        string settingsDir = _pathManager.GetSettingsDirectory();
        _settingsFilePath = Path.Combine(settingsDir, SettingsFileName);
        _settingsTempFilePath = _settingsFilePath + TempFileSuffix;
        _lastDiskSaveTick = Environment.TickCount64 - SaveDebounceMilliseconds;

        TryDeleteTempFile();

        // События загрузки буферизуются: ни одна запись в журнал не выполняется
        // до выбора каталога журналирования, иначе часть события ушла бы в каталог
        // по умолчанию вместо каталога, выбранного пользователем.
        PersistenceResult loadResult = LoadSettingsCore(out bool corruptBackupCreated);

        string configuredLogDir = LogDir;
        _logService.InitializeLogFile(configuredLogDir);
        ReportDirectoryConfigured(configuredLogDir);
        ReportCorruptBackup(corruptBackupCreated);
        ReportLoadOutcome(loadResult);
    }

    /// <summary>
    /// Перезаписывать ли существующие файлы.
    /// </summary>
    public bool OverwriteExisting
    {
        get => GetSetting("General", "OverwriteExisting", false);
        set => SetSetting("General", "OverwriteExisting", value);
    }

    /// <summary>
    /// Имя подпапки для результатов по умолчанию.
    /// </summary>
    public string DefaultOutputSubfolder
    {
        get => GetSetting("General", "DefaultOutputSubfolder", "KTools_Result");
        set => SetSetting("General", "DefaultOutputSubfolder", value);
    }

    /// <summary>
    /// Использовать ли автоматическое создание подпапки.
    /// </summary>
    public bool UseAutoSubfolder
    {
        get => GetSetting("General", "UseAutoSubfolder", false);
        set => SetSetting("General", "UseAutoSubfolder", value);
    }

    /// <summary>
    /// Тема оформления интерфейса.
    /// </summary>
    public string Theme
    {
        get => GetSetting("General", "Theme", "Dark");
        set => SetSetting("General", "Theme", value);
    }

    /// <summary>
    /// Тип фона окон приложения (Mica или Acrylic).
    /// </summary>
    public string BackdropType
    {
        get => GetSetting("General", "BackdropType", "Mica");
        set => SetSetting("General", "BackdropType", value);
    }

    /// <summary>
    /// Максимальное количество параллельных задач обработки.
    /// </summary>
    public int MaxParallelTasks
    {
        get => GetSetting("General", "MaxParallelTasks", Math.Max(1, Environment.ProcessorCount / 2));
        set => SetSetting("General", "MaxParallelTasks", value);
    }

    /// <summary>
    /// Разрешить ли параллельное выполнение задач обработки.
    /// </summary>
    public bool EnableParallel
    {
        get => GetSetting("General", "EnableParallel", true);
        set => SetSetting("General", "EnableParallel", value);
    }

    /// <summary>
    /// Очищать ли очередь перед добавлением новых файлов.
    /// </summary>
    public bool ClearListOnAdd
    {
        get => GetSetting("General", "ClearListOnAdd", false);
        set => SetSetting("General", "ClearListOnAdd", value);
    }

    /// <summary>
    /// Отображать ли монитор логов (вкладку).
    /// </summary>
    public bool ShowLogsTab
    {
        get => GetSetting("Logging", "ShowLogsTab", false);
        set => SetSetting("Logging", "ShowLogsTab", value);
    }

    /// <summary>
    /// Пользовательский путь к директории хранения логов.
    /// Установка значения только меняет настройку: уже запущенная сессия
    /// журналирования не переинициализируется.
    /// </summary>
    public string LogDir
    {
        get => GetSetting("Logging", "LogDir", string.Empty);
        set => SetLogDirectory(value);
    }

    /// <summary>
    /// Автоматически проверять обновления при старте.
    /// </summary>
    public bool AutoCheckUpdates
    {
        get => GetSetting("Updates", "AutoCheckUpdates", true);
        set => SetSetting("Updates", "AutoCheckUpdates", value);
    }

    /// <summary>
    /// Определяет, является ли текущая сборка пре-релизом (Preview/Alpha/Beta/RC).
    /// </summary>
    public static bool IsPreviewBuild
    {
        get
        {
            var infoVer = typeof(SettingsManager).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

            return infoVer.Contains("-preview", StringComparison.OrdinalIgnoreCase) ||
                   infoVer.Contains("-alpha", StringComparison.OrdinalIgnoreCase) ||
                   infoVer.Contains("-beta", StringComparison.OrdinalIgnoreCase) ||
                   infoVer.Contains("-rc", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Включать ли бета-версии при поиске обновлений.
    /// </summary>
    public bool IncludePreReleases
    {
        get => GetSetting("Updates", "IncludePreReleases", IsPreviewBuild);
        set => SetSetting("Updates", "IncludePreReleases", value);
    }

    /// <summary>
    /// Имитировать ли старую версию при проверке обновлений.
    /// </summary>
    public bool DebugSimulateOldVersion
    {
        get => GetSetting("Debug", "DebugSimulateOldVersion", false);
        set => SetSetting("Debug", "DebugSimulateOldVersion", value);
    }

    /// <summary>
    /// Отключать ли действие кнопок обновления и скачивания (имитация пустышек).
    /// </summary>
    public bool DebugDisableUpdateAction
    {
        get => GetSetting("Debug", "DebugDisableUpdateAction", false);
        set => SetSetting("Debug", "DebugDisableUpdateAction", value);
    }

    /// <summary>
    /// Флаг включения переименования выходных файлов по регулярным выражениям (Regex).
    /// </summary>
    public bool RenameEnableRegex
    {
        get => GetSetting("General", "RenameEnableRegex", false);
        set => SetSetting("General", "RenameEnableRegex", value);
    }

    /// <summary>
    /// Шаблон поиска (регулярное выражение) для переименования выходных файлов.
    /// </summary>
    public string RenameRegexSearch
    {
        get => GetSetting("General", "RenameRegexSearch", string.Empty);
        set => SetSetting("General", "RenameRegexSearch", value);
    }

    /// <summary>
    /// Строка замены для переименования выходных файлов.
    /// </summary>
    public string RenameRegexReplace
    {
        get => GetSetting("General", "RenameRegexReplace", string.Empty);
        set => SetSetting("General", "RenameRegexReplace", value);
    }

    /// <summary>
    /// Пользовательские шаблоны для поиска.
    /// </summary>
    public List<TemplateItem> SearchTemplates
    {
        get => GetSetting("General", "SearchTemplates", SettingsDefaults.GetDefaultSearchTemplates());
        set => SetSetting("General", "SearchTemplates", value);
    }

    /// <summary>
    /// Пользовательские шаблоны для замены.
    /// </summary>
    public List<TemplateItem> ReplaceTemplates
    {
        get => GetSetting("General", "ReplaceTemplates", SettingsDefaults.GetDefaultReplaceTemplates());
        set => SetSetting("General", "ReplaceTemplates", value);
    }

    /// <summary>
    /// Использовать ли регулярные выражения при глобальном переименовании.
    /// </summary>
    public bool RenameUseRegex
    {
        get => GetSetting("General", "RenameUseRegex", true);
        set => SetSetting("General", "RenameUseRegex", value);
    }

    /// <summary>
    /// Учитывать ли регистр при глобальном переименовании.
    /// </summary>
    public bool RenameCaseSensitive
    {
        get => GetSetting("General", "RenameCaseSensitive", false);
        set => SetSetting("General", "RenameCaseSensitive", value);
    }

    /// <summary>
    /// Загрузить настройки из JSON-файла на диске в кэш.
    /// Ошибка чтения или разбора не скрывается: возвращается типизированная
    /// ошибка с признаком деградации, повреждённый файл предварительно
    /// сохраняется в резервную копию, а кэш переводится в состояние по умолчанию.
    /// </summary>
    /// <returns>Результат загрузки настроек.</returns>
    public PersistenceResult LoadSettings()
    {
        CancelPendingSave();
        int discarded = DiscardPendingChanges();
        PersistenceResult result = LoadSettingsCore(out bool corruptBackupCreated);
        if (result.IsSuccess)
        {
            ReportPendingDiscarded(discarded);
        }

        ReportCorruptBackup(corruptBackupCreated);
        ReportLoadOutcome(result);
        return result;
    }

    /// <summary>
    /// Сохранить текущее состояние настроек на диск атомарно.
    /// </summary>
    /// <returns>Результат операции сохранения.</returns>
    public PersistenceResult SaveSettings()
    {
        if (IsDisposed)
        {
            return PersistenceResult.Cancelled(
                PersistenceSummaries.Disposed,
                PersistenceErrorCodes.Disposed);
        }

        CancelPendingSave();
        return PersistNow();
    }

    /// <summary>
    /// Изменить пользовательский путь к директории хранения логов.
    /// Меняется только настройка: активная сессия журналирования не переинициализируется,
    /// поэтому новый каталог используется со следующего запуска приложения.
    /// </summary>
    /// <param name="logDirectory">Новый путь или пустая строка для пути по умолчанию.</param>
    /// <returns>Результат операции сохранения настройки.</returns>
    public PersistenceResult SetLogDirectory(string? logDirectory)
    {
        return SetSetting("Logging", "LogDir", logDirectory ?? string.Empty);
    }

    /// <summary>
    /// Получить значение настройки.
    /// Возвращается глубокая копия: изменение полученной коллекции или объекта
    /// не влияет на кэш, файл настроек и систему обнаружения изменений.
    /// </summary>
    public T GetSetting<T>(string group, string key, T defaultValue)
    {
        _lock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(group, out var groupDict))
            {
                if (groupDict.TryGetValue(key, out var val))
                {
                    try
                    {
                        if (val is JsonElement jsonElem)
                        {
                            // Конвертация типов JsonElement в нативные типы C#
                            if (typeof(T) == typeof(bool))
                            {
                                return (T)(object)jsonElem.GetBoolean();
                            }
                            if (typeof(T) == typeof(int))
                            {
                                return (T)(object)jsonElem.GetInt32();
                            }
                            if (typeof(T) == typeof(string))
                            {
                                // Если элемент является строкой, возвращаем её значение.
                                if (jsonElem.ValueKind == JsonValueKind.String)
                                {
                                    return (T)(object)jsonElem.GetString()!;
                                }
                                // Если элемент является логическим значением, возвращаем строковое представление ("True"/"False").
                                if (jsonElem.ValueKind == JsonValueKind.True || jsonElem.ValueKind == JsonValueKind.False)
                                {
                                    return (T)(object)jsonElem.GetBoolean().ToString();
                                }
                                // Если элемент является числом, возвращаем его сырое текстовое представление.
                                if (jsonElem.ValueKind == JsonValueKind.Number)
                                {
                                    return (T)(object)jsonElem.GetRawText();
                                }
                                // Для всех прочих типов используем стандартный ToString().
                                return (T)(object)jsonElem.ToString();
                            }

                            // fallback-десериализация для сложных типов
                            var deserialized = jsonElem.Deserialize<T>(SettingsValueSnapshot.CloneOptions);
                            if (deserialized != null)
                            {
                                object? copied = SettingsValueSnapshot.Clone(deserialized);
                                return (T)copied!;
                            }
                        }
                        else if (val is T typedVal)
                        {
                            return (T)SettingsValueSnapshot.Clone(typedVal)!;
                        }
                        else
                        {
                            object? converted = SettingsValueSnapshot.Clone(
                                Convert.ChangeType(val, typeof(T)));
                            return (T)converted!;
                        }
                    }
                    catch (Exception)
                    {
                        // При ошибке приведения возвращаем дефолт
                    }
                }
            }
            return defaultValue;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Записать значение настройки в кэш и запустить (или запланировать) сохранение на диск.
    /// В кэш попадает глубокая копия значения, поэтому последующие изменения
    /// исходного объекта вызывающей стороны не обходят систему обнаружения изменений.
    /// Сырое значение никогда не попадает в журнал: используется только безопасный хэш.
    /// </summary>
    /// <typeparam name="T">Тип значения настройки.</typeparam>
    /// <param name="group">Группа настройки.</param>
    /// <param name="key">Ключ настройки.</param>
    /// <param name="value">Новое значение.</param>
    /// <returns>Результат операции: Succeeded, Pending или Failed.</returns>
    public PersistenceResult SetSetting<T>(string group, string key, T value)
    {
        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(key))
        {
            return PersistenceResult.Failed(
                PersistenceErrorCodes.InvalidArgument,
                PersistenceSummaries.NotApplied,
                null);
        }

        if (IsDisposed)
        {
            return PersistenceResult.Cancelled(
                PersistenceSummaries.Disposed,
                PersistenceErrorCodes.Disposed);
        }

        CacheApplyOutcome outcome = ApplyToCache(group, key, value);
        if (outcome == CacheApplyOutcome.Rejected)
        {
            return PersistenceResult.Failed(
                PersistenceErrorCodes.NoResult,
                PersistenceSummaries.NotApplied,
                null);
        }

        if (outcome == CacheApplyOutcome.Unchanged)
        {
            return PersistenceResult.Unchanged(group, key);
        }

        RecordPendingChange(group, key, value);

        string compositeKey = group + "/" + key;
        if (Environment.TickCount64 - Interlocked.Read(ref _lastDiskSaveTick) >= SaveDebounceMilliseconds)
        {
            CancelPendingSave();
            return PersistNow();
        }

        if (!ScheduleDeferredSave())
        {
            // Таймер не взведён: обещанная отложенная запись не произойдёт,
            // поэтому Pending был бы ложью — фиксируем честную отмену.
            return PersistenceResult.Cancelled(
                PersistenceSummaries.Disposed,
                PersistenceErrorCodes.Disposed,
                new[] { compositeKey });
        }

        return PersistenceResult.Pending(new[] { compositeKey });
    }

    /// <summary>
    /// Получить все сохраненные настройки определенной группы.
    /// Значения возвращаются глубокими копиями и не позволяют изменять кэш извне.
    /// </summary>
    public Dictionary<string, object> GetAllSettingsInGroup(string group)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (_cache.TryGetValue(group, out var groupDict))
            {
                foreach (var kvp in groupDict)
                {
                    object? cloned = SettingsValueSnapshot.Clone(kvp.Value);
                    if (cloned != null)
                    {
                        result[kvp.Key] = cloned;
                    }
                }
            }
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Инициализировать настройки по умолчанию на основе схемы скриптов.
    /// Успех возвращается только после подтверждённой записи на диск.
    /// Если предыдущая загрузка была деградированной, значения по умолчанию применяются
    /// только в памяти и никогда не записываются поверх повреждённого файла:
    /// для этого существует явный <see cref="ResetToDefaults"/>.
    /// </summary>
    /// <param name="scripts">Скрипты, для которых создаются значения по умолчанию.</param>
    /// <returns>Результат операции сохранения.</returns>
    public PersistenceResult InitializeDefaults(List<AbstractScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        if (IsDisposed)
        {
            return PersistenceResult.Cancelled(
                PersistenceSummaries.Disposed,
                PersistenceErrorCodes.Disposed);
        }

        List<string> removedKeys;
        int appliedCount = ApplyDefaultsNoLock(scripts, out removedKeys);
        ReportNormalized(removedKeys);

        if (IsDegradedLoad)
        {
            return ReportDefaultsDeferred(appliedCount + removedKeys.Count, removedKeys);
        }

        if (appliedCount == 0 && removedKeys.Count == 0)
        {
            if (IsCacheDirty)
            {
                // Кэш содержит несохранённые или отложенные изменения: состояние диска
                // не совпадает с кэшем, поэтому persisted=true был бы ложью.
                return PersistenceResult.Pending(SnapshotPendingKeys());
            }

            return PersistenceResult.Succeeded(
                Array.Empty<string>(),
                PersistenceSource.Disk,
                persisted: true);
        }

        CancelPendingSave();
        PersistenceResult saveResult = PersistNow();
        if (!saveResult.IsSuccess || !saveResult.Persisted)
        {
            return saveResult;
        }

        var properties = new Dictionary<string, object?>(4, StringComparer.Ordinal)
        {
            ["Count"] = appliedCount + removedKeys.Count,
            ["Changed"] = true,
            ["Persisted"] = true,
            ["Status"] = saveResult.Status.ToString()
        };

        _logService.Write(
            SettingsEventIds.DefaultsPersisted,
            LogLevel.Info,
            LogStatus.Changed,
            "Настройки по умолчанию записаны на диск",
            null,
            SettingsEventIds.Source,
            null,
            properties);

        return saveResult;
    }

    /// <summary>
    /// Явный сброс настроек по значениям по умолчанию.
    /// Только этот сценарий разрешает запись поверх деградированной (повреждённой) загрузки,
    /// потому что он инициирован пользователем.
    /// </summary>
    /// <param name="scripts">Скрипты, для которых восстанавливаются значения по умолчанию.</param>
    /// <param name="applicationDefaults">
    /// Явные значения в формате «группа/ключ» — «значение».
    /// Ключи без разделителя игнорируются.
    /// </param>
    /// <returns>Результат операции сохранения.</returns>
    public PersistenceResult ResetToDefaults(
        List<AbstractScript> scripts,
        IReadOnlyList<KeyValuePair<string, object?>>? applicationDefaults)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        if (IsDisposed)
        {
            return PersistenceResult.Cancelled(
                PersistenceSummaries.Disposed,
                PersistenceErrorCodes.Disposed);
        }

        // Разрешение на запись поверх деградированного файла действует только внутри
        // этого метода и снимается в finally: флаг очищается исключительно после
        // подтверждённой записи, поэтому неудачный сброс не снимает защиту.
        Interlocked.Exchange(ref _resetWriteAuthorized, 1);
        try
        {
            List<string> appliedKeys = new();
            if (applicationDefaults != null)
            {
                foreach (KeyValuePair<string, object?> entry in applicationDefaults)
                {
                    SplitCompositeKey(entry.Key, out string group, out string key);
                    if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    if (ApplyToCache(group, key, entry.Value) == CacheApplyOutcome.Changed)
                    {
                        appliedKeys.Add(group + "/" + key);
                    }
                }
            }

            List<string> removedKeys;
            ApplyDefaultsNoLock(scripts, out removedKeys);
            ReportNormalized(removedKeys);
            appliedKeys.AddRange(removedKeys);

            CancelPendingSave();
            PersistenceResult saveResult = PersistNow();
            if (!saveResult.IsSuccess || !saveResult.Persisted)
            {
                Interlocked.Exchange(ref _degradedLoad, 1);
                return saveResult;
            }

            Interlocked.Exchange(ref _degradedLoad, 0);
            Interlocked.Exchange(ref _failedBatchRetries, 0);

            var properties = new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["Count"] = appliedKeys.Count,
                ["Changed"] = appliedKeys.Count > 0,
                ["Persisted"] = true,
                ["Status"] = saveResult.Status.ToString()
            };

            _logService.Write(
                SettingsEventIds.DefaultsPersisted,
                LogLevel.Info,
                LogStatus.Changed,
                "Настройки сброшены к значениям по умолчанию и записаны на диск",
                null,
                SettingsEventIds.Source,
                null,
                properties);

            return saveResult;
        }
        finally
        {
            Interlocked.Exchange(ref _resetWriteAuthorized, 0);
        }
    }

    /// <summary>
    /// Нормализовать имя скрипта для использования в качестве имени секции (группы) JSON.
    /// </summary>
    public string GetSafeGroupName(string scriptName)
    {
        // Нормализация имени группы
        return "Script_" + scriptName
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(" ", "_")
            .Replace("→", "_");
    }

    /// <summary>
    /// Отменить отложенное сохранение, дождаться завершения текущей записи,
    /// удалить временный файл и освободить блокировки.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelPendingSave();
        WaitForPersistQuiescence();
        TryDeleteTempFile();

        lock (_saveTimerLock)
        {
            _pendingChanges.Clear();
            _pendingBatchReported = false;
        }

        ReleaseCacheLock();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool IsDegradedLoad => Volatile.Read(ref _degradedLoad) != 0;

    private bool IsResetWriteAuthorized => Volatile.Read(ref _resetWriteAuthorized) != 0;

    private bool IsCacheDirty =>
        Interlocked.Read(ref _cacheVersion) != Interlocked.Read(ref _persistedVersion);

    private PersistenceResult LoadSettingsCore(out bool corruptBackupCreated)
    {
        corruptBackupCreated = false;
        Dictionary<string, Dictionary<string, object>>? loaded = null;
        PersistenceResult? failure = null;

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath, FileEncoding);
                loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json)
                    ?? new Dictionary<string, Dictionary<string, object>>();
            }
            else
            {
                failure = PersistenceResult.Succeeded(
                    Array.Empty<string>(),
                    PersistenceSource.Defaults,
                    persisted: false);
            }
        }
        catch (JsonException ex)
        {
            failure = PersistenceResult.Failed(
                PersistenceErrorCodes.ParseFailed,
                PersistenceSummaries.ReadFailure,
                ex,
                degraded: true,
                source: PersistenceSource.Fallback);
        }
        catch (Exception ex)
        {
            failure = PersistenceResult.Failed(
                ClassifyReadError(ex),
                PersistenceSummaries.ReadFailure,
                ex,
                degraded: true,
                source: PersistenceSource.Fallback);
        }

        if (failure != null && failure.IsFailure)
        {
            Interlocked.Exchange(ref _degradedLoad, 1);

            // Резервная копия создаётся только при повреждении содержимого файла.
            // Транзиентные ошибки (занятый файл, отказ доступа) резервной копии
            // не требуют: исходный файл остаётся нетронутым и сам по себе.
            if (string.Equals(failure.ErrorCode, PersistenceErrorCodes.ParseFailed, StringComparison.Ordinal))
            {
                corruptBackupCreated = TryBackupCorruptSettingsFile();
            }
        }

        ReplaceCache(loaded);

        if (failure != null)
        {
            return failure;
        }

        return PersistenceResult.Succeeded(source: PersistenceSource.Disk, persisted: true);
    }

    private void ReplaceCache(Dictionary<string, Dictionary<string, object>>? data)
    {
        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            if (data != null)
            {
                foreach (KeyValuePair<string, Dictionary<string, object>> group in data)
                {
                    if (group.Value != null)
                    {
                        _cache[group.Key] = group.Value;
                    }
                }
            }

            // Сброс dirty-состояния выполняется под той же блокировкой кэша,
            // что и замена содержимого: диск и кэш не могут разъехаться вне блокировки.
            Interlocked.Exchange(ref _cacheVersion, 0);
            Interlocked.Exchange(ref _persistedVersion, 0);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void ReportLoadOutcome(PersistenceResult result)
    {
        if (result.IsSuccess && result.Source == PersistenceSource.Defaults)
        {
            var defaultsProperties = new Dictionary<string, object?>(4, StringComparer.Ordinal)
            {
                ["Persisted"] = false,
                ["Status"] = result.Status.ToString(),
                ["Succeeded"] = true,
                ["Reason"] = "first_run"
            };

            _logService.Write(
                SettingsEventIds.LoadCompleted,
                LogLevel.Info,
                LogStatus.Succeeded,
                "Файл настроек не найден, применены значения по умолчанию",
                null,
                SettingsEventIds.Source,
                null,
                defaultsProperties);
            return;
        }

        if (result.IsFailure)
        {
            var failedProperties = new Dictionary<string, object?>(6, StringComparer.Ordinal)
            {
                ["ErrorCode"] = result.ErrorCode,
                ["Failed"] = true,
                ["Persisted"] = false,
                ["Status"] = result.Status.ToString(),
                ["Succeeded"] = false,
                ["Reason"] = result.Source.ToString()
            };

            _logService.Write(
                SettingsEventIds.LoadDegraded,
                LogLevel.Error,
                LogStatus.Failed,
                result.UserSummary,
                result.RawException,
                SettingsEventIds.Source,
                null,
                failedProperties.With("ErrorCode", result.ErrorCode));
            return;
        }

        var properties = new Dictionary<string, object?>(4, StringComparer.Ordinal)
        {
            ["Persisted"] = true,
            ["Status"] = result.Status.ToString(),
            ["Succeeded"] = true,
            ["Reason"] = result.Source.ToString()
        };

        _logService.Write(
            SettingsEventIds.LoadCompleted,
            LogLevel.Info,
            LogStatus.Succeeded,
            "Настройки загружены из файла конфигурации",
            null,
            SettingsEventIds.Source,
            null,
            properties);
    }

    /// <summary>
    /// Сообщает о созданной резервной копии повреждённого файла.
    /// Если копия не создана, отдельного сообщения нет: о неуспешном чтении
    /// уже сообщает событие деградированной загрузки, поэтому ложное
    /// «резервная копия сохранена» не создаётся.
    /// </summary>
    private void ReportCorruptBackup(bool created)
    {
        if (!created)
        {
            return;
        }

        var properties = new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["Count"] = 1,
            ["Changed"] = false,
            ["ErrorCode"] = PersistenceErrorCodes.ParseFailed,
            ["Failed"] = false,
            ["Persisted"] = false,
            ["Reason"] = "corrupt_backup_created"
        };

        _logService.Write(
            SettingsEventIds.CorruptBackup,
            LogLevel.Warning,
            LogStatus.Changed,
            PersistenceSummaries.CorruptBackupCreated,
            null,
            SettingsEventIds.Source,
            null,
            properties);
    }

    private bool TryBackupCorruptSettingsFile()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return false;
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (int attempt = 0; attempt < CorruptBackupNameAttempts; attempt++)
        {
            string suffix = attempt == 0
                ? string.Empty
                : "-" + attempt.ToString(CultureInfo.InvariantCulture);
            string backupName = string.Concat(
                SettingsFileName,
                CorruptBackupInfix,
                stamp,
                suffix,
                CorruptBackupSuffix);
            string backupPath = Path.Combine(
                Path.GetDirectoryName(_settingsFilePath) ?? string.Empty,
                backupName);

            try
            {
                File.Copy(_settingsFilePath, backupPath, overwrite: false);
                PruneCorruptBackups();
                return true;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Ограничивает накопление резервных копий: по количеству, возрасту и суммарному размеру.
    /// Имена и пути в журнал не попадают, удаление ошибок не прерывает работу менеджера.
    /// </summary>
    private void PruneCorruptBackups()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            string[] backups = Directory.GetFiles(
                directory,
                SettingsFileName + CorruptBackupInfix + "*" + CorruptBackupSuffix);
            if (backups.Length <= 0)
            {
                return;
            }

            var entries = new List<(string Path, DateTime WrittenUtc, long Bytes)>(backups.Length);
            foreach (string backup in backups)
            {
                try
                {
                    var info = new FileInfo(backup);
                    entries.Add((backup, info.LastWriteTimeUtc, info.Length));
                }
                catch (Exception)
                {
                }
            }

            if (entries.Count == 0)
            {
                return;
            }

            entries.Sort(static (left, right) => right.WrittenUtc.CompareTo(left.WrittenUtc));

            DateTime cutoff = DateTime.UtcNow.AddDays(-CorruptBackupMaxAgeDays);
            long totalBytes = 0;
            for (int index = 0; index < entries.Count; index++)
            {
                bool tooMany = index >= CorruptBackupRetentionCount;
                bool tooOld = entries[index].WrittenUtc < cutoff;
                bool overBudget = totalBytes + entries[index].Bytes > CorruptBackupMaxTotalBytes;
                if (tooMany || tooOld || overBudget)
                {
                    TryDeleteBackup(entries[index].Path);
                    continue;
                }

                totalBytes += entries[index].Bytes;
            }
        }
        catch (Exception)
        {
        }
    }

    private void TryDeleteBackup(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private void ReportPendingDiscarded(int discarded)
    {
        if (discarded <= 0)
        {
            return;
        }

        var properties = new Dictionary<string, object?>(5, StringComparer.Ordinal)
        {
            ["Count"] = discarded,
            ["Changed"] = false,
            ["Failed"] = true,
            ["Persisted"] = false,
            ["Reason"] = "pending_discarded_on_reload"
        };

        _logService.Write(
            SettingsEventIds.PendingDiscarded,
            LogLevel.Warning,
            LogStatus.Changed,
            "Несохранённые изменения настроек отменены перечитыванием файла настроек",
            null,
            SettingsEventIds.Source,
            null,
            properties);
    }

    private void ReportDirectoryConfigured(string? requested)
    {
        string? effective = ReadEffectiveLogDirectory();
        bool applied = IsLogDirectoryApplied(requested, effective);
        var properties = new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["Persisted"] = false,
            ["Status"] = applied ? "Succeeded" : "Failed",
            ["Succeeded"] = applied,
            ["Failed"] = !applied,
            ["Reason"] = applied ? "configured" : "log_dir_not_applied",
            ["ValueHash"] = PersistenceValueHash.Compute(requested)
        };

        _logService.Write(
            SettingsEventIds.DirectoryConfigured,
            applied ? LogLevel.Info : LogLevel.Warning,
            applied ? LogStatus.Succeeded : LogStatus.Failed,
            applied
                ? "Каталог журналирования сконфигурирован для текущего сеанса"
                : "Запрошенный каталог журналирования не применён, используется каталог по умолчанию",
            null,
            SettingsEventIds.Source,
            null,
            properties);
    }

    private string? ReadEffectiveLogDirectory()
    {
        try
        {
            return _logService.EffectiveLogDirectory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsLogDirectoryApplied(string? requested, string? effective)
    {
        if (string.IsNullOrWhiteSpace(effective))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        return string.Equals(
            requested.TrimEnd('\\', '/'),
            effective.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private void ReportNormalized(IReadOnlyList<string> removedKeys)
    {
        if (removedKeys.Count == 0)
        {
            return;
        }

        var properties = new Dictionary<string, object?>(4, StringComparer.Ordinal)
        {
            ["Count"] = removedKeys.Count,
            ["Changed"] = true,
            ["Persisted"] = false,
            ["Status"] = "Succeeded"
        };

        _logService.Write(
            SettingsEventIds.Normalized,
            LogLevel.Debug,
            LogStatus.Changed,
            "Из схемы удалены параметры, отсутствующие в актуальной версии",
            null,
            SettingsEventIds.Source,
            null,
            properties);
    }

    private PersistenceResult ReportDefaultsDeferred(int appliedCount, IReadOnlyList<string> removedKeys)
    {
        var properties = new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["Count"] = appliedCount,
            ["Changed"] = appliedCount > 0,
            ["ErrorCode"] = PersistenceErrorCodes.DegradedLoad,
            ["Failed"] = true,
            ["Persisted"] = false,
            ["Status"] = "Failed"
        };

        _logService.Write(
            SettingsEventIds.DefaultsDeferred,
            LogLevel.Error,
            LogStatus.Failed,
            PersistenceSummaries.DegradedLoad,
            null,
            SettingsEventIds.Source,
            null,
            properties.With("ErrorCode", PersistenceErrorCodes.DegradedLoad));

        return PersistenceResult.Failed(
            PersistenceErrorCodes.DegradedLoad,
            PersistenceSummaries.DegradedLoad,
            null,
            removedKeys,
            degraded: true,
            source: PersistenceSource.Fallback);
    }

    private CacheApplyOutcome ApplyToCache<T>(string group, string key, T value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_cache.TryGetValue(group, out var groupDict))
            {
                if (value is null)
                {
                    return CacheApplyOutcome.Rejected;
                }

                groupDict = new Dictionary<string, object>();
                _cache[group] = groupDict;
            }

            if (groupDict.TryGetValue(key, out var existingValue))
            {
                if (SettingsValueSnapshot.AreEqual(existingValue, value))
                {
                    return CacheApplyOutcome.Unchanged;
                }

                if (value is null)
                {
                    groupDict.Remove(key);
                    MarkCacheDirty();
                    return CacheApplyOutcome.Changed;
                }

                groupDict[key] = SettingsValueSnapshot.Clone(value)!;
                MarkCacheDirty();
                return CacheApplyOutcome.Changed;
            }

            if (value is null)
            {
                return CacheApplyOutcome.Rejected;
            }

            groupDict[key] = SettingsValueSnapshot.Clone(value)!;
            MarkCacheDirty();
            return CacheApplyOutcome.Changed;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void MarkCacheDirty()
    {
        Interlocked.Increment(ref _cacheVersion);
    }

    private void RecordPendingChange(string group, string key, object? value)
    {
        lock (_saveTimerLock)
        {
            _pendingChanges[group + "/" + key] = PersistenceValueHash.Compute(value);
        }
    }

    private List<KeyValuePair<string, string>> DrainPendingChanges()
    {
        lock (_saveTimerLock)
        {
            var drained = new List<KeyValuePair<string, string>>(_pendingChanges);
            _pendingChanges.Clear();
            _pendingBatchReported = false;
            drained.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
            return drained;
        }
    }

    private int DiscardPendingChanges()
    {
        lock (_saveTimerLock)
        {
            int count = _pendingChanges.Count;
            _pendingChanges.Clear();
            _pendingBatchReported = false;
            return count;
        }
    }

    private IReadOnlyList<string> SnapshotPendingKeys()
    {
        lock (_saveTimerLock)
        {
            var keys = new List<string>(_pendingChanges.Count);
            foreach (string key in _pendingChanges.Keys)
            {
                keys.Add(key);
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }

    private void RestorePendingChanges(IReadOnlyList<KeyValuePair<string, string>> drained)
    {
        lock (_saveTimerLock)
        {
            foreach (KeyValuePair<string, string> change in drained)
            {
                _pendingChanges[change.Key] = change.Value;
            }
        }
    }

    private bool ScheduleDeferredSave()
    {
        return ArmDeferredTimer(SaveDebounceMilliseconds, reportPendingBatch: true);
    }

    /// <summary>
    /// Взводит таймер отложенной записи. Возвращает false, если менеджер освобождается
    /// и таймер взвести уже нельзя: в этом случае отложенная запись не будет выполнена.
    /// </summary>
    private bool ArmDeferredTimer(int delayMilliseconds, bool reportPendingBatch)
    {
        lock (_saveTimerLock)
        {
            if (IsDisposed)
            {
                return false;
            }

            _saveTimer?.Dispose();
            _saveTimer = new Timer(
                static state => ((SettingsManager)state!).OnDebouncedTimerElapsed(),
                this,
                delayMilliseconds,
                Timeout.Infinite);
        }

        if (reportPendingBatch)
        {
            ReportPendingBatchOnce();
        }

        return true;
    }

    private bool HasPendingChanges
    {
        get
        {
            lock (_saveTimerLock)
            {
                return _pendingChanges.Count > 0;
            }
        }
    }

    private void CancelPendingSave()
    {
        lock (_saveTimerLock)
        {
            Timer? timer = _saveTimer;
            _saveTimer = null;
            timer?.Dispose();
        }
    }

    private void OnDebouncedTimerElapsed()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            PersistenceResult result = PersistNow();
            if (result.IsSuccess && result.Persisted)
            {
                Interlocked.Exchange(ref _failedBatchRetries, 0);
                return;
            }

            if (string.Equals(result.ErrorCode, PersistenceErrorCodes.DegradedLoad, StringComparison.Ordinal))
            {
                return;
            }

            ScheduleFailedBatchRetry();
        }
        catch (Exception ex)
        {
            LogPersistenceFailure(
                PersistenceResult.Failed(
                    PersistenceErrorCodes.WriteFailed,
                    PersistenceSummaries.WriteFailure,
                    ex),
                0);
            ScheduleFailedBatchRetry();
        }
    }

    /// <summary>
    /// Повторно взводит отложенную запись неуспешного пакета с ограниченной задержкой.
    /// Повтор разрешён не более <see cref="MaxFailedBatchRetries"/> раз на пакет,
    /// поэтому постоянно непроходимая запись не превращается в бесконечный цикл fsync.
    /// </summary>
    private void ScheduleFailedBatchRetry()
    {
        if (IsDisposed || !HasPendingChanges)
        {
            return;
        }

        if (Interlocked.Increment(ref _failedBatchRetries) > MaxFailedBatchRetries)
        {
            Interlocked.Decrement(ref _failedBatchRetries);
            return;
        }

        ArmDeferredTimer(FailedBatchRetryDelayMilliseconds, reportPendingBatch: false);
    }

    private PersistenceResult PersistNow()
    {
        if (IsDisposed)
        {
            return PersistenceResult.Cancelled(
                PersistenceSummaries.Disposed,
                PersistenceErrorCodes.Disposed);
        }

        Interlocked.Increment(ref _persistInFlight);
        try
        {
            if (IsDisposed)
            {
                return PersistenceResult.Cancelled(
                    PersistenceSummaries.Disposed,
                    PersistenceErrorCodes.Disposed);
            }

            PersistenceResult writeResult;
            long snapshotVersion;
            lock (_saveGate)
            {
                Dictionary<string, Dictionary<string, object>>? snapshot;
                try
                {
                    _lock.EnterReadLock();
                    try
                    {
                        snapshot = CreateSnapshotNoLock();
                        snapshotVersion = Interlocked.Read(ref _cacheVersion);
                    }
                    finally
                    {
                        _lock.ExitReadLock();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return PersistenceResult.Cancelled(
                        PersistenceSummaries.Disposed,
                        PersistenceErrorCodes.Disposed);
                }

                if (snapshot is null)
                {
                    return PersistenceResult.Cancelled(
                        PersistenceSummaries.Disposed,
                        PersistenceErrorCodes.Disposed);
                }

                writeResult = WriteSettingsFile(snapshot);
            }

            if (writeResult.IsSuccess && writeResult.Persisted)
            {
                Interlocked.Exchange(ref _lastDiskSaveTick, Environment.TickCount64);

                // Подтверждённая запись снимает dirty-состояние ровно до версии
                // снятого снимка: более поздние изменения остаются несохранёнными.
                Interlocked.Exchange(ref _persistedVersion, snapshotVersion);
            }

            List<KeyValuePair<string, string>> drained = DrainPendingChanges();

            if (!writeResult.IsSuccess && !writeResult.IsCancelled)
            {
                RestorePendingChanges(drained);
            }

            List<string> changedKeys = new(drained.Count);
            foreach (KeyValuePair<string, string> change in drained)
            {
                changedKeys.Add(change.Key);
            }

            PersistenceResult result = writeResult.WithChangedKeys(changedKeys);
            ReportSaveOutcome(result, drained);
            return result;
        }
        finally
        {
            if (Interlocked.Decrement(ref _persistInFlight) == 0)
            {
                lock (_saveGate)
                {
                    Monitor.PulseAll(_saveGate);
                }
            }
        }
    }

    private void WaitForPersistQuiescence()
    {
        lock (_saveGate)
        {
            int waited = 0;
            while (Volatile.Read(ref _persistInFlight) != 0)
            {
                if (waited >= PersistQuiescenceTimeoutMilliseconds)
                {
                    return;
                }

                Monitor.Wait(_saveGate, PersistQuiescencePollMilliseconds);
                waited += PersistQuiescencePollMilliseconds;
            }
        }
    }

    private void ReleaseCacheLock()
    {
        try
        {
            _lock.Dispose();
        }
        catch (Exception)
        {
            // Освобождение блокировки не должно бросать исключение в Dispose.
        }
    }

    private Dictionary<string, Dictionary<string, object>> CreateSnapshotNoLock()
    {
        var snapshot = new Dictionary<string, Dictionary<string, object>>(_cache.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, Dictionary<string, object>> group in _cache)
        {
            var groupCopy = new Dictionary<string, object>(group.Value.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in group.Value)
            {
                object? cloned = SettingsValueSnapshot.Clone(entry.Value);
                if (cloned != null)
                {
                    groupCopy[entry.Key] = cloned;
                }
            }

            snapshot[group.Key] = groupCopy;
        }

        return snapshot;
    }

    private PersistenceResult WriteSettingsFile(Dictionary<string, Dictionary<string, object>> snapshot)
    {
        if (IsDegradedLoad && !IsResetWriteAuthorized)
        {
            return PersistenceResult.Failed(
                PersistenceErrorCodes.DegradedLoad,
                PersistenceSummaries.DegradedLoadBlocked,
                null,
                degraded: true,
                source: PersistenceSource.Fallback);
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(snapshot, SaveOptions);
        }
        catch (Exception ex)
        {
            return PersistenceResult.Failed(
                PersistenceErrorCodes.SerializeFailed,
                PersistenceSummaries.WriteFailure,
                ex);
        }

        byte[] payload = FileEncoding.GetBytes(json);
        if (payload.Length > MaxSettingsFileBytes)
        {
            return PersistenceResult.Failed(
                PersistenceErrorCodes.SnapshotTooLarge,
                PersistenceSummaries.WriteFailure,
                null);
        }

        try
        {
            string? directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteTempFile(payload);
            CommitTempFile();
            return PersistenceResult.Succeeded(source: PersistenceSource.Disk, persisted: true);
        }
        catch (Exception ex)
        {
            TryDeleteTempFile();
            return PersistenceResult.Failed(
                ClassifyPersistenceError(ex),
                PersistenceSummaries.WriteFailure,
                ex);
        }
    }

    private void WriteTempFile(byte[] payload)
    {
        using (var stream = new FileStream(
            _settingsTempFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough))
        {
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
        }
    }

    private void CommitTempFile()
    {
        try
        {
            File.Move(_settingsTempFilePath, _settingsFilePath, overwrite: true);
            return;
        }
        catch (Exception primary) when (primary is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (TryCommitWithReplace())
            {
                return;
            }

            throw;
        }
    }

    private bool TryCommitWithReplace()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return false;
        }

        try
        {
            File.Replace(_settingsTempFilePath, _settingsFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception ex)
        {
            ReportFallbackFailure("replace", ex);
            return false;
        }
    }

    private void ReportFallbackFailure(string strategy, Exception ex)
    {
        var properties = new Dictionary<string, object?>(5, StringComparer.Ordinal)
        {
            ["ErrorCode"] = ClassifyPersistenceError(ex),
            ["Failed"] = true,
            ["Persisted"] = false,
            ["Status"] = "Failed",
            ["Reason"] = strategy
        };

        _logService.Write(
            SettingsEventIds.CommitFallback,
            LogLevel.Debug,
            LogStatus.Failed,
            "Резервная стратегия фиксации файла настроек не применена",
            ex,
            SettingsEventIds.Source,
            null,
            properties);
    }

    private void TryDeleteTempFile()
    {
        try
        {
            if (File.Exists(_settingsTempFilePath))
            {
                File.Delete(_settingsTempFilePath);
            }
        }
        catch (Exception)
        {
            // Остаточный временный файл будет удалён при следующей успешной записи.
        }
    }

    private void ReportPendingBatchOnce()
    {
        int count;
        lock (_saveTimerLock)
        {
            if (_pendingBatchReported)
            {
                return;
            }

            _pendingBatchReported = true;
            count = _pendingChanges.Count;
        }

        var properties = new Dictionary<string, object?>(4, StringComparer.Ordinal)
        {
            ["Count"] = count,
            ["Changed"] = true,
            ["Persisted"] = false,
            ["Status"] = "Pending"
        };

        _logService.Write(
            SettingsEventIds.ChangedPending,
            LogLevel.Debug,
            LogStatus.Changed,
            "Изменения настроек ожидают пакетной записи на диск",
            null,
            SettingsEventIds.Source,
            null,
            properties);
    }

    private void ReportSaveOutcome(
        PersistenceResult result,
        IReadOnlyList<KeyValuePair<string, string>> drained)
    {
        if (result.IsSuccess && result.Persisted)
        {
            if (drained.Count == 0)
            {
                return;
            }

            LogChangedPersisted(drained);
            return;
        }

        if (result.IsCancelled)
        {
            LogPersistenceCancelled(result, drained.Count);
            return;
        }

        if (result.IsFailure)
        {
            LogPersistenceFailure(result, drained.Count);
        }
    }

    private void LogChangedPersisted(IReadOnlyList<KeyValuePair<string, string>> drained)
    {
        bool single = drained.Count == 1;
        var properties = new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["Count"] = drained.Count,
            ["Changed"] = true,
            ["Persisted"] = true,
            ["Status"] = "Succeeded",
            ["Succeeded"] = true,
            ["ValueHash"] = single ? drained[0].Value : PersistenceValueHash.Combine(drained)
        };

        string message = single ? "Настройка сохранена на диск" : "Настройки сохранены на диск";
        if (single)
        {
            SplitCompositeKey(drained[0].Key, out string group, out string key);
            properties["Group"] = group;
            properties["Key"] = key;
        }

        _logService.Write(
            SettingsEventIds.Changed,
            LogLevel.Info,
            LogStatus.Changed,
            message,
            null,
            SettingsEventIds.Source,
            null,
            properties);
    }

    private void LogPersistenceFailure(PersistenceResult result, int changedCount)
    {
        var properties = new Dictionary<string, object?>(7, StringComparer.Ordinal)
        {
            ["Count"] = changedCount,
            ["Changed"] = changedCount > 0,
            ["ErrorCode"] = result.ErrorCode,
            ["Failed"] = true,
            ["Persisted"] = false,
            ["Succeeded"] = false,
            ["Status"] = result.Status.ToString()
        };

        _logService.Write(
            SettingsEventIds.PersistenceFailed,
            LogLevel.Error,
            LogStatus.Failed,
            result.UserSummary,
            result.RawException,
            SettingsEventIds.Source,
            null,
            properties.With("ErrorCode", result.ErrorCode));
    }

    private void LogPersistenceCancelled(PersistenceResult result, int changedCount)
    {
        var properties = new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["Count"] = changedCount,
            ["Changed"] = changedCount > 0,
            ["ErrorCode"] = result.ErrorCode,
            ["Persisted"] = false,
            ["Status"] = result.Status.ToString(),
            ["CancelReason"] = "disposed"
        };

        _logService.Write(
            SettingsEventIds.PersistenceFailed,
            LogLevel.Warning,
            LogStatus.Cancelled,
            result.UserSummary,
            null,
            SettingsEventIds.Source,
            null,
            properties.With("CleanupState", "NotRequired"));
    }

    private static void SplitCompositeKey(string compositeKey, out string group, out string key)
    {
        int separator = compositeKey.IndexOf('/');
        if (separator <= 0)
        {
            group = compositeKey;
            key = string.Empty;
            return;
        }

        group = compositeKey[..separator];
        key = compositeKey[(separator + 1)..];
    }

    private int ApplyDefaultsNoLock(List<AbstractScript> scripts, out List<string> removedKeys)
    {
        removedKeys = new List<string>();
        int applied = 0;

        _lock.EnterWriteLock();
        try
        {
            // Общие глобальные настройки
            if (SetDefaultIfMissing("General", "OverwriteExisting", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "DefaultOutputSubfolder", "KTools_Result"))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "UseAutoSubfolder", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "Theme", "Dark"))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "BackdropType", "Mica"))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "MaxParallelTasks", Math.Max(1, Environment.ProcessorCount / 2)))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "EnableParallel", true))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "ClearListOnAdd", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "RenameEnableRegex", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "RenameRegexSearch", string.Empty))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "RenameRegexReplace", string.Empty))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "RenameUseRegex", true))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "RenameCaseSensitive", false))
            {
                applied++;
            }

            // Настройки логирования
            if (SetDefaultIfMissing("Logging", "ShowLogsTab", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("Logging", "LogDir", string.Empty))
            {
                applied++;
            }

            // Настройки обновлений
            if (SetDefaultIfMissing("Updates", "AutoCheckUpdates", true))
            {
                applied++;
            }
            if (SetDefaultIfMissing("Updates", "IncludePreReleases", IsPreviewBuild))
            {
                applied++;
            }
            if (SetDefaultIfMissing("Debug", "DebugSimulateOldVersion", false))
            {
                applied++;
            }

            // Инициализация настроек для каждого скрипта
            foreach (var script in scripts)
            {
                string groupName = GetSafeGroupName(script.Name);
                foreach (var field in script.SettingsSchema)
                {
                    if (field.Type != SettingType.Subtitle && SetDefaultIfMissing(groupName, field.Key, field.DefaultValue))
                    {
                        applied++;
                    }

                    if (field.ChildFields != null && field.ChildFields.Count > 0)
                    {
                        foreach (var child in field.ChildFields)
                        {
                            if (child.Type != SettingType.Subtitle && SetDefaultIfMissing(groupName, child.Key, child.DefaultValue))
                            {
                                applied++;
                            }
                        }
                    }
                }

                // Нормализация настроек: удаление лишних ключей, которых нет в схеме
                if (_cache.TryGetValue(groupName, out var groupDict))
                {
                    var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in script.SettingsSchema)
                    {
                        if (field.Type != SettingType.Subtitle)
                        {
                            validKeys.Add(field.Key);
                        }
                        if (field.ChildFields != null && field.ChildFields.Count > 0)
                        {
                            foreach (var child in field.ChildFields)
                            {
                                if (child.Type != SettingType.Subtitle)
                                {
                                    validKeys.Add(child.Key);
                                }
                            }
                        }
                    }

                    var keysToRemove = new List<string>();
                    foreach (var key in groupDict.Keys)
                    {
                        if (!validKeys.Contains(key))
                        {
                            keysToRemove.Add(key);
                        }
                    }

                    foreach (string key in keysToRemove)
                    {
                        groupDict.Remove(key);
                        removedKeys.Add(groupName + "/" + key);
                        applied++;
                    }
                }
            }

            if (applied > 0)
            {
                MarkCacheDirty();
            }

            return applied;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private bool SetDefaultIfMissing(string group, string key, object? value)
    {
        if (HasSetting(group, key))
        {
            return false;
        }

        SetSettingInternal(group, key, value);
        return true;
    }

    private bool HasSetting(string group, string key)
    {
        return _cache.TryGetValue(group, out var groupDict) &&
               groupDict.ContainsKey(key);
    }

    private void SetSettingInternal(string group, string key, object? value)
    {
        if (!_cache.TryGetValue(group, out var groupDict))
        {
            groupDict = new Dictionary<string, object>();
            _cache[group] = groupDict;
        }

        groupDict[key] = SettingsValueSnapshot.Clone(value)!;
    }

    private static string ClassifyReadError(Exception ex)
    {
        if (IsLockError(ex))
        {
            return PersistenceErrorCodes.FileLocked;
        }

        return ex switch
        {
            JsonException => PersistenceErrorCodes.ParseFailed,
            UnauthorizedAccessException => PersistenceErrorCodes.AccessDenied,
            DirectoryNotFoundException => PersistenceErrorCodes.InvalidPath,
            FileNotFoundException => PersistenceErrorCodes.InvalidPath,
            PathTooLongException => PersistenceErrorCodes.InvalidPath,
            NotSupportedException => PersistenceErrorCodes.InvalidPath,
            ArgumentException => PersistenceErrorCodes.InvalidPath,
            IOException => PersistenceErrorCodes.ReadFailed,
            _ => PersistenceErrorCodes.ReadFailed
        };
    }

    private static string ClassifyPersistenceError(Exception ex)
    {
        if (IsLockError(ex))
        {
            return PersistenceErrorCodes.FileLocked;
        }

        return ex switch
        {
            UnauthorizedAccessException => PersistenceErrorCodes.AccessDenied,
            DirectoryNotFoundException => PersistenceErrorCodes.InvalidPath,
            FileNotFoundException => PersistenceErrorCodes.InvalidPath,
            PathTooLongException => PersistenceErrorCodes.InvalidPath,
            NotSupportedException => PersistenceErrorCodes.InvalidPath,
            ArgumentException => PersistenceErrorCodes.InvalidPath,
            JsonException => PersistenceErrorCodes.SerializeFailed,
            IOException => PersistenceErrorCodes.WriteFailed,
            _ => PersistenceErrorCodes.WriteFailed
        };
    }

    private static bool IsLockError(Exception exception)
    {
        int code = exception.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }

    private enum CacheApplyOutcome
    {
        Unchanged = 0,
        Changed = 1,
        Rejected = 2
    }
}

/// <summary>
/// Ограниченный канонический снимок значения настройки.
/// Формирует детерминированное текстовое представление значения и его глубокую копию,
/// чтобы сравнение значений не зависело от ссылочного равенства и не обходилось
/// без ограничений по глубине, количеству элементов и длине результата.
/// </summary>
public static class SettingsValueSnapshot
{
    /// <summary>
    /// Максимальная глубина обхода вложенных структур.
    /// </summary>
    public const int MaxDepth = 12;

    /// <summary>
    /// Максимальное количество посещённых узлов при построении снимка.
    /// </summary>
    public const int MaxNodes = 8192;

    /// <summary>
    /// Максимальная длина канонического представления в символах.
    /// </summary>
    public const int MaxCanonicalLength = 96 * 1024;

    private const int MaxCollectionItems = 8192;

    /// <summary>
    /// Состояние построения канонического представления: счётчик посещённых узлов
    /// и признак усечения по достигнутым границам.
    /// </summary>
    private sealed class DescribeState
    {
        internal int Nodes { get; set; }

        internal bool Truncated { get; set; }
    }

    /// <summary>
    /// Параметры сериализации для глубокого копирования и канонического снимка.
    /// </summary>
    public static JsonSerializerOptions CloneOptions { get; } = new()
    {
        IncludeFields = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Сравнить два значения настройки по ограниченному каноническому снимку.
    /// Если снимок хотя бы одного значения усечён границами, сравнение
    /// fail-closed: значения считаются различными, поэтому два разных больших
    /// значения никогда не могут быть признаны неизменными.
    /// </summary>
    /// <param name="left">Первое значение (кэш или существующее).</param>
    /// <param name="right">Второе значение (новое).</param>
    /// <returns>True, если значения эквивалентны и оба снимка построены полностью.</returns>
    public static bool AreEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (IsScalar(left) && IsScalar(right))
        {
            return ScalarEquals(left!, right!);
        }

        if (!TryDescribe(left, out string? leftText, out bool leftTruncated) ||
            !TryDescribe(right, out string? rightText, out bool rightTruncated))
        {
            return false;
        }

        if (leftTruncated || rightTruncated)
        {
            return false;
        }

        return string.Equals(leftText, rightText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Построить ограниченное каноническое представление значения настройки.
    /// </summary>
    /// <param name="value">Значение настройки.</param>
    /// <returns>Детерминированное текстовое представление значения.</returns>
    public static string Describe(object? value)
    {
        TryDescribe(value, out string? described, out _);
        return described ?? string.Empty;
    }

    /// <summary>
    /// Построить ограниченное каноническое представление значения и сообщить,
    /// был ли снимок усечён границами.
    /// </summary>
    /// <param name="value">Значение настройки.</param>
    /// <param name="described">Каноническое представление значения.</param>
    /// <param name="truncated">True, если представление не помещается в установленные границы.</param>
    /// <returns>True, если представление построено полностью и может использоваться для сравнения.</returns>
    public static bool TryDescribe(object? value, out string? described, out bool truncated)
    {
        var builder = new StringBuilder(256);
        var state = new DescribeState();
        DescribeCore(value, builder, 0, state);
        described = builder.ToString();
        truncated = state.Truncated || described.Length > MaxCanonicalLength;
        return !truncated;
    }

    /// <summary>
    /// Создать глубокую копию значения настройки.
    /// Неизменяемые значения возвращаются как есть, коллекции и словари копируются
    /// с сохранением конкретных типов, а остальные объекты копируются через JSON.
    /// Если копирование невозможно, возвращается исходный объект без изменений.
    /// </summary>
    /// <param name="value">Значение настройки.</param>
    /// <returns>Глубокая копия значения.</returns>
    public static object? Clone(object? value)
    {
        return CloneCore(value, 0);
    }

    private static bool IsScalar(object value)
    {
        return value is string
            or bool
            or char
            or sbyte
            or byte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or float
            or double
            or decimal
            or DateTime
            or DateTimeOffset
            or TimeSpan
            or Guid
            || value.GetType().IsEnum;
    }

    private static bool ScalarEquals(object left, object right)
    {
        if (left is string leftText && right is string rightText)
        {
            return string.Equals(leftText, rightText, StringComparison.Ordinal);
        }

        if (left is IFormattable leftFormattable && right is IFormattable rightFormattable)
        {
            if (left.GetType() != right.GetType())
            {
                return false;
            }

            try
            {
                return string.Equals(
                    leftFormattable.ToString(null, CultureInfo.InvariantCulture),
                    rightFormattable.ToString(null, CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return left.Equals(right);
            }
        }

        return left.Equals(right);
    }

    private static object? CloneCore(object? value, int depth)
    {
        if (value is null || IsScalar(value))
        {
            return value;
        }

        if (value is JsonElement element)
        {
            return element.Clone();
        }

        if (depth >= MaxDepth)
        {
            return value;
        }

        Type type = value.GetType();
        if (value is IDictionary dictionary)
        {
            return CloneDictionary(dictionary, type, depth);
        }

        if (value is IEnumerable sequence)
        {
            return CloneSequence(sequence, type, depth);
        }

        return ClonePoco(value, type);
    }

    private static object CloneDictionary(IDictionary dictionary, Type type, int depth)
    {
        IDictionary? target = null;
        if (!type.IsAbstract && !type.IsInterface && !type.IsArray)
        {
            try
            {
                target = Activator.CreateInstance(type) as IDictionary;
            }
            catch (Exception)
            {
                target = null;
            }
        }

        target ??= new Dictionary<object, object?>();

        int count = 0;
        try
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count >= MaxCollectionItems)
                {
                    break;
                }

                target.Add(CloneCore(entry.Key, depth + 1)!, CloneCore(entry.Value, depth + 1));
                count++;
            }
        }
        catch (Exception)
        {
            return dictionary;
        }

        return target;
    }

    private static object CloneSequence(object value, Type type, int depth)
    {
        if (value is not IEnumerable sequence)
        {
            return value;
        }

        if (type.IsArray)
        {
            if (value is not Array source)
            {
                return value;
            }

            Type elementType = type.GetElementType() ?? typeof(object);
            Array target = Array.CreateInstance(elementType, source.Length);
            for (int index = 0; index < source.Length; index++)
            {
                try
                {
                    target.SetValue(CloneCore(source.GetValue(index), depth + 1), index);
                }
                catch (Exception)
                {
                    return value;
                }
            }

            return target;
        }

        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(List<>) &&
            !type.IsAbstract &&
            !type.IsInterface)
        {
            try
            {
                var list = (IList)Activator.CreateInstance(type)!;
                int count = 0;
                foreach (object? item in sequence)
                {
                    if (count >= MaxCollectionItems)
                    {
                        break;
                    }

                    list.Add(CloneCore(item, depth + 1));
                    count++;
                }

                return list;
            }
            catch (Exception)
            {
                return value;
            }
        }

        return value;
    }

    private static object ClonePoco(object value, Type type)
    {
        try
        {
            JsonElement element = JsonSerializer.SerializeToElement(value, type, CloneOptions);
            return element.Deserialize(type, CloneOptions) ?? value;
        }
        catch (Exception)
        {
            return value;
        }
    }

    private static void DescribeCore(object? value, StringBuilder builder, int depth, DescribeState state)
    {
        if (state.Nodes++ >= MaxNodes || builder.Length >= MaxCanonicalLength)
        {
            state.Truncated = true;
            builder.Append('~');
            return;
        }

        if (value is null)
        {
            builder.Append("n;");
            return;
        }

        if (value is JsonElement jsonElement)
        {
            DescribeJsonElement(jsonElement, builder, depth, state);
            return;
        }

        if (IsScalar(value))
        {
            DescribeScalar(value, builder);
            return;
        }

        if (depth >= MaxDepth)
        {
            builder.Append("d;");
            return;
        }

        Type type = value.GetType();
        if (value is IDictionary dictionary)
        {
            DescribeDictionary(dictionary, builder, depth, state);
            return;
        }

        if (value is IEnumerable sequence)
        {
            DescribeSequence(sequence, builder, depth, state);
            return;
        }

        try
        {
            JsonElement element = JsonSerializer.SerializeToElement(value, type, CloneOptions);
            DescribeJsonElement(element, builder, depth, state);
        }
        catch (Exception)
        {
            state.Truncated = true;
            builder.Append("o:").Append(type.Name).Append(';');
        }
    }

    private static void DescribeScalar(object value, StringBuilder builder)
    {
        switch (value)
        {
            case string text:
                builder.Append("s:").Append(text.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(text).Append(';');
                return;
            case bool flag:
                builder.Append(flag ? "b:1;" : "b:0;");
                return;
        }

        Type type = value.GetType();
        if (type.IsEnum)
        {
            builder.Append("e:").Append(type.FullName).Append(':')
                .Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)).Append(';');
            return;
        }

        if (value is IFormattable formattable)
        {
            builder.Append("v:").Append(type.FullName).Append(':');
            try
            {
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                builder.Append("unavailable");
            }

            builder.Append(';');
            return;
        }

        builder.Append("v:").Append(type.FullName).Append(':').Append(value).Append(';');
    }

    private static void DescribeDictionary(IDictionary dictionary, StringBuilder builder, int depth, DescribeState state)
    {
        var entries = new List<KeyValuePair<string, string>>(Math.Min(dictionary.Count, MaxCollectionItems));
        int count = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (count >= MaxCollectionItems)
            {
                state.Truncated = true;
                break;
            }

            var entryBuilder = new StringBuilder(64);
            DescribeCore(entry.Value, entryBuilder, depth + 1, state);
            entries.Add(new KeyValuePair<string, string>(
                entry.Key?.ToString() ?? string.Empty,
                entryBuilder.ToString()));
            count++;
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        builder.Append("d{");
        foreach (KeyValuePair<string, string> entry in entries)
        {
            builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            if (builder.Length >= MaxCanonicalLength)
            {
                state.Truncated = true;
                break;
            }
        }

        builder.Append('}');
    }

    private static void DescribeSequence(IEnumerable sequence, StringBuilder builder, int depth, DescribeState state)
    {
        builder.Append("l[");
        int count = 0;
        foreach (object? item in sequence)
        {
            if (count >= MaxCollectionItems)
            {
                state.Truncated = true;
                builder.Append('~');
                break;
            }

            DescribeCore(item, builder, depth + 1, state);
            count++;
            if (builder.Length >= MaxCanonicalLength)
            {
                state.Truncated = true;
                builder.Append('~');
                break;
            }
        }

        builder.Append(']');
    }

    private static void DescribeJsonElement(JsonElement element, StringBuilder builder, int depth, DescribeState state)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append("d{");
                var properties = new List<JsonProperty>();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    properties.Add(property);
                }

                properties.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
                foreach (JsonProperty property in properties)
                {
                    builder.Append(property.Name).Append('=');
                    DescribeJsonElement(property.Value, builder, depth + 1, state);
                    builder.Append(';');
                    if (builder.Length >= MaxCanonicalLength)
                    {
                        state.Truncated = true;
                        break;
                    }
                }

                builder.Append('}');
                return;

            case JsonValueKind.Array:
                builder.Append("l[");
                foreach (JsonElement item in element.EnumerateArray())
                {
                    DescribeJsonElement(item, builder, depth + 1, state);
                    if (builder.Length >= MaxCanonicalLength)
                    {
                        state.Truncated = true;
                        builder.Append('~');
                        break;
                    }
                }

                builder.Append(']');
                return;

            case JsonValueKind.String:
                DescribeScalar(element.GetString() ?? string.Empty, builder);
                return;

            case JsonValueKind.True:
                builder.Append("b:1;");
                return;

            case JsonValueKind.False:
                builder.Append("b:0;");
                return;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("n;");
                return;

            default:
                builder.Append("n:").Append(element.GetRawText()).Append(';');
                return;
        }
    }
}

/// <summary>
/// Представляет один элемент шаблона (регулярного выражения или переменной автозамены) с описанием.
/// </summary>
public class TemplateItem
{
    /// <summary>
    /// Шаблон регулярного выражения или переменная.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Русское описание назначения шаблона.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
