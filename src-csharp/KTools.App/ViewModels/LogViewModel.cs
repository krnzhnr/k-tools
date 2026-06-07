// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KTools_App.Core;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления для страницы просмотра системных журналов (LogPage).
/// Содержит команды для управления логами, их экспорта и открытия папки логов.
/// </summary>
public partial class LogViewModel : ObservableObject
{
    private readonly LogService _logService;
    private readonly SettingsManager _settingsManager;

    /// <summary>
    /// Инициализирует новый экземпляр LogViewModel с внедрением зависимостей.
    /// </summary>
    public LogViewModel(LogService logService, SettingsManager settingsManager)
    {
        _logService = logService;
        _settingsManager = settingsManager;
    }

    /// <summary>
    /// Возвращает полный текущий текст логов с диска.
    /// </summary>
    public string GetCurrentLogText()
    {
        return _logService.ReadCurrentLog();
    }

    /// <summary>
    /// Команда для открытия директории с файлами логов в Проводнике Windows.
    /// </summary>
    [RelayCommand]
    private void OpenLogDirectory()
    {
        try
        {
            string settingsDir = PathManager.GetSettingsDirectory();
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
            _logService.Error($"Не удалось открыть директорию с файлами логов: {ex.Message}", "LogViewModel");
        }
    }
}
