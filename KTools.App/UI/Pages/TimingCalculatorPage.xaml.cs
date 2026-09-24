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

    // Маска: 0:00:00.000
    // Индексы разделителей: 1 (':'), 4 (':'), 7 ('.')
    private static readonly int[] SeparatorIndices = { 1, 4, 7 };
    private const string DefaultTime = "0:00:00.000";
    private const int MaskLength = 11;

    public TimingCalculatorPage()
    {
        InitializeComponent();
        _logService = App.Services.GetRequiredService<ILogService>();

        // Сброс фокуса при клике на свободную область страницы.
        // Используем Tapped вместо PointerPressed, чтобы сброс фокуса выполнялся ПОСЛЕ
        // завершения жеста клика (PointerReleased), а не до него.
        // AddHandler с handledEventsToo: true гарантирует перехват даже если ScrollViewer
        // или другие дочерние элементы уже пометили событие как обработанное.
        this.AddHandler(UIElement.TappedEvent, new TappedEventHandler((s, e) =>
        {
            if (!IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
            {
                this.IsTabStop = true;
                this.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }), true);

        // Начальный расчет
        UpdateCalculation();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InputsPanel == null) return;

        // Если доступная ширина меньше 560px (учитывая ширину полей 230*2 + spacing 24 + отступы 72),
        // перегруппировываем поля в вертикальную стопку.
        if (e.NewSize.Width < 560)
        {
            if (InputsPanel.Orientation != Orientation.Vertical)
            {
                InputsPanel.Orientation = Orientation.Vertical;
                InputsPanel.Spacing = 16;
                _logService.Info("[Калькулятор сдвига] Переключение макета в вертикальный режим.", "TimingCalculatorPage");
            }
        }
        else
        {
            if (InputsPanel.Orientation != Orientation.Horizontal)
            {
                InputsPanel.Orientation = Orientation.Horizontal;
                InputsPanel.Spacing = 24;
                _logService.Info("[Калькулятор сдвига] Переключение макета в горизонтальный режим.", "TimingCalculatorPage");
            }
        }
    }

    /// <summary>
    /// Проверяет, находится ли элемент внутри интерактивного контрола (TextBox, Button).
    /// Обход визуального дерева вверх необходим, так как OriginalSource события Tapped
    /// может быть внутренним дочерним элементом шаблона TextBox/Button.
    /// </summary>
    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is TextBox || current is Button)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private async void PasteBefore_Click(object sender, RoutedEventArgs e)
    {
        await PasteFromClipboardAsync(TimeBeforeBox);
    }

    private async void PasteAfter_Click(object sender, RoutedEventArgs e)
    {
        await PasteFromClipboardAsync(TimeAfterBox);
    }

    private async System.Threading.Tasks.Task PasteFromClipboardAsync(TextBox textBox)
    {
        var dataPackageView = Clipboard.GetContent();
        if (dataPackageView.Contains(StandardDataFormats.Text))
        {
            try
            {
                string text = await dataPackageView.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    text = text.Trim();
                    string? formattedTime = NormalizeTimeText(text);
                    if (formattedTime != null)
                    {
                        _logService.Info($"Вставка времени из буфера: '{text}' -> '{formattedTime}'", "TimingCalculatorPage");
                        _isUpdatingText = true;
                        textBox.Text = formattedTime;
                        _isUpdatingText = false;
                        UpdateCalculation();
                    }
                    else
                    {
                        _logService.Warn($"Некорректный формат времени в буфере обмена для вставки: '{text}'", "TimingCalculatorPage");
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Exception(ex, "Ошибка при чтении из буфера обмена", "TimingCalculatorPage");
            }
        }
    }

    public static string? NormalizeTimeText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string text = input.Trim().Replace(',', '.');

        // 1. Формат с 3 знаками миллисекунд после точки:
        // Ч:ММ:СС.ммм (длина 11, двоеточия на 1 и 4, точка на 7)
        if (text.Length == 11 && text[1] == ':' && text[4] == ':' && text[7] == '.')
        {
            if (IsDigitsOnlyExcluding(text, 1, 4, 7))
            {
                return text;
            }
        }

        // ЧЧ:ММ:СС.ммм (длина 12, двоеточия на 2 и 5, точка на 8) -> отрезаем ведущую цифру часа
        if (text.Length == 12 && text[2] == ':' && text[5] == ':' && text[8] == '.')
        {
            string sliced = text.Substring(1);
            return NormalizeTimeText(sliced);
        }

        // ММ:СС.ммм (длина 9, двоеточие на 2, точка на 5) -> добавляем "0:" в начало
        if (text.Length == 9 && text[2] == ':' && text[5] == '.')
        {
            string extended = "0:" + text;
            return NormalizeTimeText(extended);
        }

        // 2. Формат с 2 знаками (Aegisub / сотые секунды):
        // Ч:ММ:СС.сс (длина 10, двоеточия на 1 и 4, точка на 7) -> добавляем '0' на конце для 3 знаков
        if (text.Length == 10 && text[1] == ':' && text[4] == ':' && text[7] == '.')
        {
            if (IsDigitsOnlyExcluding(text, 1, 4, 7))
            {
                return text + "0";
            }
        }

        // ЧЧ:ММ:СС.сс (длина 11, двоеточия на 2 и 5, точка на 8) -> отрезаем ведущую цифру часа
        if (text.Length == 11 && text[2] == ':' && text[5] == ':' && text[8] == '.')
        {
            string sliced = text.Substring(1);
            return NormalizeTimeText(sliced);
        }

        // ММ:СС.сс (длина 8, двоеточие на 2, точка на 5) -> добавляем "0:"
        if (text.Length == 8 && text[2] == ':' && text[5] == '.')
        {
            string extended = "0:" + text;
            return NormalizeTimeText(extended);
        }

        return null;
    }

    private static bool IsDigitsOnlyExcluding(string text, params int[] excludedIndices)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (excludedIndices.Contains(i)) continue;
            if (!char.IsDigit(text[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Сброс полей в исходное состояние 0:00:00.000.
    /// </summary>
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _logService.Info("Сброс полей калькулятора сдвига", "TimingCalculatorPage");
        TimeBeforeBox.Text = DefaultTime;
        TimeAfterBox.Text = DefaultTime;
        TimeBeforeBox.Focus(FocusState.Programmatic);
        TimeBeforeBox.SelectionStart = 0;
        UpdateCalculation();
    }

    /// <summary>
    /// Копирует тайминг в формате субтитров в буфер обмена.
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
        if (sender is TextBox textBox && textBox.FocusState != FocusState.Unfocused)
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
    /// Восстановление маски при потере фокуса или некорректном значении поля.
    /// </summary>
    private void TimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            if (string.IsNullOrEmpty(textBox.Text) || textBox.Text.Length != MaskLength)
            {
                textBox.Text = DefaultTime;
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
            this.Focus(FocusState.Programmatic);
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
        if (currentText.Length != MaskLength)
        {
            textBox.Text = DefaultTime;
            currentText = DefaultTime;
            caretIndex = 0;
        }

        // Обработка ввода цифр (0-9)
        bool isDigit = (e.Key >= Windows.System.VirtualKey.Number0 && e.Key <= Windows.System.VirtualKey.Number9) ||
                       (e.Key >= Windows.System.VirtualKey.NumberPad0 && e.Key <= Windows.System.VirtualKey.NumberPad9);

        if (isDigit)
        {
            e.Handled = true;

            // Если курсор вышел за пределы строки
            if (caretIndex >= MaskLength) return;

            // Если курсор наткнулся на разделитель (':', '.') - пропускаем его
            if (SeparatorIndices.Contains(caretIndex))
            {
                caretIndex++;
            }

            if (caretIndex < MaskLength)
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
                textBox.SelectionStart = Math.Min(nextCaret, MaskLength);
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
            if (caretIndex < MaskLength)
            {
                int targetIndex = caretIndex;
                if (SeparatorIndices.Contains(targetIndex))
                {
                    targetIndex++; // Смещаемся на цифру справа
                }

                if (targetIndex < MaskLength)
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

        // Обработка навигации стрелками влево/вправо с перешагиванием разделителей
        if (e.Key == Windows.System.VirtualKey.Left)
        {
            e.Handled = true;
            int prevCaret = textBox.SelectionStart - 1;
            if (prevCaret >= 0)
            {
                if (SeparatorIndices.Contains(prevCaret))
                {
                    prevCaret--; // Перешагиваем разделитель влево
                }
                textBox.SelectionStart = Math.Max(prevCaret, 0);
            }
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Right)
        {
            e.Handled = true;
            int nextCaret = textBox.SelectionStart + 1;
            if (nextCaret <= MaskLength)
            {
                if (SeparatorIndices.Contains(nextCaret))
                {
                    nextCaret++; // Перешагиваем разделитель вправо
                }
                textBox.SelectionStart = Math.Min(nextCaret, MaskLength);
            }
            return;
        }

        // Блокируем любые другие клавиши (кроме стрелок навигации)
        bool isNavigationKey = e.Key == Windows.System.VirtualKey.Up ||
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
    /// Парсит строку времени формата Ч:ММ:СС.ммм (11 символов) или Ч:ММ:СС.сс (10 символов) в миллисекунды.
    /// </summary>
    public static long ParseTimeToMs(string timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr)) return 0;

        string clean = timeStr.Trim().Replace(',', '.');

        try
        {
            // Формат 11 символов: Ч:ММ:СС.ммм
            if (clean.Length == 11 && clean[1] == ':' && clean[4] == ':' && clean[7] == '.')
            {
                int hours = int.Parse(clean.Substring(0, 1));
                int minutes = int.Parse(clean.Substring(2, 2));
                int seconds = int.Parse(clean.Substring(5, 2));
                int milliseconds = int.Parse(clean.Substring(8, 3));

                long totalMs = ((hours * 3600L) + (minutes * 60L) + seconds) * 1000L + milliseconds;
                return totalMs;
            }

            // Формат 10 символов (Aegisub): Ч:ММ:СС.сс
            if (clean.Length == 10 && clean[1] == ':' && clean[4] == ':' && clean[7] == '.')
            {
                int hours = int.Parse(clean.Substring(0, 1));
                int minutes = int.Parse(clean.Substring(2, 2));
                int seconds = int.Parse(clean.Substring(5, 2));
                int hundredths = int.Parse(clean.Substring(8, 2));

                long totalMs = ((hours * 3600L) + (minutes * 60L) + seconds) * 1000L + (hundredths * 10L);
                return totalMs;
            }

            // Общий разбор через TimeSpan
            if (TimeSpan.TryParse(clean, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan parsedTs))
            {
                return (long)parsedTs.TotalMilliseconds;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Форматирует общее число миллисекунд в абсолютный формат времени Ч:ММ:СС.ммм с 3 знаками миллисекунд.
    /// </summary>
    public static string FormatMsToAegisub(long totalMs)
    {
        long absMs = Math.Abs(totalMs);
        long hours = absMs / 3600000L;
        long minutes = (absMs % 3600000L) / 60000L;
        long seconds = (absMs % 60000L) / 1000L;
        long millis = absMs % 1000L;

        return $"{hours}:{minutes:D2}:{seconds:D2}.{millis:D3}";
    }

    /// <summary>
    /// Производит расчет разницы и обновляет отображение результатов в UI.
    /// </summary>
    private void UpdateCalculation()
    {
        if (_isUpdatingText) return;

        string beforeText = TimeBeforeBox?.Text ?? DefaultTime;
        string afterText = TimeAfterBox?.Text ?? DefaultTime;

        long beforeMs = ParseTimeToMs(beforeText);
        long afterMs = ParseTimeToMs(afterText);

        long diffMs = afterMs - beforeMs;

        // Вывод абсолютного значения сдвига с 3 знаками миллисекунд
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
