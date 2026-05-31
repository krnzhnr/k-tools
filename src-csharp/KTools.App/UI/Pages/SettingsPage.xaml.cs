// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using KTools_App.Core;

namespace KTools_App.UI.Pages;

/// <summary>
/// Страница настроек приложения. Управляет общими параметрами экспорта,
/// логированием, автоматическим поиском обновлений и визуальной темой.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _isInitializing;

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Загружает текущую конфигурацию из SettingsManager при открытии страницы.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        _isInitializing = true;

        try
        {
            var mgr = SettingsManager.Instance;

            // Загрузка общих настроек
            OverwriteToggle.IsOn = mgr.OverwriteExisting;
            ClearListToggle.IsOn = mgr.ClearListOnAdd;

            // Настройка лимитов слайдера параллелизма
            ParallelSlider.Maximum = Environment.ProcessorCount;
            ParallelSlider.Value = mgr.MaxParallelTasks;
            ParallelValueText.Text = mgr.MaxParallelTasks.ToString();

            SubfolderTextBox.Text = mgr.DefaultOutputSubfolder;
            AutoSubfolderToggle.IsOn = mgr.UseAutoSubfolder;

            // Загрузка темы оформления
            string theme = mgr.Theme;
            if (theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
            {
                ThemeComboBox.SelectedIndex = 1; // Светлая
            }
            else
            {
                ThemeComboBox.SelectedIndex = 0; // Темная
            }

            // Загрузка настроек логирования
            ShowLogsTabToggle.IsOn = mgr.ShowLogsTab;
            LogDirTextBox.Text = string.IsNullOrEmpty(mgr.LogDir)
                ? "Используется папка по умолчанию"
                : mgr.LogDir;

            // Загрузка настроек обновлений
            AutoCheckUpdatesToggle.IsOn = mgr.AutoCheckUpdates;
            IncludePreReleasesToggle.IsOn = mgr.IncludePreReleases;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void OverwriteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.OverwriteExisting = OverwriteToggle.IsOn;
    }

    private void ClearListToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.ClearListOnAdd = ClearListToggle.IsOn;
    }

    private void ParallelSlider_ValueChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;
        int val = (int)ParallelSlider.Value;
        ParallelValueText.Text = val.ToString();
        SettingsManager.Instance.MaxParallelTasks = val;
    }

    private void SubfolderTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        string subfolder = string.IsNullOrEmpty(SubfolderTextBox.Text)
            ? "KTools_Result"
            : SubfolderTextBox.Text;
        SettingsManager.Instance.DefaultOutputSubfolder = subfolder;
    }

    private void AutoSubfolderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.UseAutoSubfolder = AutoSubfolderToggle.IsOn;
    }

    private void ThemeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (ThemeComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string themeTag)
        {
            SettingsManager.Instance.Theme = themeTag;
        }
    }

    private void ShowLogsTabToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.ShowLogsTab = ShowLogsTabToggle.IsOn;
        // Синхронизируем видимость вкладки логов на боковой панели навигации
        MainPage.Current?.UpdateLogsTabVisibility();
    }

    private async void BrowseLogDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        try
        {
            var folderPicker = new FolderPicker();
            // Получаем хэндл главного окна приложения для интеграции COM
            var hwnd = WindowNative.GetWindowHandle(App.CurrentMainWindow);
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                SettingsManager.Instance.LogDir = folder.Path;
                LogDirTextBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Ошибка выбора директории",
                Content = $"Не удалось открыть окно выбора папки: {ex.Message}",
                CloseButtonText = "ОК",
                XamlRoot = this.XamlRoot
            };
            _ = dialog.ShowAsync();
        }
    }

    private void AutoCheckUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.AutoCheckUpdates = AutoCheckUpdatesToggle.IsOn;
    }

    private void IncludePreReleasesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.IncludePreReleases = IncludePreReleasesToggle.IsOn;
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        // Переинициализация всех настроек значениями по умолчанию
        var mgr = SettingsManager.Instance;
        mgr.OverwriteExisting = false;
        mgr.ClearListOnAdd = false;
        mgr.MaxParallelTasks = Math.Max(1, Environment.ProcessorCount / 2);
        mgr.DefaultOutputSubfolder = "KTools_Result";
        mgr.UseAutoSubfolder = false;
        mgr.Theme = "Dark";
        mgr.ShowLogsTab = false;
        mgr.LogDir = string.Empty;
        mgr.AutoCheckUpdates = true;
        mgr.IncludePreReleases = false;

        // Синхронизируем видимость панели логов в боковом меню
        MainPage.Current?.UpdateLogsTabVisibility();

        // Перезагружаем UI страницы
        LoadCurrentSettings();

        // Показываем подтверждение
        var dialog = new ContentDialog
        {
            Title = "Настройки сброшены",
            Content = "Все общие параметры, настройки логирования и темы оформления успешно сброшены к значениям по умолчанию.",
            CloseButtonText = "ОК",
            XamlRoot = this.XamlRoot
        };
        _ = dialog.ShowAsync();
    }
}
