// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using KTools_App.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace KTools_App.UI.Pages;

/// <summary>
/// Страница «Калькулятор сдвига таймингов» с поддержкой маскированного ввода времени в стиле Aegisub.
/// </summary>
public sealed partial class TimingCalculatorPage : Page
{
    private readonly ILogService _logService;
    private bool _isUpdatingText = false;

    // Маска: 0:00:00.00
    // Индексы разделителей: 1 (':'), 4 (':'), 7 ('.')
    private static readonly int[] SeparatorIndices = { 1, 4, 7 };

    public TimingCalculatorPage()
    {
        InitializeComponent();
        _logService = App.Services.GetRequiredService<ILogService>();
        
        // Сброс фокуса при клике на свободную область страницы
        this.PointerPressed += (s, e) =>
        {
            this.IsTabStop = true;
            this.Focus(FocusState.Programmatic);
            this.IsTabStop = false;
        };

        // Начальный расчет
        UpdateCalculation();
    }

    /// <summary>
    /// Сброс полей в исходное состояние 0:00:00.00.
    /// </summary>
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _logService.Info("Сброс полей калькулятора сдвига", "TimingCalculatorPage");
        TimeBeforeBox.Text = "0:00:00.00";
        TimeAfterBox.Text = "0:00:00.00";
        TimeBeforeBox.Focus(FocusState.Programmatic);
        TimeBeforeBox.SelectionStart = 0;
        UpdateCalculation();
    }

    /// <summary>
    /// Копирует тайминг в формате Aegisub в буфер обмена.
    /// </summary>
    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        string textToCopy = ResultTimeBlock.Text;
        if (!string.IsNullOrEmpty(textToCopy))
        {
            var package = new DataPackage();
            package.SetText(textToCopy);
            Clipboard.SetContent(package);
            _logService.Info($"Результат сдвига '{textToCopy}' скопирован в буфер обмена", "TimingCalculatorPage");
        }
    }

    /// <summary>
    /// Копирует значение сдвига в миллисекундах в буфер обмена.
    /// </summary>
    private void CopyMs_Click(object sender, RoutedEventArgs e)
    {
        // Извлекаем только числовое значение миллисекунд
        string rawText = ResultMsBlock.Text;
        string cleanMs = new string(rawText.Where(char.IsDigit).ToArray());
        if (rawText.StartsWith("-"))
        {
            cleanMs = "-" + cleanMs;
        }

        if (!string.IsNullOrEmpty(cleanMs))
        {
            var package = new DataPackage();
            package.SetText(cleanMs);
            Clipboard.SetContent(package);
            _logService.Info($"Сдвиг в миллисекундах '{cleanMs}' скопирован в буфер обмена", "TimingCalculatorPage");
        }
    }

    /// <summary>
    /// Обработчик фокуса: если курсор находится на разделителе, смещаем его.
    /// </summary>
    private void TimeBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            int selectionStart = textBox.SelectionStart;
            // Корректируем положение курсора, чтобы он не застревал на разделителях при клике мышкой
            if (SeparatorIndices.Contains(selectionStart))
            {
                // Смещаем курсор вперед
                textBox.SelectionStart = selectionStart + 1;
            }
        }
    }

    /// <summary>
    /// Восстановление маски при потере фокуса или пустом поле.
    /// </summary>
    private void TimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            if (string.IsNullOrEmpty(textBox.Text) || textBox.Text.Length != 10)
            {
                textBox.Text = "0:00:00.00";
            }
            UpdateCalculation();
        }
    }

    /// <summary>
    /// Перехват клавиш ввода для реализации поведения перезаписи (overwrite) в стиле Aegisub.
    /// </summary>
    private void TimeBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        // Сброс фокуса при нажатии клавиши Enter
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            this.IsTabStop = true;
            this.Focus(FocusState.Programmatic);
            this.IsTabStop = false;
            e.Handled = true;
            return;
        }

        // Разрешаем стандартные управляющие сочетания клавиш (Ctrl+C, Ctrl+V, Tab)
        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (isCtrlDown || e.Key == Windows.System.VirtualKey.Tab)
        {
            return;
        }

        int caretIndex = textBox.SelectionStart;
        string currentText = textBox.Text;

        // Гарантируем корректность маски перед обработкой
        if (currentText.Length != 10)
        {
            textBox.Text = "0:00:00.00";
            currentText = "0:00:00.00";
            caretIndex = 0;
        }

        // Обработка ввода цифр (0-9)
        bool isDigit = (e.Key >= Windows.System.VirtualKey.Number0 && e.Key <= Windows.System.VirtualKey.Number9) ||
                       (e.Key >= Windows.System.VirtualKey.NumberPad0 && e.Key <= Windows.System.VirtualKey.NumberPad9);

        if (isDigit)
        {
            e.Handled = true;

            // Если курсор вышел за пределы строки
            if (caretIndex >= 10) return;

            // Если курсор наткнулся на разделитель (':', '.') - пропускаем его
            if (SeparatorIndices.Contains(caretIndex))
            {
                caretIndex++;
            }

            if (caretIndex < 10)
            {
                char digitChar = GetDigitChar(e.Key);
                
                // Перезаписываем символ в текущей позиции
                char[] chars = currentText.ToCharArray();
                chars[caretIndex] = digitChar;
                
                _isUpdatingText = true;
                textBox.Text = new string(chars);
                _isUpdatingText = false;

                // Смещаем курсор на следующую позицию
                int nextCaret = caretIndex + 1;
                if (SeparatorIndices.Contains(nextCaret))
                {
                    nextCaret++; // Перешагиваем разделитель
                }
                textBox.SelectionStart = Math.Min(nextCaret, 10);
            }

            UpdateCalculation();
            return;
        }

        // Обработка клавиши Backspace (заменяет предыдущую цифру на '0' и смещает курсор влево)
        if (e.Key == Windows.System.VirtualKey.Back)
        {
            e.Handled = true;
            if (caretIndex > 0)
            {
                int prevCaret = caretIndex - 1;
                if (SeparatorIndices.Contains(prevCaret))
                {
                    prevCaret--; // Перешагиваем разделитель влево
                }

                if (prevCaret >= 0)
                {
                    char[] chars = currentText.ToCharArray();
                    chars[prevCaret] = '0';
                    
                    _isUpdatingText = true;
                    textBox.Text = new string(chars);
                    _isUpdatingText = false;

                    textBox.SelectionStart = prevCaret;
                }
            }
            UpdateCalculation();
            return;
        }

        // Обработка клавиши Delete (заменяет текущую цифру на '0' и оставляет курсор на месте)
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            if (caretIndex < 10)
            {
                int targetIndex = caretIndex;
                if (SeparatorIndices.Contains(targetIndex))
                {
                    targetIndex++; // Смещаемся на цифру справа
                }

                if (targetIndex < 10)
                {
                    char[] chars = currentText.ToCharArray();
                    chars[targetIndex] = '0';

                    _isUpdatingText = true;
                    textBox.Text = new string(chars);
                    _isUpdatingText = false;

                    textBox.SelectionStart = targetIndex;
                }
            }
            UpdateCalculation();
            return;
        }

        // Блокируем любые другие клавиши (кроме стрелок навигации)
        bool isNavigationKey = e.Key == Windows.System.VirtualKey.Left ||
                              e.Key == Windows.System.VirtualKey.Right ||
                              e.Key == Windows.System.VirtualKey.Up ||
                              e.Key == Windows.System.VirtualKey.Down ||
                              e.Key == Windows.System.VirtualKey.Home ||
                              e.Key == Windows.System.VirtualKey.End;

        if (!isNavigationKey)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Конвертация клавиши в соответствующий символ цифры.
    /// </summary>
    private static char GetDigitChar(Windows.System.VirtualKey key)
    {
        if (key >= Windows.System.VirtualKey.Number0 && key <= Windows.System.VirtualKey.Number9)
        {
            return (char)('0' + (key - Windows.System.VirtualKey.Number0));
        }
        if (key >= Windows.System.VirtualKey.NumberPad0 && key <= Windows.System.VirtualKey.NumberPad9)
        {
            return (char)('0' + (key - Windows.System.VirtualKey.NumberPad0));
        }
        return '0';
    }

    /// <summary>
    /// Парсит строку времени формата Ч:ММ:СС.сс в общее количество миллисекунд.
    /// </summary>
    public static long ParseTimeToMs(string timeStr)
    {
        if (string.IsNullOrEmpty(timeStr) || timeStr.Length != 10) return 0;

        try
        {
            int hours = int.Parse(timeStr.Substring(0, 1));
            int minutes = int.Parse(timeStr.Substring(2, 2));
            int seconds = int.Parse(timeStr.Substring(5, 2));
            int hundredths = int.Parse(timeStr.Substring(8, 2));

            long totalMs = ((hours * 3600L) + (minutes * 60L) + seconds) * 1000L + (hundredths * 10L);
            return totalMs;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Форматирует общее число миллисекунд в абсолютный формат времени Aegisub (Ч:ММ:СС.сс).
    /// </summary>
    public static string FormatMsToAegisub(long totalMs)
    {
        long absMs = Math.Abs(totalMs);
        long hours = absMs / 3600000L;
        long minutes = (absMs % 3600000L) / 60000L;
        long seconds = (absMs % 60000L) / 1000L;
        long hundredths = (absMs % 1000L) / 10L;

        return $"{hours}:{minutes:D2}:{seconds:D2}.{hundredths:D2}";
    }

    /// <summary>
    /// Производит расчет разницы и обновляет отображение результатов в UI.
    /// </summary>
    private void UpdateCalculation()
    {
        if (_isUpdatingText) return;

        string beforeText = TimeBeforeBox?.Text ?? "0:00:00.00";
        string afterText = TimeAfterBox?.Text ?? "0:00:00.00";

        long beforeMs = ParseTimeToMs(beforeText);
        long afterMs = ParseTimeToMs(afterText);

        long diffMs = afterMs - beforeMs;

        // Вывод абсолютного значения сдвига в формате Aegisub
        if (ResultTimeBlock != null)
        {
            ResultTimeBlock.Text = FormatMsToAegisub(diffMs);
        }

        // Вывод сдвига в миллисекундах
        if (ResultMsBlock != null)
        {
            ResultMsBlock.Text = $"{diffMs} ms";
        }

        // Определение направления сдвига и обновление индикаторов
        if (DirectionTextBlock != null && DirectionIcon != null && DirectionPanel != null)
        {
            if (diffMs > 0)
            {
                // Вперед
                DirectionIcon.Glyph = "\uE72A"; // Forward
                DirectionIcon.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                DirectionTextBlock.Text = "Вперед";
            }
            else if (diffMs < 0)
            {
                // Назад
                DirectionIcon.Glyph = "\uE72B"; // Back
                DirectionIcon.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
                DirectionTextBlock.Text = "Назад";
            }
            else
            {
                // Нет изменений
                DirectionIcon.Glyph = "\uE73E"; // CheckMark
                DirectionIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                DirectionTextBlock.Text = "Сдвиг отсутствует";
            }
        }
    }
}
