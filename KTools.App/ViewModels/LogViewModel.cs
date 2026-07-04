// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Diagnostics;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Services.Contracts;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления для страницы просмотра системных журналов (LogPage).
/// Обеспечивает загрузку истории логов, динамическое обновление в реальном времени,
/// а также команды копирования, очистки и открытия каталога логов.
/// </summary>
public partial class LogViewModel : ThreadSafeViewModel
{
    private readonly ILogService _logService;
    private readonly ISettingsManager _settingsManager;
    private readonly IPathManager _pathManager;

    /// <summary>
    /// Предоставляет коллекцию записей логов для привязки к элементу управления ListView.
    /// </summary>
    public ObservableCollection<LogItem> Logs { get; } = new();

    /// <summary>
    /// Инициализирует новый экземпляр LogViewModel с внедрением зависимостей.
    /// </summary>
    public LogViewModel(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
    }

    /// <summary>
    /// Возвращает полный текущий текст логов с диска.
    /// </summary>
    public string GetCurrentLogText()
    {
        return _logService.ReadCurrentLog();
    }

    /// <summary>
    /// Загружает историю логов с диска (последние 1000 строк) для оптимизации отрисовки.
    /// </summary>
    public void LoadLogs()
    {
        try
        {
            Logs.Clear();
            string allLogs = GetCurrentLogText();
            if (string.IsNullOrEmpty(allLogs))
            {
                _logService.DebugLog("Лог-файл пуст или не инициализирован при загрузке во ViewModel", "LogViewModel");
                return;
            }

            string[] lines = allLogs.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
            
            // Загружаем последние 1000 строк для предотвращения перегрузки графического интерфейса
            int startIdx = Math.Max(0, lines.Length - 1000);

            for (int i = startIdx; i < lines.Length; i++)
            {
                string line = lines[i];
                LogLevel level = ParseLevelFromLogLine(line);
                Logs.Add(new LogItem { Message = line, Level = level });
            }
            
            _logService.DebugLog($"Успешно загружено {Logs.Count} записей истории в графическую панель", "LogViewModel");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Критическая ошибка при загрузке и разборе файла логов во ViewModel", "LogViewModel");
        }
    }

    /// <summary>
    /// Добавляет новое сообщение лога в коллекцию и удаляет старые элементы, если превышен лимит в 2000 строк.
    /// </summary>
    public void AddLog(string formattedMessage, LogLevel level)
    {
        try
        {
            Logs.Add(new LogItem { Message = formattedMessage, Level = level });
            
            // Быстрое усечение коллекции O(1)
            while (Logs.Count > 2000)
            {
                Logs.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            // Используем системную отладку для предотвращения бесконечной рекурсии в логгере
            Debug.WriteLine($"[Error] Ошибка при динамическом добавлении лога в коллекцию ViewModel: {ex.Message}");
        }
    }

    /// <summary>
    /// Анализирует строку лога для определения ее уровня критичности.
    /// </summary>
    private LogLevel ParseLevelFromLogLine(string line)
    {
        if (line.Contains("| DEBUG   |")) return LogLevel.Debug;
        if (line.Contains("| INFO    |")) return LogLevel.Info;
        if (line.Contains("| WARNING |")) return LogLevel.Warning;
        if (line.Contains("| ERROR   |")) return LogLevel.Error;
        if (line.Contains("| FATAL   |")) return LogLevel.Fatal;
        return LogLevel.Info;
    }

    /// <summary>
    /// Команда для копирования всей истории логов с диска в системный буфер обмена Windows.
    /// </summary>
    [RelayCommand]
    private void CopyAllLogs()
    {
        try
        {
            string text = GetCurrentLogText();
            if (string.IsNullOrEmpty(text))
            {
                _logService.Warn("Попытка скопировать пустые логи в буфер обмена", "LogViewModel");
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);

            _logService.DebugLog("Все строки журналов событий успешно извлечены и скопированы в буфер обмена Windows", "LogViewModel");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Критическая ошибка при попытке записи логов в системный буфер обмена", "LogViewModel");
        }
    }

    /// <summary>
    /// Команда для очистки графического окна логов и отправки информационного сообщения.
    /// </summary>
    [RelayCommand]
    private void ClearLogs()
    {
        try
        {
            Logs.Clear();
            _logService.Info("Графическое окно отображения журналов успешно очищено пользователем", "LogViewModel");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, "Не удалось выполнить очистку графического списка логов во ViewModel", "LogViewModel");
        }
    }

    /// <summary>
    /// Команда для открытия директории с файлами логов в Проводнике Windows.
    /// </summary>
    [RelayCommand]
    private void OpenLogDirectory()
    {
        try
        {
            string settingsDir = _pathManager.GetSettingsDirectory();
            string defaultLogDir = Path.Combine(settingsDir, "logs");
            string logDir = string.IsNullOrEmpty(_settingsManager.LogDir)
                ? defaultLogDir
                : _settingsManager.LogDir;

            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            _logService.DebugLog($"Запуск Проводника Windows для папки логов: '{logDir}'", "LogViewModel");
            Process.Start("explorer.exe", $"\"{logDir}\"");
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Не удалось открыть директорию с файлами логов по пути '{_settingsManager.LogDir}'", "LogViewModel");
        }
    }
}
