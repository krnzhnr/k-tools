// -*- coding: utf-8 -*-
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// Путь к файлу настроек вычисляется при каждом обращении из актуального
    /// каталога IPathManager (ишью 142): смена базового каталога после создания
    /// менеджера (миграция, откат) не оставляет запись в устаревшем месте.
    /// </summary>
    private string SettingsFilePath => Path.Combine(_pathManager.GetSettingsDirectory(), SettingsFileName);

    /// <summary>
    /// Путь к временному файлу атомарной записи, всегда рядом с актуальным файлом настроек.
    /// </summary>
    private string SettingsTempFilePath => SettingsFilePath + TempFileSuffix;
    // Единая политика регистра ключей: OrdinalIgnoreCase во всём кэше и в схеме
    // (SettingsSchemaComparer), чтобы «CRF» и «crf» были одним слотом, а не дублями.
    private readonly Dictionary<string, Dictionary<string, object>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingChanges = new(StringComparer.Ordinal);

    private Timer? _saveTimer;
    private bool _pendingBatchReported;
    private long _lastDiskSaveTick;
    private int _disposed;
    private int _degradedLoad;
    private int _persistInFlight;
    private int _resetWriteAuthorized;
    private int _failedBatchRetries;
    private int _initialized;
    private long _cacheVersion;
    private long _persistedVersion;

    /// <summary>
    /// Инициализирует новый экземпляр класса SettingsManager с внедрением зависимостей.
    /// Конструктор выполняет только сторожевые проверки и не имеет побочек
    /// (ишью 142): ни чтения диска, ни разрешения путей, ни перенастройки журнала.
    /// Вся инициализация — в <see cref="InitializeAsync"/>, который вызывается
    /// после миграции данных.
    /// </summary>
    public SettingsManager(ILogService logService, IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
        _lastDiskSaveTick = Environment.TickCount64 - SaveDebounceMilliseconds;
    }

    /// <summary>
    /// Явная инициализация после миграции данных (ишью 142): читает settings.json
    /// из актуального каталога, настраивает каталог журнала и сообщает итоги загрузки.
    /// Вызывается один раз после IUserDataMigrator.EnsureMigrated; повторные вызовы
    /// безопасны и ничего не меняют. До вызова кэш пуст и возвращаются значения
    /// по умолчанию.
    /// </summary>
    public Task InitializeAsync()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

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
        return Task.CompletedTask;
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
    /// Тип фона окон приложения (Mica, Acrylic или Обычный без эффектов).
    /// Неизвестные значения из старых конфигураций нормализуются к Mica.
    /// </summary>
    public string BackdropType
    {
        get => NormalizeBackdropType(GetSetting("General", "BackdropType", "Mica"));
        set => SetSetting("General", "BackdropType", NormalizeBackdropType(value));
    }

    /// <summary>
    /// Нормализует строковое значение типа фона к допустимому набору.
    /// Каноническое имя обычного фона без эффектов — None, также принимается
    /// псевдоним Solid. Неизвестные и пустые значения отображаются на Mica.
    /// </summary>
    private static string NormalizeBackdropType(string? value)
    {
        if (string.Equals(value, "Acrylic", StringComparison.OrdinalIgnoreCase))
        {
            return "Acrylic";
        }

        if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Solid", StringComparison.OrdinalIgnoreCase))
        {
            return "None";
        }

        return "Mica";
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
    /// Максимальное количество одновременных потоков разборки контейнера.
    /// </summary>
    public int MaxParallelDemuxTasks
    {
        get => GetSetting("General", "MaxParallelDemuxTasks", Math.Max(1, Environment.ProcessorCount / 2));
        set => SetSetting("General", "MaxParallelDemuxTasks", value);
    }

    /// <summary>
    /// Разрешить ли параллельную разборку контейнеров очереди.
    /// По умолчанию выключена: параллельный demux — опция для быстрых дисков.
    /// </summary>
    public bool EnableParallelDemux
    {
        get => GetSetting("General", "EnableParallelDemux", false);
        set => SetSetting("General", "EnableParallelDemux", value);
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
    /// Запоминать ли выходной путь между перезапусками приложения.
    /// По умолчанию включено: глобальный путь пишется в настройки и восстанавливается.
    /// </summary>
    public bool RememberOutputPaths
    {
        get => GetSetting("General", "RememberOutputPaths", true);
        set => SetSetting("General", "RememberOutputPaths", value);
    }

    /// <summary>
    /// Показывать ли в очереди пилюлю частоты кадров.
    /// </summary>
    public bool ShowFrameRateBadge
    {
        get => GetSetting("General", "ShowFrameRateBadge", false);
        set => SetSetting("General", "ShowFrameRateBadge", value);
    }

    /// <summary>
    /// Показывать ли в очереди пилюлю типа развёртки (CFR/VFR).
    /// </summary>
    public bool ShowFrameModeBadge
    {
        get => GetSetting("General", "ShowFrameModeBadge", false);
        set => SetSetting("General", "ShowFrameModeBadge", value);
    }

    /// <summary>
    /// Режим отображения FPS и CFR/VFR в строке очереди:
    /// 0 — скрыто, 1 — пилюли, 2 — текстовой строкой.
    /// </summary>
    public int QueueBadgeMode
    {
        get => GetSetting("General", "QueueBadgeMode", 0);
        set => SetSetting("General", "QueueBadgeMode", value);
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
    /// Версия, для которой уже показывалось окно «Что нового».
    /// </summary>
    public string LastSeenWhatsNewVersion
    {
        get => GetSetting("Updates", "LastSeenWhatsNewVersion", string.Empty);
        set => SetSetting("Updates", "LastSeenWhatsNewVersion", value);
    }

    /// <summary>
    /// Показывать ли окно «Что нового» после обновления.
    /// </summary>
    public bool ShowWhatsNewAfterUpdate
    {
        get => GetSetting("Updates", "ShowWhatsNewAfterUpdate", true);
        set => SetSetting("Updates", "ShowWhatsNewAfterUpdate", value);
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
                                // Строковые значения ("false"/"true") разбираются через канонический
                                // хелпер: прямой GetBoolean бросает исключение на строках и молча
                                // возвращает дефолт, инвертируя смысл настройки.
                                bool fallback = (bool)(object)defaultValue!;
                                return (T)(object)SettingValue.AsBool(jsonElem, fallback);
                            }
                            if (typeof(T) == typeof(int))
                            {
                                // Строковые числа ("23") разбираются через канонический хелпер:
                                // прямой GetInt32 бросает исключение на строках и молча
                                // возвращает дефолт вместо сохранённого значения.
                                int fallback = (int)(object)defaultValue!;
                                return (T)(object)SettingValue.AsInt(jsonElem, fallback);
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
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
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
    /// Схемы поступают через поставщика поверх реестра скриптов, поэтому уровень
    /// настроек не зависит от конкретных скриптов. Успех возвращается только после
    /// подтверждённой записи на диск.
    /// Если предыдущая загрузка была деградированной, значения по умолчанию применяются
    /// только в памяти и никогда не записываются поверх повреждённого файла:
    /// для этого существует явный <see cref="ResetToDefaults"/>.
    /// Секции, не связанные с зарегистрированными скриптами, не удаляются,
    /// а фиксируются в журнале событием mismatch.
    /// </summary>
    /// <param name="defaultsProvider">Поставщик значений по умолчанию зарегистрированных скриптов.</param>
    /// <returns>Результат операции сохранения.</returns>
    public PersistenceResult InitializeDefaults(ISettingsDefaultsProvider defaultsProvider)
    {
        ArgumentNullException.ThrowIfNull(defaultsProvider);

        if (IsDisposed)
        {
            return PersistenceResult.Cancelled(
                PersistenceSummaries.Disposed,
                PersistenceErrorCodes.Disposed);
        }

        List<string> removedKeys;
        List<string> unknownGroups;
        int appliedCount = ApplyDefaultsNoLock(defaultsProvider, out removedKeys, out unknownGroups);
        ReportNormalized(removedKeys);
        ReportProviderMismatch(unknownGroups);

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
    /// <param name="defaultsProvider">Поставщик значений по умолчанию зарегистрированных скриптов.</param>
    /// <param name="applicationDefaults">
    /// Явные значения в формате «группа/ключ» — «значение».
    /// Ключи без разделителя игнорируются.
    /// </param>
    /// <returns>Результат операции сохранения.</returns>
    public PersistenceResult ResetToDefaults(
        ISettingsDefaultsProvider defaultsProvider,
        IReadOnlyList<KeyValuePair<string, object?>>? applicationDefaults)
    {
        ArgumentNullException.ThrowIfNull(defaultsProvider);

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
            List<string> unknownGroups;
            ApplyDefaultsNoLock(defaultsProvider, out removedKeys, out unknownGroups);
            ReportNormalized(removedKeys);
            ReportProviderMismatch(unknownGroups);
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
    /// Имена с разделителями («A/B») и без них («A_B») дают разные секции:
    /// при наличии «/» или «\» добавляется детерминированный суффикс стабильного
    /// хэша исходного имени, иначе значения скриптов затирали бы друг друга.
    /// Имена без этих символов сохраняют прежнее отображение для совместимости
    /// существующих файлов settings.json.
    /// </summary>
    public string GetSafeGroupName(string scriptName)
    {
        ArgumentException.ThrowIfNullOrEmpty(scriptName);

        // Нормализация имени группы
        string baseName = "Script_" + scriptName
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(" ", "_")
            .Replace("→", "_");

        // Разрешение коллизии: суффикс детерминирован (SHA-256 исходного имени),
        // поэтому одинаков во всех запусках и на всех машинах.
        if (scriptName.Contains('/') || scriptName.Contains('\\'))
        {
            baseName += "_" + GetStableNameHash(scriptName);
        }

        return baseName;
    }

    /// <summary>
    /// Вычислить короткий стабильный хэш имени (8 hex-символов SHA-256 в UTF-8).
    /// В отличие от string.GetHashCode не зависит от запуска процесса.
    /// </summary>
    private static string GetStableNameHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
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
            if (File.Exists(SettingsFilePath))
            {
                // Защита от OOM: раздутый файл не читается в память,
                // а сразу переходит в деградированную загрузку зеркально лимиту записи.
                if (new FileInfo(SettingsFilePath).Length > MaxSettingsFileBytes)
                {
                    failure = PersistenceResult.Failed(
                        PersistenceErrorCodes.SnapshotTooLarge,
                        PersistenceSummaries.ReadFailure,
                        null,
                        degraded: true,
                        source: PersistenceSource.Fallback);
                }
                else
                {
                    string json = File.ReadAllText(SettingsFilePath, FileEncoding);
                    loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json)
                        ?? new Dictionary<string, Dictionary<string, object>>();
                }
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
                    if (group.Value == null)
                    {
                        continue;
                    }

                    // Единая политика регистра: загруженные группы переупаковываются
                    // в регистронезависимые словари, как и весь остальной кэш.
                    // Файл прошлой версии может содержать дубли «CRF»/«crf»: схлопываем
                    // в один слот, первое вхождение побеждает как более старое.
                    if (!_cache.TryGetValue(group.Key, out var existingGroup))
                    {
                        existingGroup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        _cache[group.Key] = existingGroup;
                    }

                    foreach (KeyValuePair<string, object> entry in group.Value)
                    {
                        existingGroup.TryAdd(entry.Key, entry.Value);
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
        if (!File.Exists(SettingsFilePath))
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
                Path.GetDirectoryName(SettingsFilePath) ?? string.Empty,
                backupName);

            try
            {
                File.Copy(SettingsFilePath, backupPath, overwrite: false);
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
            string? directory = Path.GetDirectoryName(SettingsFilePath);
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
        string displayValue = string.IsNullOrWhiteSpace(requested)
            ? string.Empty
            : LogProps.FileName(requested);
        var properties = new Dictionary<string, object?>(8, StringComparer.Ordinal)
        {
            ["Persisted"] = false,
            ["Status"] = applied ? "Succeeded" : "Failed",
            ["Succeeded"] = applied,
            ["Failed"] = !applied,
            ["Reason"] = applied ? "configured" : "log_dir_not_applied",
            ["Key"] = "LogDir",
            ["Value"] = displayValue,
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

    /// <summary>
    /// Фиксирует в журнале секции настроек, не связанные ни с одним
    /// зарегистрированным скриптом. Значения не удаляются, только журналируются:
    /// молчаливое хранение маскировало бы незарегистрированный скрипт.
    /// </summary>
    private void ReportProviderMismatch(IReadOnlyList<string> unknownGroups)
    {
        if (unknownGroups.Count == 0)
        {
            return;
        }

        var orderedGroups = new List<string>(unknownGroups);
        orderedGroups.Sort(StringComparer.Ordinal);

        var properties = new Dictionary<string, object?>(5, StringComparer.Ordinal)
        {
            ["Count"] = unknownGroups.Count,
            ["Changed"] = false,
            ["Persisted"] = false,
            ["Status"] = "Skipped",
            ["Group"] = string.Join(";", orderedGroups)
        };

        _logService.Write(
            SettingsEventIds.DefaultsMismatch,
            LogLevel.Warning,
            LogStatus.Skipped,
            "В файле настроек найдены секции, не связанные с зарегистрированными скриптами, значения сохранены без изменений",
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

                // Единая политика регистра: слот группы регистронезависимый, как схема.
                groupDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
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
        var snapshot = new Dictionary<string, Dictionary<string, object>>(_cache.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Dictionary<string, object>> group in _cache)
        {
            var groupCopy = new Dictionary<string, object>(group.Value.Count, StringComparer.OrdinalIgnoreCase);
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
            string? directory = Path.GetDirectoryName(SettingsFilePath);
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
            SettingsTempFilePath,
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
            File.Move(SettingsTempFilePath, SettingsFilePath, overwrite: true);
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
        if (!File.Exists(SettingsFilePath))
        {
            return false;
        }

        try
        {
            File.Replace(SettingsTempFilePath, SettingsFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
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
            if (File.Exists(SettingsTempFilePath))
            {
                File.Delete(SettingsTempFilePath);
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
        string displayValue = string.Empty;
        string singleGroup = string.Empty;
        string singleKey = string.Empty;
        if (single)
        {
            SplitCompositeKey(drained[0].Key, out singleGroup, out singleKey);
            try
            {
                _lock.EnterReadLock();
                try
                {
                    if (_cache.TryGetValue(singleGroup, out Dictionary<string, object>? groupDict)
                        && groupDict.TryGetValue(singleKey, out object? raw))
                    {
                        if (raw is string text)
                        {
                            displayValue = text ?? string.Empty;
                        }
                        else if (raw is JsonElement element)
                        {
                            displayValue = element.ValueKind == JsonValueKind.String
                                ? element.GetString() ?? string.Empty
                                : element.GetRawText();
                        }
                        else if (raw is not null)
                        {
                            try
                            {
                                displayValue = raw.ToString() ?? string.Empty;
                            }
                            catch (Exception)
                            {
                                displayValue = string.Empty;
                            }
                        }
                    }
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
            catch (Exception)
            {
                displayValue = string.Empty;
            }
        }

        var properties = new Dictionary<string, object?>(8, StringComparer.Ordinal)
        {
            ["Count"] = drained.Count,
            ["Changed"] = true,
            ["Persisted"] = true,
            ["Status"] = "Succeeded",
            ["Succeeded"] = true,
            ["ValueHash"] = single ? drained[0].Value : PersistenceValueHash.Combine(drained)
        };

        string message;
        if (single)
        {
            properties["Group"] = singleGroup;
            properties["Key"] = singleKey;
            // Приватность важнее открытости значения (ишью #129): значения чувствительных
            // параметров и свободных CLI-аргументов (туда регулярно попадают токены и пароли)
            // в журнал не пишутся — только маркер редактуры и ValueHash.
            bool sensitive = LogRedactor.IsSensitiveKeyName(singleKey)
                || singleKey.Equals("AdditionalArgs", StringComparison.OrdinalIgnoreCase);
            if (sensitive)
            {
                properties["Value"] = LogRedactor.RedactedMarker;
                message = $"Параметр '{singleKey}' сохранён в настройках";
            }
            else
            {
                properties["Value"] = displayValue;
                message = string.IsNullOrEmpty(displayValue)
                    ? $"Параметр '{singleKey}' сохранён в настройках"
                    : $"Параметр '{singleKey}' сохранён в настройках: '{displayValue}'";
            }
        }
        else
        {
            message = "Настройки сохранены на диск";
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

    private int ApplyDefaultsNoLock(
        ISettingsDefaultsProvider defaultsProvider,
        out List<string> removedKeys,
        out List<string> unknownGroups)
    {
        removedKeys = new List<string>();
        unknownGroups = new List<string>();
        int applied = 0;
        var knownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            if (SetDefaultIfMissing("General", "MaxParallelDemuxTasks", Math.Max(1, Environment.ProcessorCount / 2)))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "EnableParallelDemux", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "ClearListOnAdd", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "RememberOutputPaths", true))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "ShowFrameRateBadge", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "ShowFrameModeBadge", false))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "QueueBadgeMode", 0))
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
            if (SetDefaultIfMissing("General", "SearchTemplates", SettingsDefaults.GetDefaultSearchTemplates()))
            {
                applied++;
            }
            if (SetDefaultIfMissing("General", "ReplaceTemplates", SettingsDefaults.GetDefaultReplaceTemplates()))
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
            if (SetDefaultIfMissing("Updates", "LastSeenWhatsNewVersion", string.Empty))
            {
                applied++;
            }
            if (SetDefaultIfMissing("Updates", "ShowWhatsNewAfterUpdate", true))
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
            if (SetDefaultIfMissing("Debug", "DebugDisableUpdateAction", false))
            {
                applied++;
            }

            // Инициализация настроек для каждого скрипта: уровень настроек работает
            // только с абстракцией поставщика и не знает конкретных скриптов.
            foreach (ScriptSettingsDefaults script in defaultsProvider.GetScriptDefaults())
            {
                if (script is null)
                {
                    continue;
                }

                ArgumentException.ThrowIfNullOrEmpty(script.ScriptName);

                string groupName = GetSafeGroupName(script.ScriptName);
                knownGroups.Add(groupName);
                foreach (SettingDefaultValue field in script.Fields)
                {
                    if (field is null)
                    {
                        continue;
                    }

                    if (SetDefaultIfMissing(groupName, field.Key, field.DefaultValue))
                    {
                        applied++;
                    }

                    if (field.Children != null && field.Children.Count > 0)
                    {
                        foreach (SettingDefaultValue child in field.Children)
                        {
                            if (child is null)
                            {
                                continue;
                            }

                            if (SetDefaultIfMissing(groupName, child.Key, child.DefaultValue))
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
                    foreach (SettingDefaultValue field in script.Fields)
                    {
                        if (field is null)
                        {
                            continue;
                        }

                        validKeys.Add(field.Key);
                        if (field.Children != null && field.Children.Count > 0)
                        {
                            foreach (SettingDefaultValue child in field.Children)
                            {
                                if (child is null)
                                {
                                    continue;
                                }

                                validKeys.Add(child.Key);
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

            // Секции незарегистрированных скриптов не удаляются (потеря данных),
            // а возвращаются вызывающему коду для фиксации в журнале как mismatch:
            // молчаливое хранение чужих секций маскировало бы отсутствующий скрипт.
            foreach (string cachedGroup in _cache.Keys)
            {
                if (cachedGroup.StartsWith("Script_", StringComparison.OrdinalIgnoreCase)
                    && !knownGroups.Contains(cachedGroup))
                {
                    unknownGroups.Add(cachedGroup);
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
            // Единая политика регистра: слот группы регистронезависимый, как схема.
            groupDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
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

        // Обобщённые коллекции (HashSet<T>, Queue<T> и прочие): копия того же типа
        // через метод Add, чтобы мутация выданной копии не меняла кэш в обход AreEqual.
        if (!type.IsAbstract && !type.IsInterface)
        {
            try
            {
                object? target = Activator.CreateInstance(type);
                if (target is IList targetList)
                {
                    int count = 0;
                    foreach (object? item in sequence)
                    {
                        if (count >= MaxCollectionItems)
                        {
                            break;
                        }

                        targetList.Add(CloneCore(item, depth + 1));
                        count++;
                    }

                    return targetList;
                }

                MethodInfo? addMethod = type.GetMethod("Add");
                if (target != null && addMethod != null && addMethod.GetParameters().Length == 1)
                {
                    int count = 0;
                    foreach (object? item in sequence)
                    {
                        if (count >= MaxCollectionItems)
                        {
                            break;
                        }

                        addMethod.Invoke(target, new[] { CloneCore(item, depth + 1) });
                        count++;
                    }

                    return target;
                }
            }
            catch (Exception)
            {
                // Ниже — JSON-fallback для типов без конструктора без параметров.
            }
        }

        // JSON-fallback для всех прочих IEnumerable: глубокая копия через сериализацию.
        return ClonePoco(value, type);
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
