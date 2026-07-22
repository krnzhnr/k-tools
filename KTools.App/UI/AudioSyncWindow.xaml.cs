// -*- coding: utf-8 -*-
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Services.Contracts;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace KTools_App.UI;

/// <summary>
/// Окно визуальной синхронизации двух аудиодорожек с нативной отрисовкой через Win2D Direct2D GPU.
/// Все комментарии и логи исключительно на русском языке в соответствии с регламентом.
/// </summary>
public sealed partial class AudioSyncWindow : Window
{
    private readonly IAudioWaveformService _waveformService;
    private readonly string _destFilePath;
    private readonly int _destAudioIndex;
    private readonly string _sourceFilePath;
    private readonly int _sourceAudioIndex;

    private WaveformLevelData? _destWaveform;
    private WaveformLevelData? _sourceWaveform;

    private double _viewOffsetSeconds = 0.0;
    private double _pixelsPerSecond = 100.0; // Базовый масштаб: 100 пикселей на 1 секунду

    private bool _isDragging = false;
    private Windows.Foundation.Point _lastMousePosition;

    private double _userShiftMs = 0.0;
    private const double AacPrimingDelayMs = 21.333333333333332; // 1024 / 48000 * 1000

    /// <summary>
    /// Результат сдвига (в мс), с учетом компенсации AAC (-21.33 мс).
    /// </summary>
    public double FinalCalculatedShiftMs => _userShiftMs - AacPrimingDelayMs;

    /// <summary>
    /// Прямой пользовательский сдвиг (в мс), заданный графически.
    /// </summary>
    public int UserShiftMs => (int)Math.Round(_userShiftMs);

    /// <summary>
    /// Подтвердил ли пользователь выбор в диалоговом окне.
    /// </summary>
    public bool IsConfirmed { get; private set; } = false;

    public AudioSyncWindow(
        IAudioWaveformService waveformService,
        string destFilePath,
        int destAudioIndex,
        string sourceFilePath,
        int sourceAudioIndex)
    {
        InitializeComponent();

        _waveformService = waveformService ?? throw new ArgumentNullException(nameof(waveformService));
        _destFilePath = destFilePath ?? throw new ArgumentException("Путь к целевому файлу не может быть пустым.", nameof(destFilePath));
        _destAudioIndex = destAudioIndex;
        _sourceFilePath = sourceFilePath ?? throw new ArgumentException("Путь к исходному файлу не может быть пустым.", nameof(sourceFilePath));
        _sourceAudioIndex = sourceAudioIndex;

        UpdateNetOffsetLabel();

        // Запуск асинхронной загрузки при открытии
        _ = LoadWaveformsAsync();
    }

    /// <summary>
    /// Фоновая извлечение и построение пиков осциллограмм для обеих дорожек.
    /// </summary>
    private async Task LoadWaveformsAsync()
    {
        try
        {
            LoadingRing.IsActive = true;
            ApplyButton.IsEnabled = false;

            StatusTextBlock.Text = "Извлечение целевого аудио...";
            _destWaveform = await _waveformService.ExtractAndGeneratePeaksAsync(
                _destFilePath,
                _destAudioIndex,
                (pct, status) => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = $"Цель [{pct:F0}%]: {status}"));

            StatusTextBlock.Text = "Извлечение пересаживаемого аудио...";
            _sourceWaveform = await _waveformService.ExtractAndGeneratePeaksAsync(
                _sourceFilePath,
                _sourceAudioIndex,
                (pct, status) => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = $"Пересадка [{pct:F0}%]: {status}"));

            StatusTextBlock.Text = "Осциллограммы загружены.";
            LoadingRing.IsActive = false;
            ApplyButton.IsEnabled = true;

            // Запрос перерисовки Win2D
            SourceCanvas.Invalidate();
            TransplantCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            StatusTextBlock.Text = $"Ошибка загрузки: {ex.Message}";
        }
    }

    private void SourceCanvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
    }

    private void TransplantCanvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
    }

    /// <summary>
    /// Отрисовка целевой дорожки (Оригинал).
    /// </summary>
    private void SourceCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawWaveformOnCanvas(sender, args, _destWaveform, ColorHelper.FromArgb(255, 0, 229, 255), 0.0);
    }

    /// <summary>
    /// Отрисовка пересаживаемой дорожки с учетом пользовательского сдвига.
    /// </summary>
    private void TransplantCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        double shiftSeconds = _userShiftMs / 1000.0;
        DrawWaveformOnCanvas(sender, args, _sourceWaveform, ColorHelper.FromArgb(255, 255, 64, 129), shiftSeconds);
    }

    /// <summary>
    /// Универсальный нативный рендеринг осциллограммы на GPU через Win2D Direct2D.
    /// </summary>
    /// <summary>
    /// Универсальный нативный рендеринг осциллограммы на GPU через Win2D Direct2D класса Adobe Audition.
    /// </summary>
    private void DrawWaveformOnCanvas(CanvasControl sender, CanvasDrawEventArgs args, WaveformLevelData? data, Color waveColor, double shiftSeconds)
    {
        var ds = args.DrawingSession;
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;

        ds.Clear(ColorHelper.FromArgb(255, 16, 18, 22));

        if (width <= 0 || height <= 0) return;

        float centerY = height / 2.0f;
        float centerX = width / 2.0f;

        // Отрисовка фоновой миллисекундной сетки хронометража в стиле Audition
        DrawTimeRulerGrid(ds, width, height, centerX);

        // Линия нулевой амплитуды
        ds.DrawLine(0, centerY, width, centerY, ColorHelper.FromArgb(80, 255, 255, 255), 1.0f);

        if (data == null) return;

        // Динамический выбор 5-уровневой LOD пирамиды (до 1000 Гц / 1 мс на отсчет)
        WaveformPeak[] peaks;
        double peaksPerSecond;

        if (_pixelsPerSecond > 2000)
        {
            peaks = data.Peaks1000Hz;
            peaksPerSecond = 1000.0;
        }
        else if (_pixelsPerSecond > 500)
        {
            peaks = data.Peaks200Hz;
            peaksPerSecond = 200.0;
        }
        else if (_pixelsPerSecond > 100)
        {
            peaks = data.Peaks50Hz;
            peaksPerSecond = 50.0;
        }
        else if (_pixelsPerSecond > 20)
        {
            peaks = data.Peaks10Hz;
            peaksPerSecond = 10.0;
        }
        else
        {
            peaks = data.Peaks1Hz;
            peaksPerSecond = 1.0;
        }

        if (peaks.Length == 0) return;

        double startTimeSeconds = _viewOffsetSeconds - (centerX / _pixelsPerSecond) - shiftSeconds;
        double endTimeSeconds = _viewOffsetSeconds + (centerX / _pixelsPerSecond) - shiftSeconds;

        int startIndex = Math.Max(0, (int)Math.Floor(startTimeSeconds * peaksPerSecond));
        int endIndex = Math.Min(peaks.Length - 1, (int)Math.Ceiling(endTimeSeconds * peaksPerSecond));

        float strokeWidth = (float)Math.Clamp(_pixelsPerSecond / peaksPerSecond * 0.9, 1.0, 4.0);
        Color fillColor = ColorHelper.FromArgb(50, waveColor.R, waveColor.G, waveColor.B);

        for (int i = startIndex; i <= endIndex; i++)
        {
            double peakTimeSec = (i / peaksPerSecond) + shiftSeconds;
            double screenX = centerX + ((peakTimeSec - _viewOffsetSeconds) * _pixelsPerSecond);

            if (screenX < -5 || screenX > width + 5) continue;

            var peak = peaks[i];
            float topY = centerY - (peak.Max * (height * 0.44f));
            float bottomY = centerY - (peak.Min * (height * 0.44f));

            if (Math.Abs(topY - bottomY) < 1.0f)
            {
                bottomY = topY + 1.0f;
            }

            // Заливка полупрозрачной огибающей амплитуды + четкий внешний контур
            ds.DrawLine((float)screenX, topY, (float)screenX, bottomY, fillColor, strokeWidth * 1.5f);
            ds.DrawLine((float)screenX, topY, (float)screenX, bottomY, waveColor, strokeWidth);
        }

        // Вертикальный визир золотого выравнивания (по центру)
        ds.DrawLine(centerX, 0, centerX, height, ColorHelper.FromArgb(220, 255, 215, 0), 2.0f);
    }

    private void DrawTimeRulerGrid(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float width, float height, float centerX)
    {
        double gridStepSeconds = 1.0;
        if (_pixelsPerSecond > 5000) gridStepSeconds = 0.01;      // 10 мс
        else if (_pixelsPerSecond > 1000) gridStepSeconds = 0.05; // 50 мс
        else if (_pixelsPerSecond > 200) gridStepSeconds = 0.2;   // 200 мс
        else if (_pixelsPerSecond > 50) gridStepSeconds = 1.0;    // 1 сек
        else if (_pixelsPerSecond > 10) gridStepSeconds = 5.0;    // 5 сек
        else gridStepSeconds = 30.0;                              // 30 сек

        double leftTime = _viewOffsetSeconds - (centerX / _pixelsPerSecond);
        double rightTime = _viewOffsetSeconds + ((width - centerX) / _pixelsPerSecond);

        double firstTickTime = Math.Floor(leftTime / gridStepSeconds) * gridStepSeconds;

        for (double t = firstTickTime; t <= rightTime; t += gridStepSeconds)
        {
            float x = centerX + (float)((t - _viewOffsetSeconds) * _pixelsPerSecond);
            if (x < 0 || x > width) continue;

            ds.DrawLine(x, 0, x, height, ColorHelper.FromArgb(25, 255, 255, 255), 1.0f);

            TimeSpan ts = TimeSpan.FromSeconds(Math.Abs(t));
            string timeStr = t >= 0 
                ? $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                : $"-{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";

            ds.DrawText(timeStr, x + 4, 4, ColorHelper.FromArgb(120, 200, 210, 225), new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 10 });
        }
    }

    #region Панорамирование и Масштабирование (Pan & Zoom)

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(sender as UIElement);
        int delta = pointerPoint.Properties.MouseWheelDelta;

        if (delta > 0)
        {
            _pixelsPerSecond = Math.Min(50000.0, _pixelsPerSecond * 1.35); // Сверхточный зум до 50 000 px/sec
        }
        else if (delta < 0)
        {
            _pixelsPerSecond = Math.Max(1.0, _pixelsPerSecond / 1.35);
        }

        SourceCanvas.Invalidate();
        TransplantCanvas.Invalidate();
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var element = sender as UIElement;
        if (element == null) return;

        _isDragging = true;
        _lastMousePosition = e.GetCurrentPoint(element).Position;
        element.CapturePointer(e.Pointer);
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        var element = sender as UIElement;
        if (element == null) return;

        var currentPos = e.GetCurrentPoint(element).Position;
        double deltaX = currentPos.X - _lastMousePosition.X;
        _lastMousePosition = currentPos;

        // Проверяем, зажата ли клавиша Shift на клавиатуре
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        bool isShiftPressed = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (ReferenceEquals(sender, TransplantCanvas) || isShiftPressed)
        {
            // Плавно прибавляем сдвиг в миллисекундах без потери точности при глубоком приближении
            double deltaSeconds = deltaX / _pixelsPerSecond;
            _userShiftMs += deltaSeconds * 1000.0;
            ShiftNumberBox.Value = Math.Round(_userShiftMs);
            UpdateNetOffsetLabel();
        }
        else
        {
            // Обычное панорамирование общего вида хронологии
            double deltaSeconds = deltaX / _pixelsPerSecond;
            _viewOffsetSeconds -= deltaSeconds;
        }

        SourceCanvas.Invalidate();
        TransplantCanvas.Invalidate();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
    }

    #endregion

    #region Управление сдвигом (Кнопки и NumberBox)

    private void OnShiftClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int deltaMs))
        {
            _userShiftMs += deltaMs;
            ShiftNumberBox.Value = _userShiftMs;
            UpdateNetOffsetLabel();
            TransplantCanvas.Invalidate();
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _userShiftMs = 0;
        ShiftNumberBox.Value = 0;
        UpdateNetOffsetLabel();
        TransplantCanvas.Invalidate();
    }

    private void ShiftNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;

        _userShiftMs = (int)Math.Round(args.NewValue);
        UpdateNetOffsetLabel();
        TransplantCanvas.Invalidate();
    }

    private void UpdateNetOffsetLabel()
    {
        if (NetOffsetTextBlock == null) return;

        double netOffset = FinalCalculatedShiftMs;
        NetOffsetTextBlock.Text = $"{netOffset:+0.00;-0.00;0.00} мс";
    }

    #endregion

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        Close();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        Close();
    }
}
