// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using KTools_App.Core;

namespace KTools_App.UI.Pages;

/// <summary>
/// Страница настроек приложения. Управляет общими параметрами экспорта,
/// темой оформления и обслуживанием конфигурации.
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
            OverwriteToggle.IsOn = mgr.GetSetting("General", "OverwriteExisting", false);
            SubfolderTextBox.Text = mgr.GetSetting("General", "DefaultOutputSubfolder", "KTools_Result");
            AutoSubfolderToggle.IsOn = mgr.GetSetting("General", "UseAutoSubfolder", false);

            // Загрузка темы оформления
            string theme = mgr.GetSetting("General", "Theme", "Dark");
            if (theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
            {
                ThemeComboBox.SelectedIndex = 1; // Светлая
            }
            else
            {
                ThemeComboBox.SelectedIndex = 0; // Темная
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void OverwriteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.SetSetting("General", "OverwriteExisting", OverwriteToggle.IsOn);
    }

    private void SubfolderTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        string subfolder = string.IsNullOrEmpty(SubfolderTextBox.Text) ? "KTools_Result" : SubfolderTextBox.Text;
        SettingsManager.Instance.SetSetting("General", "DefaultOutputSubfolder", subfolder);
    }

    private void AutoSubfolderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.SetSetting("General", "UseAutoSubfolder", AutoSubfolderToggle.IsOn);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string themeTag)
        {
            SettingsManager.Instance.SetSetting("General", "Theme", themeTag);
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        // Переинициализация всех настроек значениями по умолчанию
        SettingsManager.Instance.SetSetting("General", "OverwriteExisting", false);
        SettingsManager.Instance.SetSetting("General", "DefaultOutputSubfolder", "KTools_Result");
        SettingsManager.Instance.SetSetting("General", "UseAutoSubfolder", false);
        SettingsManager.Instance.SetSetting("General", "Theme", "Dark");

        // Перезагружаем UI страницы
        LoadCurrentSettings();

        // Показываем подтверждение
        var dialog = new ContentDialog
        {
            Title = "Настройки сброшены",
            Content = "Все общие параметры и настройки темы были успешно сброшены к значениям по умолчанию.",
            CloseButtonText = "ОК",
            XamlRoot = this.XamlRoot
        };
        _ = dialog.ShowAsync();
    }
}
