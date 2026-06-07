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
    /// Устанавливает корневой элемент XAML для привязки ContentDialog.
    /// Должен быть установлен после загрузки корневой страницы.
    /// </summary>
    public Microsoft.UI.Xaml.XamlRoot? XamlRoot
    {
        get => _xamlRoot;
        set => _xamlRoot = value;
    }

    /// <inheritdoc />
    public async Task ShowMessageAsync(string title, string content)
    {
        if (_xamlRoot == null)
        {
            throw new InvalidOperationException(
                "XamlRoot не инициализирован. "
                + "Установите свойство XamlRoot перед вызовом ShowMessageAsync.");
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "ОК",
            XamlRoot = _xamlRoot
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
        if (_xamlRoot == null)
        {
            throw new InvalidOperationException(
                "XamlRoot не инициализирован. "
                + "Установите свойство XamlRoot перед вызовом ShowConfirmationAsync.");
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            XamlRoot = _xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
