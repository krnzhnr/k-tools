// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace KTools_App.Core;

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
    /// Декларативная схема параметров настроек скрипта.
    /// По умолчанию возвращает пустой список.
    /// </summary>
    public virtual List<SettingField> SettingsSchema => new();

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

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="AbstractScript"/>
    /// и настраивает автоматическое удаление сохраненного выбора при удалении файлов из очереди.
    /// </summary>
    protected AbstractScript()
    {
        FilesQueue.CollectionChanged += (sender, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (FileQueueItem item in e.OldItems)
                {
                    SelectedTrackIds.Remove(item.FilePath);
                    SelectedAttachmentIds.Remove(item.FilePath);
                    LogService.Instance.DebugLog(
                        $"Очищен сохраненный выбор дорожек для удаленного файла: '{item.FileName}'", 
                        "AbstractScript");
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                SelectedTrackIds.Clear();
                SelectedAttachmentIds.Clear();
                LogService.Instance.DebugLog(
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

    /// <summary>
    /// Очищает список зарезервированных путей перед началом новой пакетной обработки.
    /// </summary>
    public virtual void PrepareBatch(IEnumerable<string>? inputFiles = null)
    {
        lock (_batchLock)
        {
            _batchReservedPaths.Clear();
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
    /// и коллизии имен при пакетном переименовании.
    /// </summary>
    protected string GetSafeOutputPath(string inputPath, string outputPath)
    {
        try
        {
            string inResolved = Path.GetFullPath(inputPath);
            string outResolved = Path.GetFullPath(outputPath);

            // 1. Защита исходного файла от перезаписи
            if (inResolved.Equals(outResolved, StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(outResolved) ?? "";
                string stem = Path.GetFileNameWithoutExtension(outResolved);
                string ext = Path.GetExtension(outResolved);
                outResolved = Path.Combine(dir, $"{stem}_processed{ext}");
                LogService.Instance.Info($"Защита исходника: добавлено '_processed' к имени файла: '{outResolved}'", "AbstractScript");
            }

            // 2. Защита от коллизий имен при пакетной обработке
            lock (_batchLock)
            {
                string originalDir = Path.GetDirectoryName(outResolved) ?? "";
                string originalStem = Path.GetFileNameWithoutExtension(outResolved);
                string ext = Path.GetExtension(outResolved);
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
            LogService.Instance.Exception(ex, $"Ошибка при получении безопасного выходного пути для '{inputPath}': {ex.Message}", "AbstractScript");
            return outputPath;
        }
    }

    /// <summary>
    /// Физически удаляет исходный файл с диска и заносит лог в результаты.
    /// </summary>
    protected void DeleteSource(string filePath, List<string> results)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                string msg = $"🗑 Удален исходник: {Path.GetFileName(filePath)}";
                results.Add(msg);
                LogService.Instance.Info(msg, "AbstractScript");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Не удалось удалить исходный файл '{filePath}': {ex.Message}", "AbstractScript");
            results.Add($"⚠ Не удалось удалить: {Path.GetFileName(filePath)}");
        }
    }

    /// <summary>
    /// Физически заменяет исходный файл полученным результатом с сохранением имени оригинала.
    /// </summary>
    protected bool ReplaceSourceWithResult(string sourcePath, string resultPath, List<string> results)
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
            LogService.Instance.Info(msg, "AbstractScript");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Exception(ex, $"Ошибка при подмене оригинального файла '{sourcePath}' результатом '{resultPath}': {ex.Message}", "AbstractScript");
            results.Add($"❌ Ошибка подмены: {Path.GetFileName(sourcePath)}");
            return false;
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
                LogService.Instance.DebugLog($"Удален неполный выходной файл: '{Path.GetFileName(filePath)}'", "AbstractScript");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"Не удалось удалить временный файл '{filePath}' при отмене: {ex.Message}", "AbstractScript");
        }
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
        Action<int, int, string, double?> progressCallback,
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
