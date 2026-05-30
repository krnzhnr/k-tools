// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
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
    /// Сохраненная очередь файлов с их индивидуальными
    /// статусами и прогрессом.
    /// </summary>
    public List<SavedFileState> SavedFiles { get; } = new();

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
