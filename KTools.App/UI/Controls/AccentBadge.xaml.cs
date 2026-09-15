// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KTools_App.UI.Controls;

/// <summary>
/// Универсальный визуальный бейджик в нативном стиле WinUI InfoBadge.
/// Отображает произвольный текст с акцентным фоном и контрастным текстом.
/// Автоматически скрывается при отсутствии текста.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public sealed partial class AccentBadge : UserControl
{
    /// <summary>
    /// Регистрация свойства зависимостей TextProperty для произвольного текстового содержимого бейджика.
    /// </summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(AccentBadge),
            new PropertyMetadata(null, OnTextChanged));

    /// <summary>
    /// Инициализирует новый экземпляр элемента управления AccentBadge.
    /// </summary>
    public AccentBadge()
    {
        InitializeComponent();
        UpdateVisibility();
    }

    /// <summary>
    /// Получает или задает отображаемый текст бейджика.
    /// </summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AccentBadge badge)
        {
            badge.UpdateTextAndVisibility((string?)e.NewValue);
        }
    }

    private void UpdateTextAndVisibility(string? text)
    {
        if (BadgeTextBlock != null)
        {
            BadgeTextBlock.Text = text ?? string.Empty;
        }
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        Visibility = string.IsNullOrWhiteSpace(Text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
