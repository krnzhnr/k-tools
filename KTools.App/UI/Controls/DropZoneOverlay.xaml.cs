// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;

namespace KTools_App.UI.Controls;

/// <summary>
/// Универсальный пользовательский элемент управления для отображения пунктирной окантовки зон сброса файлов (Drop Zone)
/// с аппаратно-ускоренной GPU DirectComposition анимацией подсвечивания.
/// </summary>
public sealed partial class DropZoneOverlay : UserControl
{
    private bool _isHighlighted;
    private bool _isDashedVisible = true;

    /// <summary>
    /// Кешированная ссылка на Composition Visual элемента DashedBorder для повторного использования без повторных вызовов GetElementVisual.
    /// </summary>
    private Microsoft.UI.Composition.Visual? _cachedVisual;

    /// <summary>
    /// Инициализирует новый экземпляр DropZoneOverlay.
    /// Подписывается на событие Loaded для корректной инициализации Composition Visual
    /// после полного подключения элемента к визуальному дереву.
    /// </summary>
    public DropZoneOverlay()
    {
        InitializeComponent();
        Loaded += DropZoneOverlay_Loaded;
    }

    /// <summary>
    /// Обработчик события Loaded — инициализирует начальную прозрачность Composition Visual.
    /// Вызывается после полного подключения элемента к визуальному дереву и Composition-слою,
    /// что гарантирует корректную работу первой GPU DirectComposition анимации.
    /// </summary>
    private void DropZoneOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        if (DashedBorder == null) return;

        _cachedVisual = ElementCompositionPreview.GetElementVisual(DashedBorder);
        UpdateVisualState(false);
    }

    /// <summary>
    /// Управляемая видимость окантовки (например, показывать пунктир только при пустом состоянии очереди).
    /// </summary>
    public void SetVisible(bool isVisible)
    {
        if (_isDashedVisible == isVisible) return;
        _isDashedVisible = isVisible;
        UpdateVisualState(true);
    }

    /// <summary>
    /// Возвращает кешированный Composition Visual элемента DashedBorder,
    /// создавая его при первом обращении если Loaded ещё не выполнился.
    /// </summary>
    private Microsoft.UI.Composition.Visual? EnsureVisual()
    {
        if (_cachedVisual != null) return _cachedVisual;
        if (DashedBorder == null) return null;

        _cachedVisual = ElementCompositionPreview.GetElementVisual(DashedBorder);
        return _cachedVisual;
    }

    /// <summary>
    /// Устанавливает состояние подсветки зоны сброса с использованием GPU DirectComposition.
    /// </summary>
    /// <param name="isHighlighted">True — включить акцентную подсветку, False — вернуть исходное состояние.</param>
    public void SetHighlighted(bool isHighlighted)
    {
        if (DashedBorder == null) return;
        if (_isHighlighted == isHighlighted) return;

        _isHighlighted = isHighlighted;
        UpdateVisualState(true);
    }

    private void UpdateVisualState(bool animate)
    {
        var visual = EnsureVisual();
        if (visual == null || DashedBorder == null) return;

        // Если окантовка не выделена подсвечиванием и скрыта (например, когда файлы уже загружены), targetOpacity = 0.0f
        float targetOpacity = _isHighlighted ? 1.0f : (_isDashedVisible ? 0.45f : 0.0f);

        if (animate)
        {
            var compositor = visual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1.0f, targetOpacity);
            animation.Duration = TimeSpan.FromMilliseconds(120);
            visual.StartAnimation("Opacity", animation);
        }
        else
        {
            visual.Opacity = targetOpacity;
        }

        if (_isHighlighted)
        {
            if (Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var accentBrush) && accentBrush is Brush b)
            {
                DashedBorder.Stroke = b;
            }
            else
            {
                DashedBorder.Stroke = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);
            }
        }
        else
        {
            if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var secBrush) && secBrush is Brush sb)
            {
                DashedBorder.Stroke = sb;
            }
        }
    }
}

