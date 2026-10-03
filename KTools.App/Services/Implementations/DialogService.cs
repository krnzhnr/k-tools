// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Services.Contracts;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Реализация службы диалоговых окон на основе WinUI 3 ContentDialog.
/// Поддерживает маршалинг вызовов в UI-поток и защиту от одновременного показа диалогов.
/// </summary>
public sealed class DialogService : IDialogService
{
    private static readonly SemaphoreSlim DialogSemaphore = new(1, 1);
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
        var queue = App.UiDispatcherQueue;
        if (queue != null && !queue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource<bool>();
            bool enqueued = queue.TryEnqueue(async () =>
            {
                try
                {
                    await ShowMessageInternalAsync(title, content);
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (enqueued)
            {
                await tcs.Task;
                return;
            }
        }

        await ShowMessageInternalAsync(title, content);
    }

    private async Task ShowMessageInternalAsync(string title, string content)
    {
        await DialogSemaphore.WaitAsync();
        try
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
        finally
        {
            DialogSemaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ShowConfirmationAsync(
        string title,
        string content,
        string confirmText = "ОК",
        string cancelText = "Отмена")
    {
        var queue = App.UiDispatcherQueue;
        if (queue != null && !queue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource<bool>();
            bool enqueued = queue.TryEnqueue(async () =>
            {
                try
                {
                    bool res = await ShowConfirmationInternalAsync(title, content, confirmText, cancelText);
                    tcs.SetResult(res);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (enqueued)
            {
                return await tcs.Task;
            }
        }

        return await ShowConfirmationInternalAsync(title, content, confirmText, cancelText);
    }

    private async Task<bool> ShowConfirmationInternalAsync(
        string title,
        string content,
        string confirmText,
        string cancelText)
    {
        await DialogSemaphore.WaitAsync();
        try
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
        finally
        {
            DialogSemaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MediaTrack?> ChooseSubtitleTrackAsync(
        string videoFileName,
        IReadOnlyList<MediaTrack> tracks)
    {
        if (tracks == null || tracks.Count == 0)
        {
            return null;
        }

        var queue = App.UiDispatcherQueue;
        if (queue != null && !queue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource<MediaTrack?>();
            bool enqueued = queue.TryEnqueue(async () =>
            {
                try
                {
                    var track = await ShowChooseSubtitleTrackInternalAsync(videoFileName, tracks);
                    tcs.SetResult(track);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (enqueued)
            {
                return await tcs.Task;
            }

            return null;
        }

        return await ShowChooseSubtitleTrackInternalAsync(videoFileName, tracks);
    }

    private async Task<MediaTrack?> ShowChooseSubtitleTrackInternalAsync(
        string videoFileName,
        IReadOnlyList<MediaTrack> tracks)
    {
        await DialogSemaphore.WaitAsync();
        try
        {
            var xamlRoot = XamlRoot;
            if (xamlRoot == null)
            {
                return null;
            }

            var comboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 0)
            };

            foreach (var track in tracks)
            {
                string flags = string.Empty;
                if (track.IsDefault && track.IsForced)
                {
                    flags = " [Default, Forced]";
                }
                else if (track.IsDefault)
                {
                    flags = " [Default]";
                }
                else if (track.IsForced)
                {
                    flags = " [Forced]";
                }

                string trackTitle = !string.IsNullOrWhiteSpace(track.Name) ? $" — {track.Name}" : string.Empty;
                string itemText = $"#{track.TrackId}: [{track.Language}]{trackTitle} ({track.Codec}){flags}";

                comboBox.Items.Add(new ComboBoxItem
                {
                    Content = itemText,
                    Tag = track
                });
            }

            comboBox.SelectedIndex = 0;

            var stackPanel = new StackPanel
            {
                Spacing = 8
            };

            stackPanel.Children.Add(new TextBlock
            {
                Text = $"Для видео «{videoFileName}» не найдены надписи по указанным ключевым словам.",
                TextWrapping = TextWrapping.Wrap
            });

            stackPanel.Children.Add(new TextBlock
            {
                Text = "Выберите дорожку субтитров для вшивания в видеоряд (Burn-in) или нажмите «Пропустить хардсаб»:",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            });

            stackPanel.Children.Add(comboBox);

            var dialog = new ContentDialog
            {
                Title = "Выбор дорожки субтитров для вшивания",
                Content = stackPanel,
                PrimaryButtonText = "Вшить выбранную",
                CloseButtonText = "Пропустить хардсаб",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                return selectedItem.Tag as MediaTrack;
            }

            return null;
        }
        finally
        {
            DialogSemaphore.Release();
        }
    }
}

