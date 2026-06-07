// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using KTools_App.Core;
using KTools_App.ViewModels;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для страницы логов LogPage.
/// Осуществляет заполнение RichEditBox и его раскраску в зависимости от уровней логов.
/// </summary>
public sealed partial class LogPage : Page
{
    private const int MaxLines = 2000;

    /// <summary>
    /// Предоставляет доступ к модели представления страницы логов.
    /// </summary>
    public LogViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр LogPage, разрешая зависимости через DI.
    /// </summary>
    public LogPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<LogViewModel>();
    }

    /// <summary>
    /// Вызывается при переходе на эту страницу.
    /// Подгружает историю логов с диска и подписывается на события логгера.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        LoadExistingLogs();
        LogService.LogReceived += OnLogReceived;

        LogService.Instance.DebugLog("Открыта вкладка мониторинга логов в реальном времени с поддержкой Fluent-цветов", "LogPage");
    }

    /// <summary>
    /// Вызывается при уходе со страницы.
    /// Отписывается от событий для предотвращения утечек памяти.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        LogService.LogReceived -= OnLogReceived;
        LogService.Instance.DebugLog("Пользователь покинул вкладку мониторинга логов", "LogPage");
    }

    /// <summary>
    /// Загружает существующие логи из ViewModel и выполняет их раскраску в UI.
    /// </summary>
    private void LoadExistingLogs()
    {
        string allLogs = ViewModel.GetCurrentLogText();
        if (string.IsNullOrEmpty(allLogs)) return;

        LogRichEditBox.IsReadOnly = false;
        try
        {
            var document = LogRichEditBox.Document;
            document.SetText(TextSetOptions.None, string.Empty);

            string[] lines = allLogs.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
            
            // Загружаем последние 1000 строк для оптимизации отрисовки UI
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
    /// Вспомогательный метод парсинга уровня логирования из форматированной текстовой строки.
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
    /// Возвращает цвет отображения для конкретного уровня логирования.
    /// </summary>
    private Color GetColorForLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => Color.FromArgb(255, 128, 128, 128),
            LogLevel.Info => Color.FromArgb(255, 220, 220, 220),
            LogLevel.Warning => Color.FromArgb(255, 255, 184, 0),
            LogLevel.Error => Color.FromArgb(255, 255, 77, 77),
            LogLevel.Fatal => Color.FromArgb(255, 255, 0, 0),
            _ => Color.FromArgb(255, 220, 220, 220)
        };
    }

    /// <summary>
    /// Добавляет новую строку лога в конец RichEditBox.
    /// </summary>
    private void AppendLogLine(string formattedLog, LogLevel level)
    {
        var document = LogRichEditBox.Document;
        
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
    /// Ограничивает количество строк в RichEditBox для экономии ОЗУ.
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
                charsToRemove += lines[i].Length + 1;
            }

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
    /// Выполняет автопрокрутку окна вывода логов вниз.
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
    /// Обработчик прихода нового лога. Осуществляет маршалинг в UI-поток.
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
    /// Копирует весь текст лога в системный буфер обмена.
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
    /// Очищает RichEditBox.
    /// </summary>
    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
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
