// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using KTools_App.Core;

namespace KTools_App.UI.Pages;

/// <summary>
/// Страница для просмотра системных журналов в реальном времени с поддержкой цветовой подсветки уровней.
/// Поведение страницы полностью соответствует оригиналу: сохраняется буфер строк (2000),
/// доступна кнопка копирования в буфер обмена, а при очистке сбрасывается только экран,
/// сохраняя файлы на диске.
/// </summary>
public sealed partial class LogPage : Page
{
    private const int MaxLines = 2000;

    public LogPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Вызывается при переходе пользователя на эту страницу.
    /// Загружает существующие журналы с диска с парсингом цветов и подписывается на события логгера.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        LoadExistingLogs();

        // Подписываемся на новые логи
        LogService.LogReceived += OnLogReceived;

        LogService.Instance.DebugLog("Открыта вкладка мониторинга логов в реальном времени с поддержкой Fluent-цветов", "LogPage");
    }

    /// <summary>
    /// Вызывается при уходе пользователя со страницы.
    /// Отписывается от события поступления логов для предотвращения утечек памяти.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Отписка для защиты памяти
        LogService.LogReceived -= OnLogReceived;

        LogService.Instance.DebugLog("Пользователь покинул вкладку мониторинга логов", "LogPage");
    }

    /// <summary>
    /// Парсит существующий файл лога с диска и выполняет цветовую заливку строк в зависимости от уровня критичности.
    /// Загружает последние 1000 строк для обеспечения мгновенного отклика графического интерфейса.
    /// </summary>
    private void LoadExistingLogs()
    {
        string allLogs = LogService.Instance.ReadCurrentLog();
        if (string.IsNullOrEmpty(allLogs)) return;

        // Временно отключаем ReadOnly на время всей пакетной вставки
        LogRichEditBox.IsReadOnly = false;
        try
        {
            var document = LogRichEditBox.Document;
            document.SetText(TextSetOptions.None, string.Empty);

            string[] lines = allLogs.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
            
            // Берем последние 1000 строк для максимального быстродействия UI
            int startIdx = Math.Max(0, lines.Length - 1000);

            for (int i = startIdx; i < lines.Length; i++)
            {
                string line = lines[i];
                LogLevel level = ParseLevelFromLogLine(line);
                
                var range = document.GetRange(int.MaxValue, int.MaxValue);
                int start = range.StartPosition;
                range.Text = line + "\r";
                
                var colorRange = document.GetRange(start, start + line.Length);
                colorRange.CharacterFormat.ForegroundColor = GetColorForLogLevel(level);
            }

            ScrollToEnd();
        }
        finally
        {
            LogRichEditBox.IsReadOnly = true;
        }
    }

    /// <summary>
    /// Парсит текстовую строку лога для выявления уровня логирования по оригинальным маркерам.
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
    /// Возвращает цвет отображения текста в UI в зависимости от уровня логирования.
    /// Соответствует цветовой палитре оригинального скрипта.
    /// </summary>
    private Color GetColorForLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => Color.FromArgb(255, 128, 128, 128),   // Серый (#808080)
            LogLevel.Info => Color.FromArgb(255, 220, 220, 220),    // Светло-серый (#DCDCDC)
            LogLevel.Warning => Color.FromArgb(255, 255, 184, 0),   // Оранжево-желтый (#FFB800)
            LogLevel.Error => Color.FromArgb(255, 255, 77, 77),     // Светло-красный (#FF4D4D)
            LogLevel.Fatal => Color.FromArgb(255, 255, 0, 0),       // Ярко-красный (#FF0000)
            _ => Color.FromArgb(255, 220, 220, 220)
        };
    }

    /// <summary>
    /// Вставляет строку лога в конец документа RichEditBox с применением соответствующего форматирования цвета.
    /// Исключает вызовы GetText и переключение выделения для максимального быстродействия UI.
    /// </summary>
    private void AppendLogLine(string formattedLog, LogLevel level)
    {
        var document = LogRichEditBox.Document;
        
        // Временно отключаем ReadOnly для вставки новой строки
        LogRichEditBox.IsReadOnly = false;
        try
        {
            var range = document.GetRange(int.MaxValue, int.MaxValue);
            int start = range.StartPosition;
            range.Text = formattedLog + "\r";
            
            var colorRange = document.GetRange(start, start + formattedLog.Length);
            colorRange.CharacterFormat.ForegroundColor = GetColorForLogLevel(level);
        }
        finally
        {
            LogRichEditBox.IsReadOnly = true;
        }
    }

    /// <summary>
    /// Ограничивает количество абзацев в RichEditBox для предотвращения утечек памяти (максимум 2000 строк).
    /// </summary>
    private void LimitLines(int maxLines)
    {
        var document = LogRichEditBox.Document;
        document.GetText(TextGetOptions.None, out string text);

        string[] lines = text.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.None);
        if (lines.Length > maxLines)
        {
            int charsToRemove = 0;
            int linesToRemove = lines.Length - maxLines;
            for (int i = 0; i < linesToRemove; i++)
            {
                charsToRemove += lines[i].Length + 1; // +1 для символа переноса строки
            }

            // Временно отключаем ReadOnly для очистки старых строк из начала буфера
            LogRichEditBox.IsReadOnly = false;
            try
            {
                var range = document.GetRange(0, charsToRemove);
                range.Text = string.Empty;
            }
            finally
            {
                LogRichEditBox.IsReadOnly = true;
            }
        }
    }

    /// <summary>
    /// Выполняет безопасный перевод фокуса и скроллинг окна логов к последней записи.
    /// </summary>
    private void ScrollToEnd()
    {
        var document = LogRichEditBox.Document;
        document.GetText(TextGetOptions.None, out string text);
        int endPos = text.Length - 1;
        if (endPos < 0) endPos = 0;
        LogRichEditBox.Document.Selection.SetRange(endPos, endPos);
    }

    /// <summary>
    /// Обработчик события поступления нового лога. 
    /// Выполняет маршалинг в UI-поток через DispatcherQueue для защиты от кросс-поточных исключений.
    /// </summary>
    private void OnLogReceived(object? sender, LogReceivedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppendLogLine(e.FormattedMessage, e.Level);
            LimitLines(MaxLines);
            ScrollToEnd();
        });
    }

    /// <summary>
    /// Копирует весь текст лога из окна отображения в буфер обмена Windows.
    /// </summary>
    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LogRichEditBox.Document.GetText(TextGetOptions.None, out string text);
            if (string.IsNullOrEmpty(text) || text == "\r") return;

            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);

            LogService.Instance.DebugLog("Все строки журналов событий успешно скопированы в буфер обмена", "LogPage");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Не удалось скопировать содержимое логов: {ex.Message}", "LogPage");
        }
    }

    /// <summary>
    /// Открывает папку с файлами журналов событий на диске в Проводнике Windows.
    /// </summary>
    private void OpenLogDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string settingsDir = PathManager.GetSettingsDirectory();
            string defaultLogDir = Path.Combine(settingsDir, "logs");
            string logDir = string.IsNullOrEmpty(SettingsManager.Instance.LogDir)
                ? defaultLogDir
                : SettingsManager.Instance.LogDir;

            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            LogService.Instance.DebugLog($"Запуск Проводника Windows для папки логов: '{logDir}'", "LogPage");
            System.Diagnostics.Process.Start("explorer.exe", $"\"{logDir}\"");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Не удалось открыть директорию с файлами логов: {ex.Message}", "LogPage");
        }
    }

    /// <summary>
    /// Очищает только текущее графическое окно вывода логов на экране (в соответствии с оригиналом).
    /// Файл истории журналов на диске при этом не удаляется.
    /// </summary>
    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        // Временно отключаем ReadOnly для очистки
        LogRichEditBox.IsReadOnly = false;
        try
        {
            LogRichEditBox.Document.SetText(TextSetOptions.None, string.Empty);
        }
        finally
        {
            LogRichEditBox.IsReadOnly = true;
        }
        LogService.Instance.Info("Окно графического отображения логов очищено пользователем", "LogPage");
    }
}
