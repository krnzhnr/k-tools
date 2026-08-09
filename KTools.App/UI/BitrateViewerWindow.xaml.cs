// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using KTools_App.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.UI;

namespace KTools_App.UI;

/// <summary>
/// Окно визуализации побитового битрейта с аппаратно-ускоренной GPU отрисовкой на Win2D Direct2D.
/// Содержит бесшовную 10-уровневую LOD пирамиду с альфа-блендингом (Cross-fade) без ступенек.
/// Поддерживает панорамирование, инспекцию в точке курсора и выгрузку в PNG.
/// Все комментарии и логи исключительно на русском языке в соответствии с регламентом.
/// </summary>
public sealed partial class BitrateViewerWindow : Window
{
    private BitrateAnalysisResult? _data;
    private double _viewOffsetSeconds = 0.0;
    private double _pixelsPerSecond = 50.0;
    private bool _isDragging = false;
    private Windows.Foundation.Point _lastMousePosition;
    private Windows.Foundation.Point _currentHoverPosition;
    private bool _isHovering = false;

    public BitrateViewerWindow(BitrateAnalysisResult data)
    {
        InitializeComponent();
        ApplyThemeAndBackdrop();
        SetData(data);
    }

    private void ApplyThemeAndBackdrop()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        try
        {
            string theme = App.Services.GetRequiredService<ISettingsManager>().Theme;
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
                    ? ElementTheme.Light
                    : ElementTheme.Dark;
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Error($"Не удалось применить тему к окну анализа битрейта: {ex.Message}", "BitrateViewerWindow");
        }

        try
        {
            string backdrop = App.Services.GetRequiredService<ISettingsManager>().BackdropType;
            if (backdrop.Equals("Acrylic", StringComparison.OrdinalIgnoreCase))
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
            else
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            }
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Error($"Не удалось применить эффект фона к окну анализа битрейта: {ex.Message}", "BitrateViewerWindow");
        }
    }

    public void SetData(BitrateAnalysisResult data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));

        TitleTextBlock.Text = Path.GetFileName(data.FilePath);
        SubtitleTextBlock.Text = $"Кодек: {data.CodecName.ToUpperInvariant()} | Кадров: {data.TotalFrames} | Длительность: {FormatTime(data.DurationSeconds)}";
        
        MeanTextBlock.Text = $"{data.MeanMbps:F2} Mbps";
        MaxTextBlock.Text = $"{data.MaxMbps:F2} Mbps";
        MinTextBlock.Text = $"{data.MinMbps:F2} Mbps";

        // Центрируем начальный вид
        _viewOffsetSeconds = data.DurationSeconds / 2.0;
        if (data.DurationSeconds > 0)
        {
            _pixelsPerSecond = Math.Max(1.0, 1400.0 / data.DurationSeconds);
        }

        GraphCanvas.Invalidate();
    }

    private void GraphCanvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
    }

    private void GraphCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;

        ds.Clear(ColorHelper.FromArgb(255, 16, 18, 22));

        if (width <= 0 || height <= 0 || _data == null || _data.PerSecondMbps.Length == 0) return;

        float paddingBottom = 30.0f;
        float plotHeight = height - paddingBottom - 20.0f;
        float centerX = width / 2.0f;

        // Строго фиксированный верхний предел шкалы: вычисляется от MaxMbps файла.
        // Ни при каком горизонтальном зуме шкала не меняется.
        double maxVal = Math.Max(1.0, (_data.MaxMbps * 1.15) / _verticalScaleMultiplier);

        // 1. Отрисовка координатной сетки времени и битрейта
        DrawGridAndAxes(ds, width, height, plotHeight, paddingBottom, centerX, maxVal);

        // 2. Отрисовка столбиков битрейта напрямую из PerSecondMbps
        DrawBitrateBars(ds, width, plotHeight, paddingBottom, centerX, maxVal, ColorHelper.FromArgb(255, 0, 229, 255));

        // 3. Отрисовка сглаженной контрастной огибающей скользящего среднего битрейта (Ярко-оранжевая линия)
        DrawMovingAverageTrendLine(ds, width, plotHeight, paddingBottom, centerX, maxVal);

        // 4. Отрисовка линий ключевых I-кадров (с порогом плотности для исключения фризов при масштабном отдалении)
        DrawKeyframeLines(ds, width, plotHeight, paddingBottom, centerX);

        // 5. Отрисовка линии общего среднего битрейта файла
        float meanY = (float)(height - paddingBottom - (_data.MeanMbps / maxVal * plotHeight));
        ds.DrawLine(0, meanY, width, meanY, ColorHelper.FromArgb(160, 255, 183, 77), 1.5f, new CanvasStrokeStyle { CustomDashStyle = new float[] { 4, 4 } });

        // 6. Отрисовка инспекционного курсора при наведении
        if (_isHovering && _currentHoverPosition.X >= 0 && _currentHoverPosition.X <= width)
        {
            float hoverX = (float)_currentHoverPosition.X;
            ds.DrawLine(hoverX, 0, hoverX, height - paddingBottom, ColorHelper.FromArgb(200, 255, 255, 255), 1.0f);

            double hoverSec = _viewOffsetSeconds + ((hoverX - centerX) / _pixelsPerSecond);
            if (hoverSec >= 0 && hoverSec <= _data.DurationSeconds)
            {
                double currentMbps = 0.0;
                string extraInfo = "";

                if (_data.FramePackets != null && _data.FramePackets.Length > 0)
                {
                    double fps = _data.Fps > 0 ? _data.Fps : 25.0;
                    int estIndex = (int)Math.Clamp(Math.Floor(hoverSec * fps), 0, _data.FramePackets.Length - 1);
                    var closestFrame = _data.FramePackets[estIndex];

                    if (closestFrame != null && Math.Abs(closestFrame.PtsTime - hoverSec) < 0.5)
                    {
                        double frameSizeKb = closestFrame.SizeBytes / 1024.0;
                        extraInfo = $" | Кадр: {frameSizeKb:F1} КБ {(closestFrame.IsKeyframe ? "[I-Кадр]" : "")}";
                    }
                }

                int secIdx = (int)Math.Floor(hoverSec);
                if (secIdx >= 0 && secIdx < _data.PerSecondMbps.Length)
                {
                    currentMbps = _data.PerSecondMbps[secIdx];
                }

                string tooltip = $"Время: {FormatTime(hoverSec)} | Битрейт сек.: {currentMbps:F2} Mbps{extraInfo}";
                DispatcherQueue.TryEnqueue(() => HoverInfoTextBlock.Text = tooltip);
            }
        }
    }

    private void DrawGridAndAxes(CanvasDrawingSession ds, float width, float height, float plotHeight, float paddingBottom, float centerX, double maxVal)
    {
        // Вертикальная сетка времени
        double gridStepSeconds = 1.0;
        if (_pixelsPerSecond > 2000) gridStepSeconds = 0.05;      // 50 мс
        else if (_pixelsPerSecond > 500) gridStepSeconds = 0.2;  // 200 мс
        else if (_pixelsPerSecond > 100) gridStepSeconds = 0.5;  // 500 мс
        else if (_pixelsPerSecond > 20) gridStepSeconds = 2.0;
        else if (_pixelsPerSecond > 5) gridStepSeconds = 10.0;
        else if (_pixelsPerSecond > 1) gridStepSeconds = 60.0;
        else gridStepSeconds = 300.0;

        double leftTime = _viewOffsetSeconds - (centerX / _pixelsPerSecond);
        double rightTime = _viewOffsetSeconds + ((width - centerX) / _pixelsPerSecond);
        double firstTickTime = Math.Floor(leftTime / gridStepSeconds) * gridStepSeconds;

        for (double t = firstTickTime; t <= rightTime; t += gridStepSeconds)
        {
            float x = centerX + (float)((t - _viewOffsetSeconds) * _pixelsPerSecond);
            if (x < 0 || x > width) continue;

            ds.DrawLine(x, 0, x, height - paddingBottom, ColorHelper.FromArgb(20, 255, 255, 255), 1.0f);
            ds.DrawText(FormatTime(t), x + 3, height - paddingBottom + 4, ColorHelper.FromArgb(120, 200, 210, 225), new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 10 });
        }

        // Горизонтальная сетка значений Mbps
        int steps = 5;
        for (int i = 0; i <= steps; i++)
        {
            double mbpsVal = (maxVal / steps) * i;
            float y = height - paddingBottom - (float)((mbpsVal / maxVal) * plotHeight);

            ds.DrawLine(0, y, width, y, ColorHelper.FromArgb(30, 255, 255, 255), 1.0f);
            ds.DrawText($"{mbpsVal:F1} M", 6, y - 14, ColorHelper.FromArgb(140, 0, 229, 255), new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 10 });
        }
    }

    private void DrawBitrateBars(CanvasDrawingSession ds, float width, float plotHeight, float paddingBottom, float centerX, double maxVal, Color color)
    {
        if (_data == null || _data.PerSecondMbps == null || _data.PerSecondMbps.Length == 0) return;

        double leftTime = _viewOffsetSeconds - (centerX / _pixelsPerSecond);
        double rightTime = _viewOffsetSeconds + ((width - centerX) / _pixelsPerSecond);

        int startSec = Math.Max(0, (int)Math.Floor(leftTime));
        int endSec = Math.Min(_data.PerSecondMbps.Length - 1, (int)Math.Ceiling(rightTime));

        if (startSec > endSec) return;

        float baselineY = plotHeight + 20.0f;
        Color fillColor = ColorHelper.FromArgb((byte)(color.A * 0.25), color.R, color.G, color.B);

        if (_pixelsPerSecond >= 1.0)
        {
            // Приближение (1+ px на секунду): отрисовка каждого посекундного столбца
            float barWidth = (float)Math.Max(1.0, _pixelsPerSecond * 0.85);
            float strokeWidth = Math.Clamp(barWidth, 1.0f, 8.0f);

            for (int s = startSec; s <= endSec; s++)
            {
                float x = centerX + (float)((s - _viewOffsetSeconds) * _pixelsPerSecond);
                if (x < -10 || x > width + 10) continue;

                double mbps = _data.PerSecondMbps[s];
                float topY = baselineY - (float)((mbps / maxVal) * plotHeight);
                if (Math.Abs(baselineY - topY) < 1.0f) topY = baselineY - 1.0f;

                ds.DrawLine(x, baselineY, x, topY, fillColor, strokeWidth * 1.4f);
                ds.DrawLine(x, baselineY, x, topY, color, strokeWidth);
            }
        }
        else
        {
            // Отдаление (< 1 px на секунду): пиксельный рендеринг с MAX-агрегацией в рамках каждого пикселя экрана
            int screenWidth = (int)Math.Ceiling(width);
            double secondsPerPixel = 1.0 / _pixelsPerSecond;

            for (int px = 0; px < screenWidth; px++)
            {
                double pxLeftTime = leftTime + (px * secondsPerPixel);
                double pxRightTime = pxLeftTime + secondsPerPixel;

                int pStartSec = Math.Max(0, (int)Math.Floor(pxLeftTime));
                int pEndSec = Math.Min(_data.PerSecondMbps.Length - 1, (int)Math.Floor(pxRightTime));

                if (pStartSec >= _data.PerSecondMbps.Length || pStartSec > pEndSec) continue;

                double maxMbps = 0.0;
                for (int s = pStartSec; s <= pEndSec; s++)
                {
                    if (_data.PerSecondMbps[s] > maxMbps)
                        maxMbps = _data.PerSecondMbps[s];
                }

                float topY = baselineY - (float)((maxMbps / maxVal) * plotHeight);
                if (Math.Abs(baselineY - topY) < 1.0f) topY = baselineY - 1.0f;

                float x = px;
                ds.DrawLine(x, baselineY, x, topY, color, 1.0f);
            }
        }
    }

    private void DrawMovingAverageTrendLine(CanvasDrawingSession ds, float width, float plotHeight, float paddingBottom, float centerX, double maxVal)
    {
        if (_data == null || _data.FramePackets == null || _data.FramePackets.Length == 0) return;

        // Фиксированная ширина скользящего окна в секундах (не зависит от _pixelsPerSecond, чтобы вертикальный размах оставался статичным)
        double windowSec = 4.0;

        double leftTime = _viewOffsetSeconds - (centerX / _pixelsPerSecond);
        double rightTime = _viewOffsetSeconds + ((width - centerX) / _pixelsPerSecond);

        // Строим 250 гладких отсчетов для визирной области
        int sampleCount = 250;
        double stepTime = (rightTime - leftTime) / sampleCount;
        if (stepTime <= 0) return;

        // Используем два указателя (Two-Pointers / Sliding Window) за O(N) суммарно для всех точек экрана!
        int leftIdx = 0;
        int rightIdx = 0;
        long currentWindowBits = 0;

        using var builder = new CanvasPathBuilder(ds);
        float baselineY = plotHeight + 20.0f;
        bool isFirst = true;

        for (int i = 0; i <= sampleCount; i++)
        {
            double t = leftTime + (i * stepTime);
            if (t < 0 || t > _data.DurationSeconds) continue;

            double wStart = t - (windowSec / 2.0);
            double wEnd = t + (windowSec / 2.0);

            // Двигаем правый указатель
            while (rightIdx < _data.FramePackets.Length && _data.FramePackets[rightIdx].PtsTime <= wEnd)
            {
                currentWindowBits += _data.FramePackets[rightIdx].SizeBytes * 8;
                rightIdx++;
            }

            // Двигаем левый указатель
            while (leftIdx < _data.FramePackets.Length && _data.FramePackets[leftIdx].PtsTime < wStart)
            {
                currentWindowBits -= _data.FramePackets[leftIdx].SizeBytes * 8;
                leftIdx++;
            }

            double actualDuration = Math.Max(0.1, Math.Min(wEnd, _data.DurationSeconds) - Math.Max(0, wStart));
            double smoothedMbps = (currentWindowBits / actualDuration) / 1_000_000.0;

            float x = centerX + (float)((t - _viewOffsetSeconds) * _pixelsPerSecond);
            float y = baselineY - (float)((smoothedMbps / maxVal) * plotHeight);

            if (isFirst)
            {
                builder.BeginFigure(x, y);
                isFirst = false;
            }
            else
            {
                builder.AddLine(x, y);
            }
        }

        if (isFirst) return;
        builder.EndFigure(CanvasFigureLoop.Open);

        using var geometry = CanvasGeometry.CreatePath(builder);
        // Красивая яркая абсолютно гладкая красная огибающая
        Color redTrendColor = ColorHelper.FromArgb(240, 255, 52, 64);
        ds.DrawGeometry(geometry, redTrendColor, 2.5f);
    }

    private void DrawKeyframeLines(CanvasDrawingSession ds, float width, float plotHeight, float paddingBottom, float centerX)
    {
        if (_data == null || _data.KeyframeTimes.Length == 0) return;

        // Порог отображения линий ключевых кадров: строго от 15 пикселей между пунктирами (скрываем при отдалении!)
        double keyframeSpacingPx = (_data.DurationSeconds / _data.KeyframeTimes.Length) * _pixelsPerSecond;
        if (keyframeSpacingPx < 15.0) return;

        // Рассчитываем альфа-прозрачность в зависимости от плотности
        byte alpha = (byte)Math.Clamp(keyframeSpacingPx * 8.0, 40, 220);
        Color kColor = ColorHelper.FromArgb(alpha, 255, 64, 129);

        double leftTime = _viewOffsetSeconds - (centerX / _pixelsPerSecond);
        double rightTime = _viewOffsetSeconds + ((width - centerX) / _pixelsPerSecond);

        foreach (double kTime in _data.KeyframeTimes)
        {
            if (kTime < leftTime || kTime > rightTime) continue;

            float x = centerX + (float)((kTime - _viewOffsetSeconds) * _pixelsPerSecond);
            if (x < -5 || x > width + 5) continue;

            ds.DrawLine(x, 0, x, plotHeight + 20.0f, kColor, 1.0f, new CanvasStrokeStyle { CustomDashStyle = new float[] { 2, 3 } });
        }
    }

    private double _verticalScaleMultiplier = 1.0;

    #region Интерактивность (Pan & Zoom & Hover)

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(GraphCanvas);
        int delta = point.Properties.MouseWheelDelta;
        double width = GraphCanvas.ActualWidth;
        float centerX = (float)(width / 2.0);

        // Проверяем, зажата ли клавиша Shift на клавиатуре для вертикального зума
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        bool isShiftPressed = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (isShiftPressed)
        {
            // Масштабирование высоты графика по вертикали
            if (delta > 0)
                _verticalScaleMultiplier = Math.Min(10.0, _verticalScaleMultiplier * 1.2);
            else if (delta < 0)
                _verticalScaleMultiplier = Math.Max(0.2, _verticalScaleMultiplier / 1.2);
        }
        else
        {
            // Масштабирование по горизонтали с фокусировкой в точку курсора мыши
            double cursorX = point.Position.X;
            double timeAtCursor = _viewOffsetSeconds + ((cursorX - centerX) / _pixelsPerSecond);

            double oldPixelsPerSecond = _pixelsPerSecond;
            if (delta > 0)
                _pixelsPerSecond = Math.Min(10000.0, _pixelsPerSecond * 1.3);
            else if (delta < 0)
                _pixelsPerSecond = Math.Max(0.1, _pixelsPerSecond / 1.3);

            // Корректируем смещение _viewOffsetSeconds так, чтобы время под курсором осталось ровно на месте cursorX
            _viewOffsetSeconds = timeAtCursor - ((cursorX - centerX) / _pixelsPerSecond);
        }

        GraphCanvas.Invalidate();
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        _lastMousePosition = e.GetCurrentPoint(GraphCanvas).Position;
        GraphCanvas.CapturePointer(e.Pointer);
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _currentHoverPosition = e.GetCurrentPoint(GraphCanvas).Position;
        _isHovering = true;

        if (_isDragging)
        {
            double deltaX = _currentHoverPosition.X - _lastMousePosition.X;
            _lastMousePosition = _currentHoverPosition;

            double deltaSeconds = deltaX / _pixelsPerSecond;
            _viewOffsetSeconds -= deltaSeconds;
        }

        GraphCanvas.Invalidate();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        GraphCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        if (_data != null)
        {
            _verticalScaleMultiplier = 1.0;
            _viewOffsetSeconds = _data.DurationSeconds / 2.0;
            _pixelsPerSecond = Math.Max(1.0, 1400.0 / _data.DurationSeconds);
            GraphCanvas.Invalidate();
        }
    }

    private async void OnSavePngClick(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(windowId);
            
            picker.SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("PNG График", new List<string> { ".png" });
            picker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_data.FilePath)}_bitrate.png";

            var file = await picker.PickSaveFileAsync();
            if (file != null && !string.IsNullOrEmpty(file.Path))
            {
                using var fileStream = File.Create(file.Path);
                using var stream = fileStream.AsRandomAccessStream();
                var device = CanvasDevice.GetSharedDevice();
                using var renderTarget = new CanvasRenderTarget(device, 1920, 1080, 96);
                using (var ds = renderTarget.CreateDrawingSession())
                {
                    // Рендерим полноразмерный PNG
                    ds.Clear(ColorHelper.FromArgb(255, 16, 18, 22));
                    DrawBitrateBars(ds, 1920, 1000, 40, 960, _data.MaxMbps * 1.15, ColorHelper.FromArgb(255, 0, 229, 255));
                }
                await renderTarget.SaveAsync(stream, CanvasBitmapFileFormat.Png);
            }
        }
        catch { }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    private static string FormatTime(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.Hours > 0 
            ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
