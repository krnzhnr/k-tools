// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;

using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;

using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Core;

/// <summary>
/// Делегат обратного вызова для уведомления о прогрессе выполнения скрипта.
/// </summary>
/// <param name="fileIndex">Индекс обрабатываемого файла в очереди.</param>
/// <param name="totalCount">Общее количество файлов в очереди.</param>
/// <param name="status">Текстовый статус выполнения.</param>
/// <param name="percent">Процент выполнения для текущего файла.</param>
/// <param name="fps">Текущая скорость обработки в кадрах в секунду (для видео).</param>
/// <param name="bitrate">Текущий битрейт потока.</param>
public delegate void ScriptProgressCallback(
    int fileIndex,
    int totalCount,
    string status,
    double? percent,
    double? fps = null,
    string? bitrate = null
);

/// <summary>
/// Абстрактный базовый класс для всех скриптов обработки файлов в K-Tools.
/// Определяет контракт метаданных, зависимостей и асинхронного выполнения.
/// </summary>
public abstract class AbstractScript
{
    protected const string ScriptSource = nameof(AbstractScript);
    private volatile bool _isCancelled;

    /// <summary>
    /// Отображаемое на русском языке имя скрипта.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Описание назначения скрипта и его ключевых особенностей.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    /// Категория скрипта (например, "Видео", "Аудио", "Контейнеры", "Субтитры").
    /// </summary>
    public abstract string Category { get; }

    /// <summary>
    /// Имя иконки для Fluent-отображения.
    /// </summary>
    public abstract string IconName { get; }

    /// <summary>
    /// Список поддерживаемых расширений файлов в нижнем регистре с точкой (например, .mkv, .mp4).
    /// </summary>
    public abstract string[] FileExtensions { get; }

    /// <summary>
    /// Название первой вкладки в рабочей панели (по умолчанию "Файлы").
    /// </summary>
    public virtual string FirstTabHeader => "Файлы";

    /// <summary>
    /// Указывает, нужно ли отображать панель ввода URL над списком файлов.
    /// </summary>
    public virtual bool ShowUrlInputBar => false;

    /// <summary>
    /// Декларативная схема параметров настроек скрипта.
    /// </summary>
    public virtual List<SettingField> SettingsSchema => new();

    public virtual List<SettingField> GetSettingsSchema(Dictionary<string, object>? currentSettings = null)
    {
        return SettingsSchema;
    }

    /// <summary>
    /// Возвращает полную схему параметров, включая локальные поля переименования.
    /// Поддерживает динамическое обновление схемы на основе текущих выбранных параметров.
    /// </summary>
    public virtual List<SettingField> GetFullSettingsSchema(Dictionary<string, object>? currentSettings = null)
    {
        var schema = new List<SettingField>(GetSettingsSchema(currentSettings));

        // Добавляем вкладку "Переименование" с полями локального переопределения
        schema.Add(new SettingField(
            "LocalRenameOverride",
            "Переопределить глобальное переименование",
            SettingType.Checkbox,
            false,
            "Переименование"));

        schema.Add(new SettingField(
            "LocalRenameUseRegex",
            "Использовать регулярные выражения",
            SettingType.Checkbox,
            true,
            "Переименование",
            visibleIfKey: "LocalRenameOverride",
            visibleIfValues: new List<string> { "True" }));

        schema.Add(new SettingField(
            "LocalRenameCaseSensitive",
            "Учитывать регистр",
            SettingType.Checkbox,
            false,
            "Переименование",
            visibleIfKey: "LocalRenameOverride",
            visibleIfValues: new List<string> { "True" }));

        schema.Add(new SettingField(
            "LocalRenameSearch",
            "Локальный поиск",
            SettingType.Text,
            string.Empty,
            "Переименование",
            visibleIfKey: "LocalRenameOverride",
            visibleIfValues: new List<string> { "True" })
        {
            PlaceholderText = "Например:  - (\\d+) или просто текст"
        });

        schema.Add(new SettingField(
            "LocalRenameReplace",
            "Локальная замена",
            SettingType.Text,
            string.Empty,
            "Переименование",
            visibleIfKey: "LocalRenameOverride",
            visibleIfValues: new List<string> { "True" })
        {
            PlaceholderText = "Например:  - [$1] или серия_${num:2}"
        });

        return schema;
    }

    /// <summary>
    /// Указывает, поддерживает ли скрипт одновременную параллельную обработку файлов.
    /// </summary>
    public virtual bool SupportsParallel => false;

    /// <summary>
    /// Указывает, использует ли скрипт кастомный виджет выбора дорожек (например, TreeView).
    /// </summary>
    public virtual bool UseCustomWidget => false;

    /// <summary>
    /// Список строковых ключей внешних зависимостей, необходимых для работы скрипта.
    /// </summary>
    public virtual string[] RequiredDependencies => Array.Empty<string>();

    private System.Threading.CancellationTokenSource _cts = new();

    /// <summary>
    /// Токен отмены выполнения текущего скрипта.
    /// </summary>
    public System.Threading.CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Проверить, была ли отправлена команда отмены выполнения скрипта.
    /// </summary>
    public bool IsCancelled => _isCancelled || _cts.IsCancellationRequested;

    /// <summary>
    /// Инициировать отмену выполнения текущего скрипта.
    /// </summary>
    public virtual void Cancel()
    {
        _isCancelled = true;
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch (Exception ex)
        {
            _logService?.Write(
                "script.cancellation_token.dispose_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Токен отмены операции не освобождён",
                ex,
                GetType().Name,
                properties: LogProps
                    .Create("ErrorCode", "CANCELLATION_TOKEN_DISPOSE_FAILED")
                    .With("CleanupState", "NotStarted"));
        }
    }

    /// <summary>
    /// Сбросить состояние отмены перед новым запуском пакета файлов.
    /// </summary>
    public virtual void ResetCancellation()
    {
        _isCancelled = false;
        try
        {
            _cts.Dispose();
        }
        catch { }
        _cts = new System.Threading.CancellationTokenSource();
    }

    protected ILogService _logService { get; }
    protected ISettingsManager _settingsManager { get; }
    protected IPathManager _pathManager { get; }

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="AbstractScript"/> с внедрением зависимостей
    /// и настраивает автоматическое удаление сохраненного выбора при удалении файлов из очереди.
    /// </summary>
    protected AbstractScript(ILogService logService, ISettingsManager settingsManager, IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));

        FilesQueue.CollectionChanged += (sender, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (FileQueueItem item in e.OldItems)
                {
                    SelectedTrackIds.Remove(item.FilePath);
                    SelectedAttachmentIds.Remove(item.FilePath);
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                if (SelectedTrackIds.Count > 0 || SelectedAttachmentIds.Count > 0)
                {
                    int cleared = SelectedTrackIds.Count + SelectedAttachmentIds.Count;
                    SelectedTrackIds.Clear();
                    SelectedAttachmentIds.Clear();
                    _logService.Write(
                        "script.selection.cleared",
                        LogLevel.Debug,
                        LogStatus.Changed,
                        "Сохранённый выбор дорожек и вложений очищен из-за сброса очереди файлов",
                        source: ScriptSource,
                        properties: LogProps.Create("Count", cleared));
                }
            }
        };
    }

    /// <summary>
    /// Словарь выбранных дорожек для файлов (путь -> список ID дорожек).
    /// </summary>
    public Dictionary<string, List<int>> SelectedTrackIds { get; } = new();

    /// <summary>
    /// Словарь выбранных вложений для файлов (путь -> список ID вложений).
    /// </summary>
    public Dictionary<string, List<int>> SelectedAttachmentIds { get; } = new();

    /// <summary>
    /// Очередь файлов скрипта, сохраняющаяся между переходами.
    /// </summary>
    public ObservableCollection<FileQueueItem> FilesQueue { get; } = new();

    private readonly System.Text.StringBuilder _logBuilder = new();
    private readonly object _logLock = new();

    /// <summary>
    /// Сохраненный текст журнала выполнения скрипта.
    /// </summary>
    public string SavedLogText
    {
        get { lock (_logLock) { return _logBuilder.ToString(); } }
        set { lock (_logLock) { _logBuilder.Clear().Append(value ?? string.Empty); } }
    }

    /// <summary>
    /// Добавляет сообщение в журнал без материализации всей строки (O(1) на сообщение).
    /// </summary>
    public void AppendToLog(string message)
    {
        lock (_logLock) { _logBuilder.Append(message); }
    }

    public void ClearSavedLog()
    {
        lock (_logLock) { _logBuilder.Clear(); }
    }

    /// <summary>
    /// Добавляет несколько строк в журнал одной атомарной операцией
    /// (для параллельной обработки файлов строки не перемешиваются).
    /// </summary>
    public void AppendLinesToLog(IEnumerable<string> lines)
    {
        lock (_logLock)
        {
            foreach (var line in lines)
            {
                _logBuilder.Append(line);
                _logBuilder.Append(Environment.NewLine);
            }
        }
    }

    /// <summary>
    /// Оставляет только последние <paramref name="keepChars"/> символов журнала,
    /// если его длина превысила <paramref name="maxChars"/> (аналог прежней логики обрезки).
    /// </summary>
    public void TrimSavedLogToTail(int maxChars, int keepChars)
    {
        if (maxChars < 0 || keepChars < 0)
        {
            return;
        }

        lock (_logLock)
        {
            if (_logBuilder.Length > maxChars)
            {
                int removeCount = _logBuilder.Length - Math.Min(keepChars, _logBuilder.Length);
                if (removeCount > 0)
                {
                    _logBuilder.Remove(0, removeCount);
                }
            }
        }
    }

    /// <summary>
    /// Возвращает длину накопленного журнала в символах.
    /// </summary>
    public int SavedLogLength
    {
        get { lock (_logLock) { return _logBuilder.Length; } }
    }

    /// <summary>
    /// Сохраненный глобальный текстовый статус медиаобработки.
    /// </summary>
    public string SavedStatusText { get; set; } = "Ожидание запуска...";

    /// <summary>
    /// Сохраненное значение интегрального прогресс-бара.
    /// </summary>
    public double SavedGlobalProgress { get; set; }

    /// <summary>
    /// Сохраненный пользовательский выходной путь.
    /// </summary>
    public string SavedOutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Указывает, выполняется ли скрипт в данный момент.
    /// </summary>
    public bool IsProcessing { get; set; }

    /// <summary>
    /// Событие изменения состояния выполнения скрипта.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Вызывает событие изменения состояния для подписчиков.
    /// </summary>
    public void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private readonly object _batchLock = new();
    private readonly HashSet<string> _batchReservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private int _batchRenameCounter = 0;

    /// <summary>
    /// Очищает список зарезервированных путей перед началом новой пакетной обработки.
    /// </summary>
    public virtual void PrepareBatch(IEnumerable<string>? inputFiles = null)
    {
        lock (_batchLock)
        {
            _batchReservedPaths.Clear();
            _batchRenameCounter = 0;
            if (inputFiles != null)
            {
                foreach (var file in inputFiles)
                {
                    _batchReservedPaths.Add(Path.GetFullPath(file));
                }
            }
        }
    }

    /// <summary>
    /// Применяет правила переименования PowerRename (локальные для скрипта или глобальные) к имени файла (без расширения).
    /// </summary>
    /// <param name="stem">Имя файла без расширения.</param>
    /// <param name="fileNum">Порядковый номер файла в очереди пакетной обработки.</param>
    /// <param name="settings">Словарь переопределенных настроек (опционально).</param>
    /// <returns>Преобразованное имя файла без расширения.</returns>
    public string ApplyPowerRename(string stem, int fileNum, Dictionary<string, object>? settings = null)
    {
        if (string.IsNullOrEmpty(stem)) return stem;

        bool renameEnabled = false;
        bool useRegex = true;
        bool caseSensitive = false;
        string pattern = "";
        string replacement = "";

        string settingsGroup = _settingsManager.GetSafeGroupName(Name);
        bool localOverride = settings != null
            ? GetSettingValue(settings, "LocalRenameOverride", false)
            : _settingsManager.GetSetting(settingsGroup, "LocalRenameOverride", false);

        if (localOverride)
        {
            pattern = settings != null
                ? GetSettingValue(settings, "LocalRenameSearch", string.Empty)
                : _settingsManager.GetSetting(settingsGroup, "LocalRenameSearch", string.Empty);
            replacement = settings != null
                ? GetSettingValue(settings, "LocalRenameReplace", string.Empty)
                : _settingsManager.GetSetting(settingsGroup, "LocalRenameReplace", string.Empty);
            useRegex = settings != null
                ? GetSettingValue(settings, "LocalRenameUseRegex", true)
                : _settingsManager.GetSetting(settingsGroup, "LocalRenameUseRegex", true);
            caseSensitive = settings != null
                ? GetSettingValue(settings, "LocalRenameCaseSensitive", false)
                : _settingsManager.GetSetting(settingsGroup, "LocalRenameCaseSensitive", false);
            renameEnabled = !string.IsNullOrEmpty(pattern);
        }
        else
        {
            // Используем глобальные настройки
            renameEnabled = _settingsManager.RenameEnableRegex;
            pattern = _settingsManager.RenameRegexSearch;
            replacement = _settingsManager.RenameRegexReplace;
            useRegex = _settingsManager.RenameUseRegex;
            caseSensitive = _settingsManager.RenameCaseSensitive;
        }

        if (renameEnabled && !string.IsNullOrEmpty(pattern))
        {
            try
            {
                string oldStem = stem;

                // 1. Сначала вычисляем все переменные форматирования (даты, uuid, нумерацию) в строке замены
                string resolvedReplacement = EvaluatePowerRenameVariables(replacement, fileNum, DateTime.Now);

                // 2. Выполняем поиск и замену (через Regex или стандартный текст)
                if (useRegex)
                {
                    var options = caseSensitive
                        ? System.Text.RegularExpressions.RegexOptions.None
                        : System.Text.RegularExpressions.RegexOptions.IgnoreCase;

                    stem = System.Text.RegularExpressions.Regex.Replace(stem, pattern, resolvedReplacement, options);
                }
                else
                {
                    var options = caseSensitive
                        ? System.Text.RegularExpressions.RegexOptions.None
                        : System.Text.RegularExpressions.RegexOptions.IgnoreCase;

                    stem = System.Text.RegularExpressions.Regex.Replace(stem, System.Text.RegularExpressions.Regex.Escape(pattern), resolvedReplacement, options);
                }

                if (oldStem != stem)
                {
                    _logService.Write(
                        "script.rename.applied",
                        LogLevel.Debug,
                        LogStatus.Changed,
                        "К имени выходного файла применено правило переименования",
                        source: ScriptSource,
                        properties: LogProps
                            .Create("OutputName", LogProps.FileName(stem))
                            .With("Index", fileNum));
                }
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "script.rename.failed",
                    LogLevel.Warning,
                    LogStatus.Skipped,
                    "Правило переименования не применено, исходное имя выходного файла сохранено",
                    ex,
                    ScriptSource,
                    properties: LogProps
                        .Create("ErrorCode", "RENAME_RULE_FAILED")
                        .With("OutputName", LogProps.FileName(stem)));
            }
        }

        return stem;
    }

    /// <summary>
    /// Возвращает безопасный путь для сохранения результата, предотвращая перезапись исходника
    /// и коллизии имен при пакетном переименовании. Поддерживает переименование по правилам PowerRename.
    /// </summary>
    protected string GetSafeOutputPath(string inputPath, string outputPath, Dictionary<string, object>? settings = null)
    {
        try
        {
            string inResolved = Path.GetFullPath(inputPath);
            string outResolved = Path.GetFullPath(outputPath);

            string dir = Path.GetDirectoryName(outResolved) ?? "";
            string stem = Path.GetFileNameWithoutExtension(outResolved);
            string ext = Path.GetExtension(outResolved);

            // Обработка автоматического создания подпапки результатов
            if (_settingsManager.UseAutoSubfolder)
            {
                string inputDir = Path.GetDirectoryName(inResolved) ?? "";

                // Если пользователь не выбрал кастомный путь или выбрал ту же папку, что и исходный файл
                if (string.IsNullOrEmpty(dir) || dir.Equals(inputDir, StringComparison.OrdinalIgnoreCase))
                {
                    string subfolderName = _settingsManager.DefaultOutputSubfolder;
                    if (string.IsNullOrWhiteSpace(subfolderName))
                    {
                        subfolderName = "KTools_Result";
                    }

                    string targetSubdir = Path.Combine(inputDir, subfolderName);
                    try
                    {
                        if (!Directory.Exists(targetSubdir))
                        {
                            Directory.CreateDirectory(targetSubdir);
                            _logService.Write(
                                "script.output.subfolder_created",
                                LogLevel.Debug,
                                LogStatus.Succeeded,
                                "Автоматически создана папка для результатов обработки",
                                source: ScriptSource,
                                properties: LogProps
                                    .Create("FileName", LogProps.FileName(targetSubdir))
                                    .With("InputName", LogProps.FileName(inResolved)));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.Write(
                            "script.output.subfolder_failed",
                            LogLevel.Warning,
                            LogStatus.Failed,
                            "Не удалось создать папку для результатов обработки",
                            ex,
                            ScriptSource,
                            properties: LogProps
                                .Create("FileName", LogProps.FileName(targetSubdir))
                                .With("ErrorCode", "OUTPUT_SUBFOLDER_FAILED"));
                    }

                    dir = targetSubdir;
                    outResolved = Path.Combine(dir, $"{stem}{ext}");
                }
            }

            // Получаем порядковый номер файла в текущей пакетной обработке
            int fileNum = 1;
            lock (_batchLock)
            {
                _batchRenameCounter++;
                fileNum = _batchRenameCounter;
            }

            // Переименование выходных файлов (PowerRename логика)
            string renamedStem = ApplyPowerRename(stem, fileNum, settings);
            if (renamedStem != stem)
            {
                stem = renamedStem;
                outResolved = Path.Combine(dir, $"{stem}{ext}");
            }

            // 1. Защита исходного файла от перезаписи
            if (inResolved.Equals(outResolved, StringComparison.OrdinalIgnoreCase))
            {
                outResolved = Path.Combine(dir, $"{stem}_processed{ext}");
                _logService.Write(
                    "script.output.collision_avoided",
                    LogLevel.Debug,
                    LogStatus.Changed,
                    "Выходной путь совпал с исходным, к имени результата добавлен суффикс _processed",
                    source: ScriptSource,
                    properties: LogProps
                        .Create("InputName", LogProps.FileName(inResolved))
                        .With("OutputName", LogProps.FileName(outResolved)));
            }

            // 2. Защита от коллизий имен при пакетной обработке
            lock (_batchLock)
            {
                string originalDir = Path.GetDirectoryName(outResolved) ?? "";
                string originalStem = Path.GetFileNameWithoutExtension(outResolved);
                ext = Path.GetExtension(outResolved);
                int counter = 1;

                while (_batchReservedPaths.Contains(outResolved))
                {
                    outResolved = Path.Combine(originalDir, $"{originalStem}_{counter}{ext}");
                    counter++;
                }

                _batchReservedPaths.Add(outResolved);
            }

            return outResolved;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.output.path_resolve_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                $"Не удалось определить безопасный выходной путь для '{LogProps.FileName(inputPath)}', используется исходный путь результата",
                ex,
                ScriptSource,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(inputPath))
                    .With("ErrorCode", "OUTPUT_PATH_RESOLVE_FAILED"));
            return outputPath;
        }
    }

    /// <summary>
    /// Возвращает выходное расширение файла на основе настроек скрипта (например, .mp4, .vtt).
    /// </summary>
    public virtual string GetOutputExtension(string inputPath)
    {
        return Path.GetExtension(inputPath);
    }

    /// <summary>
    /// Возвращает гипотетический выходной путь для предпросмотра переименования (без изменения состояния).
    /// </summary>
    public string GetPreviewOutputPath(string inputPath, string outputPath, int fileNum, Dictionary<string, object>? settings = null)
    {
        try
        {
            string inResolved = Path.GetFullPath(inputPath);
            string outResolved = Path.GetFullPath(outputPath);

            string dir = Path.GetDirectoryName(outResolved) ?? "";
            string stem = Path.GetFileNameWithoutExtension(outResolved);
            string ext = GetOutputExtension(inputPath);

            stem = ApplyPowerRename(stem, fileNum, settings);

            outResolved = Path.Combine(dir, $"{stem}{ext}");

            if (inResolved.Equals(outResolved, StringComparison.OrdinalIgnoreCase))
            {
                outResolved = Path.Combine(dir, $"{stem}_processed{ext}");
            }

            return outResolved;
        }
        catch (Exception ex)
        {
            _logService?.Write(
                "script.output.preview_path_resolve_failed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Предпросмотр имени результата недоступен, используется исходное имя",
                ex,
                ScriptSource,
                properties: LogProps.Create("ErrorCode", "PREVIEW_PATH_RESOLVE_FAILED"));
            return outputPath;
        }
    }

    /// <summary>
    /// Парсит и заменяет переменные форматирования (PowerRename логика) в строке замены.
    /// </summary>
    private string EvaluatePowerRenameVariables(string replacement, int fileNum, DateTime time)
    {
        if (string.IsNullOrEmpty(replacement)) return replacement;

        // Генерация UUID
        replacement = replacement.Replace("${ruuidv4}", Guid.NewGuid().ToString(), StringComparison.OrdinalIgnoreCase);

        // Временные метки
        replacement = replacement.Replace("${YYYY}", time.ToString("yyyy"), StringComparison.OrdinalIgnoreCase);
        replacement = replacement.Replace("${MM}", time.ToString("MM"), StringComparison.OrdinalIgnoreCase);
        replacement = replacement.Replace("${DD}", time.ToString("dd"), StringComparison.OrdinalIgnoreCase);
        replacement = replacement.Replace("${hh}", time.ToString("HH"), StringComparison.OrdinalIgnoreCase);
        replacement = replacement.Replace("${mm}", time.ToString("mm"), StringComparison.OrdinalIgnoreCase);
        replacement = replacement.Replace("${ss}", time.ToString("ss"), StringComparison.OrdinalIgnoreCase);

        // Автонумерация ${num} и ${num:N}
        replacement = System.Text.RegularExpressions.Regex.Replace(replacement, @"\$\{num\}", fileNum.ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        replacement = System.Text.RegularExpressions.Regex.Replace(replacement, @"\$\{num:(\d+)\}", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out int pad))
            {
                return fileNum.ToString().PadLeft(pad, '0');
            }
            return fileNum.ToString();
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return replacement;
    }

    /// <summary>
    /// Физически удаляет исходный файл с диска и заносит лог в результаты (асинхронно).
    /// При возникновении ошибок доступа (например, файл занят другим процессом) выполняется несколько попыток повтора с задержкой.
    /// </summary>
    protected async Task DeleteSourceAsync(string filePath, List<string> results)
    {
        const int maxRetries = 5;
        const int delayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    string msg = $"🗑 Исходный файл удалён: {LogProps.FileName(filePath)}";
                    results.Add(msg);
                    _logService.Write(
                        "script.source.deleted",
                        LogLevel.Info,
                        LogStatus.Succeeded,
                        $"Исходный файл '{LogProps.FileName(filePath)}' удалён",
                        source: ScriptSource,
                        properties: LogProps
                            .Create("InputName", LogProps.FileName(filePath))
                            .With("Verified", true));
                    return;
                }
            }
            catch (IOException ioEx) when (attempt < maxRetries)
            {
                string lockingInfo = FileLockDetector.GetLockingProcessesInfo(filePath, _logService);
                _logService.Write(
                    "script.source.delete_retry",
                    LogLevel.Warning,
                    LogStatus.RetryScheduled,
                    $"Исходный файл занят, запланирована повторная попытка удаления {attempt}/{maxRetries} через {delayMs} мс",
                    ioEx,
                    ScriptSource,
                    properties: LogProps
                        .Create("ErrorCode", "SOURCE_FILE_BUSY")
                        .With("InputName", LogProps.FileName(filePath))
                        .With("Attempt", attempt)
                        .With("MaxAttempts", maxRetries)
                        .With("ElapsedMs", delayMs)
                        .With("Retryable", true)
                        .With("Count", string.IsNullOrEmpty(lockingInfo) ? 0 : 1));
                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "script.source.delete_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    $"Не удалось удалить исходный файл '{LogProps.FileName(filePath)}'",
                    ex,
                    ScriptSource,
                    properties: LogProps
                        .Create("InputName", LogProps.FileName(filePath))
                        .With("ErrorCode", "SOURCE_DELETE_FAILED")
                        .With("Retryable", false));
                results.Add($"⚠ Не удалось удалить: {LogProps.FileName(filePath)}");
                return;
            }
        }

        string finalLockInfo = FileLockDetector.GetLockingProcessesInfo(filePath, _logService);
        string finalProcStr = string.IsNullOrEmpty(finalLockInfo) ? "процесс неизвестен" : $"занят процессами: {finalLockInfo}";
        string failMsg = $"⚠ Не удалось удалить: {LogProps.FileName(filePath)} после {maxRetries} попыток ({finalProcStr}).";
        results.Add(failMsg);
        _logService.Write(
            "script.source.delete_exhausted",
            LogLevel.Error,
            LogStatus.Failed,
            $"Исходный файл '{LogProps.FileName(filePath)}' не удалён после {maxRetries} попыток, файл {finalProcStr}",
            source: ScriptSource,
            properties: LogProps
                .Create("InputName", LogProps.FileName(filePath))
                .With("ErrorCode", "SOURCE_DELETE_EXHAUSTED")
                .With("MaxAttempts", maxRetries)
                .With("Retryable", false)
                .With("CleanupState", "NotStarted"));
    }

    /// <summary>
    /// Физически заменяет исходный файл полученным результатом с сохранением имени оригинала (асинхронно).
    /// Исходный файл никогда не удаляется до гарантированной подмены: сначала проверяется наличие результата,
    /// затем применяется атомарная замена (File.Replace) либо безопасная последовательность
    /// «перенос исходника во временную резервную копию → перенос результата → удаление резервной копии»
    /// с восстановлением исходника при любой ошибке.
    /// При возникновении ошибок доступа (например, файл занят другим процессом) выполняется несколько попыток повтора с задержкой.
    /// </summary>
    /// <param name="sourcePath">Путь к заменяемому исходному файлу.</param>
    /// <param name="resultPath">Путь к подготовленному файлу результата.</param>
    /// <param name="results">Список сообщений журнала выполнения.</param>
    /// <returns>true, если подмена выполнена; иначе false, причём исходный файл остаётся на диске.</returns>
    protected async Task<bool> ReplaceSourceWithResultAsync(string sourcePath, string resultPath, List<string> results)
    {
        const int maxRetries = 5;
        const int delayMs = 500;

        if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
        {
            string missingMsg = $"❌ Ошибка замены: результат '{LogProps.FileName(resultPath)}' не найден, исходник сохранён";
            results.Add(missingMsg);
            _logService.Write(
                "script.source.replace_result_missing",
                LogLevel.Error,
                LogStatus.Failed,
                "Исходный файл не заменён: подготовленный результат не найден",
                source: ScriptSource,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(sourcePath))
                    .With("OutputName", LogProps.FileName(resultPath))
                    .With("ErrorCode", "REPLACE_RESULT_MISSING")
                    .With("CleanupState", "SourcePreserved"));
            return false;
        }

        string? backupPath = null;
        try
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    ReplaceFilesAtomically(sourcePath, resultPath, ref backupPath);
                    TryDeleteBackupFile(backupPath);
                    backupPath = null;

                    string msg = $"🔄 Исходный файл заменён результатом обработки: {LogProps.FileName(sourcePath)}";
                    results.Add(msg);
                    _logService.Write(
                        "script.source.replaced",
                        LogLevel.Info,
                        LogStatus.Succeeded,
                        $"Исходный файл '{LogProps.FileName(sourcePath)}' заменён результатом обработки '{LogProps.FileName(resultPath)}'",
                        source: ScriptSource,
                        properties: LogProps
                            .Create("InputName", LogProps.FileName(sourcePath))
                            .With("OutputName", LogProps.FileName(sourcePath))
                            .With("Verified", true)
                            .With("CleanupState", "BackupRemoved"));
                    return true;
                }
                catch (IOException ioEx)
                {
                    if (attempt < maxRetries)
                    {
                        string lockingInfo = FileLockDetector.GetLockingProcessesInfo(sourcePath, _logService);
                        _logService.Write(
                            "script.source.replace_retry",
                            LogLevel.Warning,
                            LogStatus.RetryScheduled,
                            $"Исходный файл занят, запланирована повторная попытка замены {attempt}/{maxRetries} через {delayMs} мс",
                            ioEx,
                            ScriptSource,
                            properties: LogProps
                                .Create("ErrorCode", "SOURCE_FILE_BUSY")
                                .With("InputName", LogProps.FileName(sourcePath))
                                .With("Attempt", attempt)
                                .With("MaxAttempts", maxRetries)
                                .With("ElapsedMs", delayMs)
                                .With("Retryable", true)
                                .With("Count", string.IsNullOrEmpty(lockingInfo) ? 0 : 1));
                        await Task.Delay(delayMs);
                        continue;
                    }

                    _logService.Write(
                        "script.source.replace_exhausted_attempts",
                        LogLevel.Error,
                        LogStatus.Failed,
                        $"Исходный файл '{LogProps.FileName(sourcePath)}' не удалось заменить после {maxRetries} попыток",
                        ioEx,
                        ScriptSource,
                        properties: LogProps
                            .Create("ErrorCode", "SOURCE_REPLACE_LOCKED")
                            .With("InputName", LogProps.FileName(sourcePath))
                            .With("MaxAttempts", maxRetries)
                            .With("Retryable", false)
                            .With("CleanupState", "SourcePreserved"));
                    break;
                }
            }

            string finalLockInfo = FileLockDetector.GetLockingProcessesInfo(sourcePath, _logService);
            string finalProcStr = string.IsNullOrEmpty(finalLockInfo) ? "процесс неизвестен" : $"занят процессами: {finalLockInfo}";
            string failMsg = $"❌ Ошибка замены: {LogProps.FileName(sourcePath)} ({finalProcStr}), исходник сохранён";
            results.Add(failMsg);
            _logService.Write(
                "script.source.replace_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Исходный файл '{LogProps.FileName(sourcePath)}' не заменён результатом '{LogProps.FileName(resultPath)}' после {maxRetries} попыток, файл {finalProcStr}. Исходный файл сохранён.",
                source: ScriptSource,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(sourcePath))
                    .With("OutputName", LogProps.FileName(resultPath))
                    .With("ErrorCode", "SOURCE_REPLACE_FAILED")
                    .With("MaxAttempts", maxRetries)
                    .With("Retryable", false)
                    .With("CleanupState", "SourcePreserved"));
            return false;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.source.replace_error",
                LogLevel.Error,
                LogStatus.Failed,
                $"Не удалось заменить исходный файл '{LogProps.FileName(sourcePath)}' результатом '{LogProps.FileName(resultPath)}', исходник сохранён",
                ex,
                ScriptSource,
                properties: LogProps
                    .Create("InputName", LogProps.FileName(sourcePath))
                    .With("OutputName", LogProps.FileName(resultPath))
                    .With("ErrorCode", "SOURCE_REPLACE_ERROR")
                    .With("Retryable", false)
                    .With("CleanupState", "SourcePreserved"));
            results.Add($"❌ Ошибка замены: {LogProps.FileName(sourcePath)}, исходник сохранён");
            return false;
        }
        finally
        {
            RestoreBackupFile(backupPath, sourcePath);
        }
    }

    /// <summary>
    /// Выполняет подмену исходника результатом, не позволяя потерять исходные данные:
    /// приоритет — атомарная операция <see cref="File.Replace(string, string, string?, bool)"/>,
    /// при её недоступности исходник переносится во временную резервную копию рядом с ним,
    /// и возврат выполняется только после успешного переноса результата.
    /// </summary>
    private static void ReplaceFilesAtomically(string sourcePath, string resultPath, ref string? backupPath)
    {
        if (!File.Exists(sourcePath))
        {
            File.Move(resultPath, sourcePath);
            return;
        }

        bool sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(sourcePath)),
            Path.GetPathRoot(Path.GetFullPath(resultPath)),
            StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            try
            {
                File.Replace(resultPath, sourcePath, null, true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        string? directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        string backup = Path.Combine(
            directory,
            $"{Path.GetFileName(sourcePath)}.ktools-replace-{Guid.NewGuid():N}.bak");
        File.Move(sourcePath, backup);
        backupPath = backup;

        try
        {
            File.Move(resultPath, sourcePath);
        }
        catch
        {
            RestoreBackupFile(backup, sourcePath);
            backupPath = null;
            throw;
        }
    }

    private static void RestoreBackupFile(string? backupPath, string sourcePath)
    {
        if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            if (!File.Exists(sourcePath))
            {
                File.Move(backupPath, sourcePath);
            }
            else
            {
                File.Delete(backupPath);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteBackupFile(string? backupPath)
    {
        if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Delete(backupPath);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Удаляет незавершенные выходные файлы при прерывании процесса.
    /// </summary>
    protected void CleanupIfCancelled(string filePath)
    {
        if (!IsCancelled) return;

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logService.Write(
                    "script.output.cancelled_cleanup",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Неполный выходной файл удалён после отмены операции",
                    source: ScriptSource,
                    properties: LogProps
                        .Create("OutputName", LogProps.FileName(filePath))
                        .With("CleanupState", "Removed"));
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.output.cancelled_cleanup_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось удалить временный выходной файл при отмене операции",
                ex,
                ScriptSource,
                properties: LogProps
                    .Create("OutputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "CANCELLED_CLEANUP_FAILED")
                    .With("CleanupState", "Failed"));
        }
    }

    /// <summary>
    /// Физически удаляет выходной файл при возникновении любых ошибок или сбоев в процессе выполнения.
    /// Позволяет избежать засорения диска поврежденными или частично записанными медиафайлами.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к неудавшимся выходному файлу.</param>
    protected async Task CleanupFailedOutputFileAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (!File.Exists(filePath)) break;
                try
                {
                    File.Delete(filePath);
                    _logService.Write(
                        "script.output.failed_cleanup",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        "Повреждённый выходной файл удалён после сбоя обработки",
                        source: ScriptSource,
                        properties: LogProps
                            .Create("OutputName", LogProps.FileName(filePath))
                            .With("CleanupState", "Removed"));
                    break;
                }
                catch
                {
                    if (attempt == 5) throw;
                    await Task.Delay(150);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "script.output.failed_cleanup_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось очистить повреждённый выходной файл после сбоя обработки",
                ex,
                ScriptSource,
                properties: LogProps
                    .Create("OutputName", LogProps.FileName(filePath))
                    .With("ErrorCode", "FAILED_OUTPUT_CLEANUP_FAILED")
                    .With("CleanupState", "Failed"));
        }
    }

    /// <summary>
    /// Фильтрует список файлов очереди, возвращая только те, которые будут обработаны как основные единицы работы.
    /// По умолчанию возвращает все файлы. Скрипты, объединяющие несколько файлов (например, сборка MKV),
    /// должны переопределить этот метод, чтобы исключить сопутствующие файлы из счётчика обработки.
    /// </summary>
    /// <param name="allFiles">Полный список файлов в очереди.</param>
    /// <returns>Список файлов, являющихся основными единицами обработки.</returns>
    public virtual List<FileQueueItem> GetProcessableFiles(List<FileQueueItem> allFiles)
    {
        return allFiles;
    }

    /// <summary>
    /// Асинхронный запуск обработки одного файла.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к файлу.</param>
    /// <param name="settings">Словарь текущих настроек пользователя.</param>
    /// <param name="outputPath">Директория сохранения результата.</param>
    /// <param name="progressCallback">Делегат для отправки прогресса (индекс, всего, сообщение, процент).</param>
    /// <param name="fileIndex">Порядковый индекс обрабатываемого файла в очереди.</param>
    /// <param name="totalCount">Общее число файлов в очереди.</param>
    /// <returns>Список сообщений о результатах выполнения (ошибки, успехи, пути подмены).</returns>
    public abstract Task<ExecutionResult> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount,
        ExecutionContext context);

    public Task<ExecutionResult> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        ExecutionContext batch = ExecutionContext.CreateBatch(GetType().Name, Math.Max(totalCount, 0));
        ExecutionContext context = totalCount > 0 && fileIndex >= 0 && fileIndex < totalCount
            ? batch.ForItem(fileIndex)
            : batch;
        return ExecuteSingleAsync(
            filePath,
            settings,
            outputPath,
            progressCallback,
            fileIndex,
            totalCount,
            context);
    }

    protected static ExecutionResult CreateExecutionResult(
        ExecutionContext context,
        ExecutionStatus status,
        IEnumerable<string> messages,
        string? errorCode = null,
        int? exitCode = null,
        string? outputFile = null,
        bool? outputExists = null,
        Exception? exception = null,
        bool retryable = false,
        CleanupState cleanupState = CleanupState.Unknown,
        double durationMs = 0)
    {
        return ExecutionResult.Create(
            context,
            status,
            messages,
            errorCode,
            exitCode,
            outputFile,
            outputExists,
            exceptionInfo: exception is null ? null : ExceptionInfo.FromException(exception),
            exception: exception,
            retryable: retryable,
            cleanupState: cleanupState,
            durationMs: durationMs);
    }

    /// <summary>
    /// Безопасно извлекает значение параметра из словаря настроек.
    /// Поддерживает автоматическую конвертацию JsonElement.
    /// </summary>
    protected T GetSettingValue<T>(
        Dictionary<string, object> settings,
        string key,
        T defaultValue)
    {
        if (settings == null) return defaultValue;

        if (settings.TryGetValue(key, out var val))
        {
            try
            {
                if (val is System.Text.Json.JsonElement jsonElem)
                {
                    if (typeof(T) == typeof(bool))
                    {
                        return (T)(object)(jsonElem.ValueKind ==
                            System.Text.Json.JsonValueKind.True);
                    }
                    if (typeof(T) == typeof(int))
                    {
                        return (T)(object)jsonElem.GetInt32();
                    }
                    if (typeof(T) == typeof(string))
                    {
                        return (T)(object)jsonElem.GetString()!;
                    }

                    var deserialized = jsonElem.Deserialize<T>();
                    if (deserialized != null)
                    {
                        return deserialized;
                    }
                }
                else if (val is T typedVal)
                {
                    return typedVal;
                }
                else
                {
                    return (T)Convert.ChangeType(val, typeof(T));
                }
            }
            catch (Exception)
            {
                // Игнорируем ошибки приведения
            }
        }
        return defaultValue;
    }
}
