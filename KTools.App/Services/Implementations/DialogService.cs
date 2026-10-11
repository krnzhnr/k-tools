// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Services.Contracts;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

    /// <inheritdoc />
    public async Task<string?> AskAudioTitleAsync(
        IReadOnlyList<string> audioFileNames,
        string defaultTitle,
        IList<string> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        var queue = App.UiDispatcherQueue;
        if (queue != null && !queue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource<string?>();
            bool enqueued = queue.TryEnqueue(async () =>
            {
                try
                {
                    string? title = await ShowAskAudioTitleInternalAsync(audioFileNames, defaultTitle, presets);
                    tcs.SetResult(title);
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

        return await ShowAskAudioTitleInternalAsync(audioFileNames, defaultTitle, presets);
    }

    private async Task<string?> ShowAskAudioTitleInternalAsync(
        IReadOnlyList<string> audioFileNames,
        string defaultTitle,
        IList<string> presets)
    {
        string filesHint = audioFileNames != null && audioFileNames.Count > 0
            ? string.Join(", ", audioFileNames.Take(3)) + (audioFileNames.Count > 3 ? $" и ещё {audioFileNames.Count - 3}" : string.Empty)
            : string.Empty;
        return await ShowAudioTitleDialogInternalAsync(
            "Заголовок аудиодорожек",
            $"Общий заголовок для добавляемых аудиодорожек ({audioFileNames?.Count ?? 0}): {filesHint}",
            defaultTitle,
            presets,
            "Применить",
            "Отмена");
    }

    /// <inheritdoc />
    public async Task<string?> RenameAudioTitleAsync(
        string oldTitle,
        int affectedCount,
        IList<string> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        var queue = App.UiDispatcherQueue;
        if (queue != null && !queue.HasThreadAccess)
        {
            var tcs = new TaskCompletionSource<string?>();
            bool enqueued = queue.TryEnqueue(async () =>
            {
                try
                {
                    string? title = await ShowRenameAudioTitleInternalAsync(oldTitle, affectedCount, presets);
                    tcs.SetResult(title);
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

        return await ShowRenameAudioTitleInternalAsync(oldTitle, affectedCount, presets);
    }

    private async Task<string?> ShowRenameAudioTitleInternalAsync(
        string oldTitle,
        int affectedCount,
        IList<string> presets)
    {
        return await ShowAudioTitleDialogInternalAsync(
            "Переименование заголовка",
            $"Заголовок «{oldTitle}» встретился у дорожек: {affectedCount}. Новый заголовок запишется во все серии с таким заголовком.",
            oldTitle,
            presets,
            "Переименовать",
            "Отмена");
    }

    private async Task<string?> ShowAudioTitleDialogInternalAsync(
        string dialogTitle,
        string description,
        string defaultTitle,
        IList<string> presets,
        string primaryText,
        string closeText)
    {
        await DialogSemaphore.WaitAsync();
        try
        {
            var xamlRoot = XamlRoot;
            if (xamlRoot == null)
            {
                return null;
            }

            var stackPanel = new StackPanel
            {
                Spacing = 8,
                MinWidth = 320
            };

            stackPanel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap
            });

            var titleBox = new TextBox
            {
                Text = defaultTitle ?? string.Empty,
                PlaceholderText = "Например: Русская озвучка",
                Margin = new Thickness(0, 4, 0, 0)
            };
            AutomationProperties.SetName(titleBox, "Заголовок аудиодорожек");
            AutomationProperties.SetAutomationId(titleBox, "AudioTitleInput");
            stackPanel.Children.Add(titleBox);

            stackPanel.Children.Add(new TextBlock
            {
                Text = "Заготовки (нажмите для применения, крестик — удалить):",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            });

            var tokenView = new UI.Controls.TokenView
            {
                ItemsSource = presets,
                SelectionMode = ListViewSelectionMode.Single,
                IsWrapped = true,
                CanRemoveTokens = true,
                MaxHeight = 160
            };
            AutomationProperties.SetName(tokenView, "Заготовленные заголовки");
            AutomationProperties.SetAutomationId(tokenView, "AudioTitlePresets");
            tokenView.SelectionChanged += (s, e) =>
            {
                if (tokenView.SelectedItem is string preset)
                {
                    titleBox.Text = preset;
                }
            };
            string initialTitle = defaultTitle ?? string.Empty;
            if (initialTitle.Length > 0 && presets.Contains(initialTitle))
            {
                tokenView.SelectedItem = initialTitle;
            }

            stackPanel.Children.Add(tokenView);

            var addRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            var presetBox = new TextBox
            {
                PlaceholderText = "Новая заготовка",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 220
            };
            AutomationProperties.SetName(presetBox, "Новая заготовка заголовка");
            var addButton = new Button
            {
                Content = "Добавить",
                MinWidth = 96
            };
            AutomationProperties.SetName(addButton, "Добавить заготовку");
            addButton.Click += (s, e) =>
            {
                string candidate = presetBox.Text?.Trim() ?? string.Empty;
                if (candidate.Length > 0 && !presets.Contains(candidate))
                {
                    presets.Add(candidate);
                    presetBox.Text = string.Empty;
                }
            };
            addRow.Children.Add(presetBox);
            addRow.Children.Add(addButton);
            stackPanel.Children.Add(addRow);

            var dialog = new ContentDialog
            {
                Title = dialogTitle,
                Content = stackPanel,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                return titleBox.Text?.Trim() ?? string.Empty;
            }

            return null;
        }
        finally
        {
            DialogSemaphore.Release();
        }
    }
}

