using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KTools_App.UI.Pages;
using KTools_App.Core;

namespace KTools_App;

/// <summary>
/// Главная страница приложения, содержащая боковое навигационное меню.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// Глобальная статическая ссылка на текущий экземпляр MainPage.
    /// </summary>
    public static MainPage? Current { get; private set; }

    /// <summary>
    /// Словарь для быстрого поиска скрипта по его идентификатору.
    /// </summary>
    private Dictionary<string, ScriptInfo> _scriptsByTag = new();

    public MainPage()
    {
        InitializeComponent();
        Current = this;
        InitializeScripts();
    }

    /// <summary>
    /// Инициализирует словарь скриптов для быстрого доступа по Tag'ам из NavigationView.
    /// </summary>
    private void InitializeScripts()
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

        // Создаём словарь для быстрого доступа
        _scriptsByTag.Add("script:video_encoding", scripts[0]);
        _scriptsByTag.Add("script:container_conversion", scripts[1]);
        _scriptsByTag.Add("script:metadata_cleanup", scripts[2]);

        _scriptsByTag.Add("script:audio_encoding", scripts[3]);
        _scriptsByTag.Add("script:audio_downmix", scripts[4]);
        _scriptsByTag.Add("script:audio_speed", scripts[5]);
        _scriptsByTag.Add("script:audio_channels", scripts[6]);

        _scriptsByTag.Add("script:mkv_assembly", scripts[7]);
        _scriptsByTag.Add("script:stream_management", scripts[8]);
        _scriptsByTag.Add("script:stream_replacement", scripts[9]);
        _scriptsByTag.Add("script:container_demux", scripts[10]);

        _scriptsByTag.Add("script:subtitles_convert", scripts[11]);
    }

    /// <summary>
    /// Динамически управляет видимостью вкладки логов в боковой навигационной панели
    /// на основе пользовательских настроек приложения.
    /// </summary>
    public void UpdateLogsTabVisibility()
    {
        bool show = SettingsManager.Instance.ShowLogsTab;
        NavItemLogs.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Обработчик загрузки NavigationView. Проверяет наличие обязательных зависимостей.
    /// Если обязательные компоненты не обнаружены, принудительно перенаправляет на страницу настройки.
    /// </summary>
    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        LogService.Instance.Info("Загрузка главного навигационного интерфейса MainPage", "MainPage");

        // Синхронизируем видимость вкладки журналов логов
        UpdateLogsTabVisibility();

        // Выполняем верификацию наличия обязательного бинарного окружения на диске
        bool hasRequired = DependencyManager.Instance.AreRequiredDependenciesInstalled();
        LogService.Instance.Info($"Результат проверки обязательных зависимостей: {hasRequired}", "MainPage");
        
        if (!hasRequired)
        {
            LogService.Instance.Warn("Отсутствуют обязательные бинарные компоненты. Перенаправление на страницу установки", "MainPage");
            // Перенаправляем пользователя на экран установки внешних бинарников
            NavView.SelectedItem = NavItemDependencies;
        }
        else
        {
            // Переходим на домашний экран со списком доступных скриптов
            NavView.SelectedItem = NavItemHome;
        }
    }

    /// <summary>
    /// Осуществляет внешний принудительный переход на главную страницу приложения.
    /// Вызывается из дочерних страниц после успешного завершения необходимых процедур (например, установки).
    /// </summary>
    public void NavigateToHomeExternally()
    {
        // Установка свойства SelectedItem автоматически инициирует событие SelectionChanged и роутинг
        NavView.SelectedItem = NavItemHome;
    }

    /// <summary>
    /// Осуществляет внешний переход на страницу настройки бинарных зависимостей.
    /// </summary>
    public void NavigateToDependenciesExternally()
    {
        NavView.SelectedItem = NavItemDependencies;
    }

    /// <summary>
    /// Обработчик изменения выбора элемента навигации.
    /// Перенаправляет содержимое фрейма на соответствующую страницу и
    /// динамически обновляет закрепленный заголовок программы и описание.
    /// </summary>
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            LogService.Instance.Info("Пользователь переключился на страницу настроек приложения", "MainPage");
            // Переход на страницу общих настроек
            HeaderTitle.Text = "Настройки";
            HeaderSubtitle.Text = "Общие параметры и конфигурация приложения";
            HeaderSubtitle.Visibility = Visibility.Visible;
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer is NavigationViewItem selectedItem)
        {
            string tag = selectedItem.Tag?.ToString() ?? string.Empty;

            if (tag == "home")
            {
                LogService.Instance.Info("Пользователь переключился на домашнюю страницу", "MainPage");
                // Выполняем переход на домашнюю страницу с плитками скриптов
                ContentFrame.Navigate(typeof(HomePage));

                // Возвращаем глобальный заголовок K-Tools и описание
                HeaderTitle.Text = "K-Tools";
                HeaderSubtitle.Text = "Ваш персональный набор инструментов для обработки медиа";
                HeaderSubtitle.Visibility = Visibility.Visible;
            }
            else if (tag.StartsWith("script:"))
            {
                // Обработка клика на отдельный скрипт
                if (_scriptsByTag.TryGetValue(tag, out var script))
                {
                    LogService.Instance.Info($"Пользователь переключился на рабочий скрипт: '{script.Name}'", "MainPage");
                    HeaderTitle.Text = script.Name;
                    HeaderSubtitle.Text = script.Description;
                    HeaderSubtitle.Visibility = Visibility.Visible;

                    // Находим реальный объект скрипта в реестре и осуществляем переход
                    var realScript = ScriptRegistry.Instance.GetScriptByName(script.Name);
                    if (realScript != null)
                    {
                        ContentFrame.Navigate(typeof(WorkPanel), realScript);
                    }
                    else
                    {
                        LogService.Instance.Error($"Не удалось найти скрипт с именем '{script.Name}' в реестре", "MainPage");
                    }
                }
            }
            else if (tag == "logs")
            {
                LogService.Instance.Info("Пользователь переключился на страницу просмотра логов", "MainPage");
                // Обработка клика на Логи
                HeaderTitle.Text = "Логи";
                HeaderSubtitle.Text = "Просмотр журналов выполнения и системных сообщений в реальном времени";
                HeaderSubtitle.Visibility = Visibility.Visible;
                ContentFrame.Navigate(typeof(LogPage));
            }
            else if (tag == "dependencies")
            {
                LogService.Instance.Info("Пользователь переключился на страницу настройки компонентов (зависимостей)", "MainPage");
                // Переход на страницу управления внешними бинарными зависимостями
                HeaderTitle.Text = "Компоненты";
                HeaderSubtitle.Text = "Установка, обновление и удаление внешних бинарных утилит (FFmpeg, MKVToolNix, eac3to, DEE)";
                HeaderSubtitle.Visibility = Visibility.Visible;
                ContentFrame.Navigate(typeof(DependencySetupPage));
            }
            // Категории без Tag игнорируются (просто открывают/закрывают меню)
        }
    }

    /// <summary>
    /// Осуществляет переход к скрипту по его названию, синхронизируя боковое меню навигации.
    /// </summary>
    public void NavigateToScript(string scriptName)
    {
        var pair = _scriptsByTag.FirstOrDefault(p => p.Value.Name.Equals(scriptName, StringComparison.OrdinalIgnoreCase));
        if (pair.Key != null)
        {
            var item = FindNavItemByTag(NavView.MenuItems, pair.Key);
            if (item != null)
            {
                NavView.SelectedItem = item;
            }
        }
    }

    private NavigationViewItem? FindNavItemByTag(IList<object> items, string tag)
    {
        foreach (var obj in items)
        {
            if (obj is NavigationViewItem item)
            {
                if (item.Tag?.ToString() == tag) return item;

                var subItem = FindNavItemByTag(item.MenuItems, tag);
                if (subItem != null) return subItem;
            }
        }
        return null;
    }
}


