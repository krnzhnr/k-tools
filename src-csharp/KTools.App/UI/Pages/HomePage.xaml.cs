// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using KTools_App.Core;

namespace KTools_App.UI.Pages;

/// <summary>
/// Страница «Главная» с обзором и быстрым переходом к скриптам медиаобработки.
/// </summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        LoadScripts();
    }

    /// <summary>
    /// Инициализация и группировка списка оригинальных 12 скриптов K-Tools.
    /// </summary>
    private void LoadScripts()
    {
        var scripts = new List<ScriptInfo>
        {
            // Категория: Видео
            new ScriptInfo { Name = "Кодирование видео", Category = "Видео", IconName = "video", Description = "Кодирование видео: изменение формата, вшивание субтитров, фильтрация тегов и настройка звука" },
            new ScriptInfo { Name = "Конвертация контейнера", Category = "Видео", IconName = "forward", Description = "Перемещение видео/аудио потоков в другой контейнер без перекодирования" },
            new ScriptInfo { Name = "Очистка метаданных", Category = "Видео", IconName = "delete", Description = "Удаление метаданных из видеофайлов с сохранением оригинального качества" },

            // Категория: Аудио
            new ScriptInfo { Name = "Кодирование аудио", Category = "Аудио", IconName = "music", Description = "Перекодирование аудио в QAAC, AAC, FLAC, WAV, E-AC3, AC3 и др. с настройкой качества" },
            new ScriptInfo { Name = "Даунмикс в Stereo", Category = "Аудио", IconName = "volume2", Description = "Даунмикс 5.1/7.1 в Stereo 2.0 (DDP/DD) через Dolby Encoding Engine" },
            new ScriptInfo { Name = "Изменение скорости аудио", Category = "Аудио", IconName = "sync", Description = "Изменение скорости/тона аудио (PAL ↔ NTSC) с помощью eac3to." },
            new ScriptInfo { Name = "Разделение каналов", Category = "Аудио", IconName = "map", Description = "Разделение многоканального аудио на моно-WAV файлы с опциональной склейкой в стереопары" },

            // Категория: Контейнеры
            new ScriptInfo { Name = "Сборка MKV", Category = "Контейнеры", IconName = "add", Description = "Сборка контейнера MKV из отдельных потоков видео, аудио и субтитров с сопоставлением по имени" },
            new ScriptInfo { Name = "Управление потоками", Category = "Контейнеры", IconName = "list", Description = "Удаление или сохранение выбранных дорожек (видео, аудио, субтитры) в MKV и MP4 файлах." },
            new ScriptInfo { Name = "Замена потоков", Category = "Контейнеры", IconName = "switch", Description = "Заменяет дорожки в MKV/MP4 на внешние файлы (видео, аудио, субтитры)." },
            new ScriptInfo { Name = "Разборка контейнера", Category = "Контейнеры", IconName = "download", Description = "Массовое извлечение потоков из контейнера с авто-именованием." },

            // Категория: Субтитры
            new ScriptInfo { Name = "ASS/SRT → VTT", Category = "Субтитры", IconName = "font", Description = "Конвертация субтитров ASS/SSA/SRT в WebVTT с фильтрацией по актёрам и очисткой тегов." }
        };

        // Заполнение GridView по соответствующим категориям
        VideoGridView.ItemsSource = scripts.Where(s => s.Category == "Видео").ToList();
        AudioGridView.ItemsSource = scripts.Where(s => s.Category == "Аудио").ToList();
        ContainersGridView.ItemsSource = scripts.Where(s => s.Category == "Контейнеры").ToList();
        SubtitlesGridView.ItemsSource = scripts.Where(s => s.Category == "Субтитры").ToList();
    }

    /// <summary>
    /// Обработчик наведения указателя на карточку скрипта.
    /// Добавляет визуальный эффект при наведении.
    /// </summary>
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 0.8;
        }
    }

    /// <summary>
    /// Обработчик ухода указателя со статьи скрипта.
    /// Убирает визуальный эффект при уходе.
    /// </summary>
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Обработчик клика по карточке скрипта.
    /// Вызывается при нажатии на карточку и осуществляет переход на экран скрипта.
    /// </summary>
    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is ScriptInfo script)
        {
            // Находим родительский MainPage для выполнения синхронизированного перехода
            DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this);
            while (parent != null && parent is not MainPage)
            {
                parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
            }

            if (parent is MainPage mainPage)
            {
                mainPage.NavigateToScript(script.Name);
            }
        }
    }
}
