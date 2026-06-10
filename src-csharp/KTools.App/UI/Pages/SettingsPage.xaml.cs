// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage.Pickers;
using WinRT.Interop;
using KTools_App.ViewModels;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для страницы настроек SettingsPage.
/// Инициализирует биндинги к SettingsViewModel и обрабатывает выбор папки логов.
/// </summary>
public partial class SettingsPage : Page
{
    /// <summary>
    /// Предоставляет доступ к модели представления страницы настроек.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Инициализирует новый экземпляр SettingsPage, разрешая зависимости через DI.
    /// </summary>
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// Обработчик клика по кнопке выбора директории для логов.
    /// Требует HWND главного окна приложения для открытия системного FolderPicker в WinUI 3.
    /// </summary>
    private async void BrowseLogDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPicker = new FolderPicker();
            
            // Получаем HWND главного окна для интеграции COM
            var hwnd = WindowNative.GetWindowHandle(App.CurrentMainWindow);
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                ViewModel.SetLogDirectory(folder.Path);
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
            await dialog.ShowAsync();
        }
    }
}
