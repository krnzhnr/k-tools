// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using KTools_App.ViewModels;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для страницы настройки компонентов DependencySetupPage.
/// Управляет привязками к DependencySetupViewModel и обрабатывает анимацию карточек при наведении.
/// </summary>
public sealed partial class DependencySetupPage : Page
{
    /// <summary>
    /// Предоставляет доступ к модели представления страницы настройки зависимостей.
    /// </summary>
    public DependencySetupViewModel ViewModel { get; }

    /// <summary>
    /// Словарь активных анимаций прокрутки (бегущих строк) для длинных текстов статусов.
    /// </summary>
    private readonly Dictionary<TextBlock, Storyboard> _activeAnimations = new();

    /// <summary>
    /// Инициализирует новый экземпляр DependencySetupPage, разрешая зависимости через DI.
    /// </summary>
    public DependencySetupPage()
    {
        ViewModel = App.Services.GetRequiredService<DependencySetupViewModel>();
        InitializeComponent();
        
        Unloaded += OnPageUnloaded;
    }

    /// <summary>
    /// Вызывается при переходе на эту страницу.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
    }

    /// <summary>
    /// Обработчик наведения указателя мыши на карточку зависимости.
    /// Запускает анимацию бегущей строки для длинных текстов, которые не влезают по ширине.
    /// </summary>
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 0.85;

            // Ищем все дочерние TextBlock для запуска анимации прокрутки
            var textBlocks = FindVisualChildren<TextBlock>(border);
            foreach (var tb in textBlocks)
            {
                string? tag = tb.Tag?.ToString();
                if (tag == "StatusText" || tag == "SubStatusText")
                {
                    // Проверяем, превышает ли реальная ширина текста доступное пространство в 196px
                    if (tb.ActualWidth > 196)
                    {
                        StartMarqueeAnimation(tb);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Обработчик ухода указателя мыши с карточки зависимости.
    /// Останавливает анимацию бегущей строки и сбрасывает положение текста.
    /// </summary>
    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 1.0;

            // Ищем все дочерние TextBlock для остановки анимации прокрутки
            var textBlocks = FindVisualChildren<TextBlock>(border);
            foreach (var tb in textBlocks)
            {
                StopMarqueeAnimation(tb);
            }
        }
    }

    /// <summary>
    /// Запускает анимацию прокрутки (бегущей строки) для указанного TextBlock.
    /// </summary>
    private void StartMarqueeAnimation(TextBlock textBlock)
    {
        // Если анимация уже запущена для этого элемента, ничего не делаем
        if (_activeAnimations.ContainsKey(textBlock)) return;

        double delta = textBlock.ActualWidth - 196;
        if (delta <= 0) return;

        // Создаем или получаем TranslateTransform для сдвига по X
        if (textBlock.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            textBlock.RenderTransform = transform;
        }

        // Вычисляем длительность на основе разницы ширины (скорость ~30 пикселей в секунду)
        double durationSeconds = Math.Max(2.0, delta / 30.0);

        var animation = new DoubleAnimation
        {
            From = 0,
            To = -delta - 8, // Небольшой запас за край обрезки
            Duration = new Duration(TimeSpan.FromSeconds(durationSeconds)),
            BeginTime = TimeSpan.FromSeconds(0.4), // Пауза перед началом прокрутки
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");

        _activeAnimations[textBlock] = storyboard;
        storyboard.Begin();
    }

    /// <summary>
    /// Останавливает анимацию прокрутки для указанного TextBlock и возвращает его в исходное состояние.
    /// </summary>
    private void StopMarqueeAnimation(TextBlock textBlock)
    {
        if (_activeAnimations.TryGetValue(textBlock, out var storyboard))
        {
            storyboard.Stop();
            _activeAnimations.Remove(textBlock);
        }

        if (textBlock.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
        }
    }

    /// <summary>
    /// Вспомогательный метод рекурсивного поиска всех дочерних визуальных элементов заданного типа.
    /// </summary>
    private static List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var list = new List<T>();
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
            {
                list.Add(t);
            }
            list.AddRange(FindVisualChildren<T>(child));
        }
        return list;
    }

    /// <summary>
    /// Вызывается при выгрузке страницы из визуального дерева.
    /// Освобождает системные подписки во ViewModel.
    /// </summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cleanup();
    }
}
