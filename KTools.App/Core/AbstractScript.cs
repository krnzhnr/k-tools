// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using KTools_App.Services.Contracts;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;

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

    /// <summary>
    /// Возвращает полную схему параметров, включая локальные поля переименования.
    /// </summary>
    public List<SettingField> GetFullSettingsSchema()
    {
        var schema = new List<SettingField>(SettingsSchema);
        
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

    /// <summary>
    /// Проверить, была ли отправлена команда отмены выполнения скрипта.
    /// </summary>
    public bool IsCancelled => _isCancelled;

    /// <summary>
    /// Инициировать отмену выполнения текущего скрипта.
    /// </summary>
    public virtual void Cancel()
    {
        _isCancelled = true;
    }

    /// <summary>
    /// Сбросить состояние отмены перед новым запуском пакета файлов.
    /// </summary>
    public virtual void ResetCancellation()
    {
        _isCancelled = false;
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
                    _logService.DebugLog(
                        $"Очищен сохраненный выбор дорожек для удаленного файла: '{item.FileName}'", 
                        "AbstractScript");
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                SelectedTrackIds.Clear();
                SelectedAttachmentIds.Clear();
                _logService.DebugLog(
                    "Очищен весь сохраненный выбор дорожек в связи со сбросом очереди файлов", 
                    "AbstractScript");
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

    /// <summary>
    /// Сохраненный текст журнала выполнения скрипта.
    /// </summary>
    public string SavedLogText { get; set; } = string.Empty;

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
                            _logService.Info($"Автоматически создана папка результатов: '{targetSubdir}'", "AbstractScript");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.Exception(ex, $"Не удалось автоматически создать папку результатов '{targetSubdir}': {ex.Message}", "AbstractScript");
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
                        outResolved = Path.Combine(dir, $"{stem}{ext}");
                        _logService.Info($"Применено переименование PowerRename: '{oldStem}' -> '{stem}'", "AbstractScript");
                    }
                }
                catch (Exception ex)
                {
                    _logService.Exception(ex, $"Ошибка применения переименования '{pattern}' -> '{replacement}': {ex.Message}", "AbstractScript");
                }
            }

            // 1. Защита исходного файла от перезаписи
            if (inResolved.Equals(outResolved, StringComparison.OrdinalIgnoreCase))
            {
                outResolved = Path.Combine(dir, $"{stem}_processed{ext}");
                _logService.Info($"Защита исходника: добавлено '_processed' к имени файла: '{outResolved}'", "AbstractScript");
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
            _logService.Exception(ex, $"Ошибка при получении безопасного выходного пути для '{inputPath}': {ex.Message}", "AbstractScript");
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
                    string resolvedReplacement = EvaluatePowerRenameVariables(replacement, fileNum, DateTime.Now);

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
                }
                catch
                {
                    // Игнорируем ошибки при предпросмотре
                }
            }

            outResolved = Path.Combine(dir, $"{stem}{ext}");

            if (inResolved.Equals(outResolved, StringComparison.OrdinalIgnoreCase))
            {
                outResolved = Path.Combine(dir, $"{stem}_processed{ext}");
            }

            return outResolved;
        }
        catch
        {
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
    /// Физически удаляет исходный файл с диска и заносит лог в результаты.
    /// При возникновении ошибок доступа (например, файл занят другим процессом) выполняется несколько попыток повтора с задержкой.
    /// </summary>
    protected void DeleteSource(string filePath, List<string> results)
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
                    string msg = $"🗑 Удален исходник: {Path.GetFileName(filePath)}";
                    results.Add(msg);
                    _logService.Info(msg, "AbstractScript");
                    return;
                }
            }
            catch (IOException ioEx) when (attempt < maxRetries)
            {
                string lockingInfo = FileLockDetector.GetLockingProcessesInfo(filePath, _logService);
                string procSuffix = string.IsNullOrEmpty(lockingInfo) ? "процесс неизвестен" : $"заблокирован процессами: {lockingInfo}";
                _logService.Warn($"Попытка удаления исходника {attempt}/{maxRetries} не удалась (файл занят, {procSuffix}): {ioEx.Message}. Повторная попытка через {delayMs} мс.", "AbstractScript");
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, $"Критическая ошибка при удалении исходного файла '{filePath}': {ex.Message}", "AbstractScript");
                results.Add($"⚠ Не удалось удалить: {Path.GetFileName(filePath)}");
                return;
            }
        }

        string finalLockInfo = FileLockDetector.GetLockingProcessesInfo(filePath, _logService);
        string finalProcStr = string.IsNullOrEmpty(finalLockInfo) ? "процесс неизвестен" : $"занят процессами: {finalLockInfo}";
        string failMsg = $"⚠ Не удалось удалить: {Path.GetFileName(filePath)} после {maxRetries} попыток ({finalProcStr}).";
        results.Add(failMsg);
        _logService.Error($"Не удалось физически удалить исходный файл '{filePath}' после {maxRetries} попыток. Файл {finalProcStr}.", "AbstractScript");
    }

    /// <summary>
    /// Физически заменяет исходный файл полученным результатом с сохранением имени оригинала.
    /// При возникновении ошибок доступа (например, файл занят другим процессом) выполняется несколько попыток повтора с задержкой.
    /// </summary>
    protected bool ReplaceSourceWithResult(string sourcePath, string resultPath, List<string> results)
    {
        const int maxRetries = 5;
        const int delayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    File.Delete(sourcePath);
                }
                File.Move(resultPath, sourcePath);

                string msg = $"🔄 Подменен оригинал: {Path.GetFileName(sourcePath)}";
                results.Add(msg);
                _logService.Info(msg, "AbstractScript");
                return true;
            }
            catch (IOException ioEx) when (attempt < maxRetries)
            {
                string lockingInfo = FileLockDetector.GetLockingProcessesInfo(sourcePath, _logService);
                string procSuffix = string.IsNullOrEmpty(lockingInfo) ? "процесс неизвестен" : $"заблокирован процессами: {lockingInfo}";
                _logService.Warn($"Попытка подмены оригинала {attempt}/{maxRetries} не удалась (файл занят, {procSuffix}): {ioEx.Message}. Повторная попытка через {delayMs} мс.", "AbstractScript");
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, $"Ошибка при подмене оригинального файла '{sourcePath}' результатом '{resultPath}': {ex.Message}", "AbstractScript");
                results.Add($"❌ Ошибка подмены: {Path.GetFileName(sourcePath)}");
                return false;
            }
        }

        string finalLockInfo = FileLockDetector.GetLockingProcessesInfo(sourcePath, _logService);
        string finalProcStr = string.IsNullOrEmpty(finalLockInfo) ? "процесс неизвестен" : $"занят процессами: {finalLockInfo}";
        string failMsg = $"❌ Ошибка подмены: {Path.GetFileName(sourcePath)} ({finalProcStr})";
        results.Add(failMsg);
        _logService.Error($"Не удалось подменить оригинальный файл '{sourcePath}' результатом '{resultPath}' после {maxRetries} попыток. Файл {finalProcStr}.", "AbstractScript");
        return false;
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
                _logService.DebugLog($"Удален неполный выходной файл: '{Path.GetFileName(filePath)}'", "AbstractScript");
            }
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось удалить временный файл '{filePath}' при отмене: {ex.Message}", "AbstractScript");
        }
    }

    /// <summary>
    /// Физически удаляет выходной файл при возникновении любых ошибок или сбоев в процессе выполнения.
    /// Позволяет избежать засорения диска поврежденными или частично записанными медиафайлами.
    /// </summary>
    /// <param name="filePath">Абсолютный путь к неудавшимся выходному файлу.</param>
    protected void CleanupFailedOutputFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logService.DebugLog($"Удален поврежденный выходной файл после сбоя или ошибки: '{Path.GetFileName(filePath)}'", "AbstractScript");
            }
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Не удалось очистить поврежденный выходной файл '{filePath}' после сбоя: {ex.Message}", "AbstractScript");
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
    public abstract Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount);

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
