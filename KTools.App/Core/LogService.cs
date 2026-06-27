// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Уровни детализации логирования.
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

/// <summary>
/// Потокобезопасный синглтон-сервис логирования.
/// Обеспечивает посуточную ротацию лог-файлов на диске, дублирование в Debug,
/// автоматическую очистку устаревших файлов логов старше 14 дней и
/// трансляцию новых событий логирования в графический интерфейс в реальном времени.
/// </summary>
public sealed class LogService : ILogService
{
    private static readonly Lazy<LogService> LazyInstance =
        new(() => new LogService());

    private readonly object _lock = new();
    private string _currentLogFile = string.Empty;
    private string _customLogDir = string.Empty;

    private LogService()
    {
        InitializeLogFile();
    }

    /// <summary>
    /// Глобальная точка доступа к единственному экземпляру класса LogService.
    /// </summary>
    public static LogService Instance => LazyInstance.Value;

    /// <summary>
    /// Событие, возникающее при записи нового лог-сообщения.
    /// Передает сформированную форматированную строку лога и уровень критичности.
    /// </summary>
    public static event EventHandler<LogReceivedEventArgs>? LogReceived;

    /// <summary>
    /// Инициализировать путь к файлу лога и очистить старые логи.
    /// </summary>
    public void InitializeLogFile()
    {
        InitializeLogFile(null);
    }

    /// <summary>
    /// Инициализировать путь к файлу лога с возможностью указания пользовательской директории.
    /// </summary>
    /// <param name="customLogDir">Пользовательский путь к директории логов.</param>
    public void InitializeLogFile(string? customLogDir)
    {
        lock (_lock)
        {
            try
            {
                if (customLogDir != null)
                {
                    _customLogDir = customLogDir;
                }

                // Определяем папку логов (настройки пользователя или папка по умолчанию)
                string settingsDir = PathManager.GetSettingsDirectory();
                string defaultLogDir = Path.Combine(settingsDir, "logs");

                string logDir = string.IsNullOrEmpty(_customLogDir)
                    ? defaultLogDir
                    : _customLogDir;

                // Попытка использовать заданную папку логов
                try
                {
                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Если пользовательская папка логов недоступна (например, в Program Files при MSIX),
                    // используем папку логов по умолчанию в LocalAppData
                    Debug.WriteLine($"[Warning] Нет доступа к папке логов {logDir}, используется папка по умолчанию");
                    logDir = defaultLogDir;

                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                }

                // Уникальный файл лога для каждого запуска на основе даты и времени
                string timestampStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentLogFile = Path.Combine(logDir, $"ktools_{timestampStr}.log");

                // Ротация логов: удаляем файлы старше 10 дней (как в оригинале)
                CleanOldLogs(logDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Не удалось инициализировать логгер: {ex.Message}");
            }
        }
    }

    private void CleanOldLogs(string logDir)
    {
        try
        {
            if (!Directory.Exists(logDir)) return;

            string[] files = Directory.GetFiles(logDir, "ktools_*.log");
            DateTime cutoff = DateTime.Now.AddDays(-10);

            foreach (string file in files)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoff)
                {
                    fileInfo.Delete();
                    Debug.WriteLine($"[Info] Ротация логов: удалён старый файл {fileInfo.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] Ошибка очистки старых файлов логов: {ex.Message}");
        }
    }

    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="level">Уровень критичности.</param>
    /// <param name="message">Сообщение лога.</param>
    /// <param name="source">Источник события (класс / компонент).</param>
    public void Log(LogLevel level, string message, string source = "System")
    {
        string levelStr = level.ToString().ToUpperInvariant().PadRight(7);
        string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string formatted = $"{timeStr} | {levelStr} | {source.PadRight(20)} | {message}";

        lock (_lock)
        {
            // 1. Вывод в консоль отладчика IDE
            Debug.WriteLine(formatted);

            // 2. Запись в текущий файл лога запуска
            try
            {
                if (string.IsNullOrEmpty(_currentLogFile))
                {
                    InitializeLogFile();
                }

                // Гарантируем наличие директории перед записью
                string? dir = Path.GetDirectoryName(_currentLogFile);
                if (dir != null && !Directory.Exists(dir))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // Если нет доступа на запись, выводим в Debug и продолжаем без записи на диск
                        Debug.WriteLine($"[Error] Нет доступа для создания папки логов {dir}: {ex.Message}");
                        goto SkipFileWrite;
                    }
                }

                File.AppendAllText(_currentLogFile, formatted + Environment.NewLine, Encoding.UTF8);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[Error] Нет доступа для записи лога на диск: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Не удалось записать лог на диск: {ex.Message}");
            }

            SkipFileWrite:
            ;
        }

        // 3. Вызываем событие для трансляции в графический интерфейс
        LogReceived?.Invoke(null, new LogReceivedEventArgs(formatted, level));
    }

    public void DebugLog(string message, string source = "System") => Log(LogLevel.Debug, message, source);
    public void Info(string message, string source = "System") => Log(LogLevel.Info, message, source);
    public void Warn(string message, string source = "System") => Log(LogLevel.Warning, message, source);
    public void Error(string message, string source = "System") => Log(LogLevel.Error, message, source);
    public void Fatal(string message, string source = "System") => Log(LogLevel.Fatal, message, source);

    /// <summary>
    /// Записать исключение в лог с подробным стеком вызовов на русском языке.
    /// Обеспечивает детальную регистрацию всех непредвиденных сбоев.
    /// </summary>
    /// <param name="ex">Объект перехваченного исключения.</param>
    /// <param name="message">Сопутствующий русскоязычный контекст ошибки.</param>
    /// <param name="source">Компонент-источник сбоя.</param>
    public void Exception(Exception ex, string message, string source = "System")
    {
        string fullMessage = $"{message}. Ошибка: {ex.Message}{Environment.NewLine}Стек вызовов: {ex.StackTrace}";
        Log(LogLevel.Error, fullMessage, source);
    }

    /// <summary>
    /// Прочитать весь текст из текущего посуточного лог-файла.
    /// </summary>
    /// <returns>Строка с текстом лога или пустая строка.</returns>
    public string ReadCurrentLog()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_currentLogFile))
                {
                    return File.ReadAllText(_currentLogFile, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Не удалось прочитать текущий лог-файл: {ex.Message}");
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// Полностью очистить текущий посуточный лог-файл на диске.
    /// </summary>
    public void ClearCurrentLog()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_currentLogFile))
                {
                    File.WriteAllText(_currentLogFile, string.Empty, Encoding.UTF8);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[Error] Нет доступа для очистки лог-файла: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Не удалось очистить текущий лог-файл: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Аргументы события поступления нового лог-сообщения.
/// </summary>
public sealed class LogReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Форматированная строка лога.
    /// </summary>
    public string FormattedMessage { get; }

    /// <summary>
    /// Уровень логирования.
    /// </summary>
    public LogLevel Level { get; }

    public LogReceivedEventArgs(string formattedMessage, LogLevel level)
    {
        FormattedMessage = formattedMessage;
        Level = level;
    }
}
