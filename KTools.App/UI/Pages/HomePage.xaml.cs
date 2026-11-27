// -*- coding: utf-8 -*-
using System;
using System.Linq;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.UI.Controls;
using KTools_App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace KTools_App.UI.Pages;

/// <summary>
/// Класс логики (Code-Behind) для домашней страницы HomePage.
/// Отображает плитки доступных скриптов, обрабатывает навигацию при клике
/// и принимает файлы перетаскиванием прямо на карточку скрипта (ишью #47).
/// </summary>
public sealed partial class HomePage : Page
{
    private const string SourceName = nameof(HomePage);

    /// <summary>
    /// Предоставляет доступ к модели представления домашней страницы.
    /// </summary>
    public HomeViewModel ViewModel { get; }

    /// <summary>
    /// Счётчик событий перетаскивания: отсекает устаревшие асинхронные проверки
    /// совместимости, перекрытые новым кадром перетаскивания или сбросом.
    /// </summary>
    private int _cardDragSeq;

    /// <summary>
    /// Карточка с активной подсветкой дроп-зоны. Подсветка всегда только одна:
    /// при переходе указателя на другую карточку предыдущая гаснет.
    /// </summary>
    private Button? _highlightedCard;

    /// <summary>
    /// Инициализирует новый экземпляр HomePage, разрешая зависимости через DI.
    /// </summary>
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// Обработчик клика по карточке скрипта.
    /// Выполняет переход к экрану выполнения скрипта через службу навигации.
    /// </summary>
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ScriptInfo scriptInfo)
        {
            ViewModel.NavigateToScriptCommand.Execute(scriptInfo);
        }
    }

    /// <summary>
    /// Обработчик перетаскивания файлов над карточкой скрипта.
    /// Принимает только совместимые по расширению файлы доступного скрипта,
    /// подсвечивает карточку пунктирным оверлеем и показывает подпись «Добавить в ...».
    /// </summary>
    private async void Card_DragOver(object sender, DragEventArgs e)
    {
        // Отложенное завершение: состав пакета читается асинхронно,
        // система считывает AcceptedOperation только после Complete().
        var deferral = e.GetDeferral();
        int seq = ++_cardDragSeq;
        try
        {
            if (sender is not Button button || button.DataContext is not ScriptInfo scriptInfo)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            var script = ViewModel.ResolveDropScript(scriptInfo);
            if (script == null || !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                if (seq == _cardDragSeq)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    SetCardDropHighlight(button, false);
                }

                e.Handled = true;
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            if (seq != _cardDragSeq)
            {
                // Устаревшее событие: указатель уже перешёл на другую карточку.
                return;
            }

            var compatible = ViewModel.FilterDropPaths(script, items.OfType<StorageFile>().Select(f => f.Path));
            if (compatible.Count > 0)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = $"Добавить в {script.Name}";
                SetCardDropHighlight(button, true);
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
                SetCardDropHighlight(button, false);
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Write(
                "ui.home.card_drag.failed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Проверка совместимости перетаскиваемых файлов с карточкой не выполнена",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "CARD_DRAG_CHECK_FAILED"));
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Обработчик ухода указателя с карточки: гасит подсветку дроп-зоны.
    /// </summary>
    private void Card_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            SetCardDropHighlight(button, false);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Обработчик сброса файлов на карточку: запоминает совместимые пути для скрипта
    /// и сразу открывает его рабочую панель, которая подхватит файлы в очередь.
    /// </summary>
    private async void Card_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        // Отменяем незавершённые проверки DragOver и гасим любую подсветку.
        ++_cardDragSeq;
        SetCardDropHighlight(null, false);

        try
        {
            if (sender is not Button button || button.DataContext is not ScriptInfo scriptInfo)
            {
                return;
            }

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();

            // Папки игнорируются, как во внутренней дроп-зоне списка файлов:
            // принимаются только файлы.
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            if (paths.Count == 0)
            {
                return;
            }

            if (!ViewModel.TryStageCardDrop(scriptInfo, paths))
            {
                return;
            }

            App.Services.GetRequiredService<ILogService>().Write(
                "ui.home.card_drop.staged",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Файлы сброшены на карточку «{scriptInfo.Name}»: совместимые пути запомнены, открывается рабочая панель",
                source: SourceName,
                properties: LogProps
                    .Create("Count", paths.Count)
                    .With("Page", nameof(HomePage)));
            ViewModel.NavigateToScriptCommand.Execute(scriptInfo);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogService>().Write(
                "ui.home.card_drop.failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось обработать файлы, сброшенные на карточку скрипта",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "CARD_DROP_FAILED"));
        }
    }

    /// <summary>
    /// Включает или гасит пунктирную подсветку дроп-зоны на карточке.
    /// Одновременно подсвечена только одна карточка: включение новой гасит предыдущую.
    /// </summary>
    /// <param name="card">Карточка или null, чтобы только погасить текущую подсветку.</param>
    /// <param name="isHighlighted">True — подсветить, False — погасить.</param>
    private void SetCardDropHighlight(Button? card, bool isHighlighted)
    {
        if (_highlightedCard != null && !ReferenceEquals(_highlightedCard, card))
        {
            if (FindDescendant<DropZoneOverlay>(_highlightedCard) is { } previousOverlay)
            {
                previousOverlay.SetHighlighted(false);
                previousOverlay.Visibility = Visibility.Collapsed;
            }

            _highlightedCard = null;
        }

        if (card == null)
        {
            return;
        }

        if (FindDescendant<DropZoneOverlay>(card) is { } overlay)
        {
            if (isHighlighted)
            {
                overlay.Visibility = Visibility.Visible;
                overlay.SetHighlighted(true);
                _highlightedCard = card;
            }
            else
            {
                overlay.SetHighlighted(false);
                overlay.Visibility = Visibility.Collapsed;
                if (ReferenceEquals(_highlightedCard, card))
                {
                    _highlightedCard = null;
                }
            }
        }
    }

    /// <summary>
    /// Находит первый дочерний элемент заданного типа в визуальном дереве.
    /// Нужен для доступа к оверлею внутри шаблона карточки.
    /// </summary>
    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            var nested = FindDescendant<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
