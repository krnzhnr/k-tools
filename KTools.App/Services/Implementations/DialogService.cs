// -*- coding: utf-8 -*-
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация службы диалоговых окон на основе WinUI 3 ContentDialog.
/// Требует установки XamlRoot перед использованием.
/// </summary>
public sealed class DialogService : IDialogService
{
    private Microsoft.UI.Xaml.XamlRoot? _xamlRoot;

    /// <summary>
    /// Устанавливает или динамически возвращает корневой элемент XAML для привязки ContentDialog.
    /// Если свойство не задано явно, пытается получить его из главного окна приложения.
    /// </summary>
    public Microsoft.UI.Xaml.XamlRoot? XamlRoot
    {
        get => _xamlRoot ?? (App.CurrentMainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.XamlRoot;
        set => _xamlRoot = value;
    }

    /// <inheritdoc />
    public async Task ShowMessageAsync(string title, string content)
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot == null)
        {
            throw new InvalidOperationException(
                "XamlRoot не инициализирован. "
                + "Убедитесь, что главное окно создано или установите свойство XamlRoot перед вызовом ShowMessageAsync.");
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "ОК",
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }

    /// <inheritdoc />
    public async Task<bool> ShowConfirmationAsync(
        string title,
        string content,
        string confirmText = "ОК",
        string cancelText = "Отмена")
    {
        var xamlRoot = XamlRoot;
        if (xamlRoot == null)
        {
            throw new InvalidOperationException(
                "XamlRoot не инициализирован. "
                + "Убедитесь, что главное окно создано или установите свойство XamlRoot перед вызовом ShowConfirmationAsync.");
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
