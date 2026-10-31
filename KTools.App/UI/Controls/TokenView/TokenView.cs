// -*- coding: utf-8 -*-
// Портировано из CommunityToolkit Labs-Windows (MIT): components/TokenView.
// Оригинальная лицензия MIT .NET Foundation сохранена ниже.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using CommunityToolkit.WinUI;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;

using Windows.System;

namespace KTools_App.UI.Controls;

/// <summary>
/// Список токенов в стиле Windows 11: одиночный или множественный выбор,
/// перенос строк (<see cref="IsWrapped"/>), удаление токенов (<see cref="CanRemoveTokens"/>),
/// привязка коллекций через источник элементов базового списка.
/// Основан на экспериментальном TokenView из CommunityToolkit Labs.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
[TemplatePart(Name = TokenViewScrollViewerName, Type = typeof(ScrollViewer))]
[TemplatePart(Name = TokenViewScrollBackButtonName, Type = typeof(ButtonBase))]
[TemplatePart(Name = TokenViewScrollForwardButtonName, Type = typeof(ButtonBase))]
public partial class TokenView : ListViewBase
{
    private const string TokenViewScrollViewerName = "ScrollViewer";
    private const string TokenViewScrollBackButtonName = "ScrollBackButton";
    private const string TokenViewScrollForwardButtonName = "ScrollForwardButton";

    private int _internalSelectedIndex = -1;
    private MethodInfo? _removeItemsSourceMethod;

    private ScrollViewer? _tokenViewScroller;
    private ButtonBase? _tokenViewScrollBackButton;
    private ButtonBase? _tokenViewScrollForwardButton;

    /// <summary>
    /// Возникает перед удалением токена из коллекции.
    /// </summary>
    public event EventHandler<TokenItemRemovingEventArgs>? TokenItemRemoving;

    /// <summary>
    /// Создаёт новый экземпляр <see cref="TokenView"/>.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Обработчик смены ItemsSource обращается к методу Remove коллекции.")]
    public TokenView()
    {
        DefaultStyleKey = typeof(TokenView);

        // Перехват генерации контейнеров и смены коллекции.
        RegisterPropertyChangedCallback(ItemsSourceProperty, ItemsSource_PropertyChanged);
        RegisterPropertyChangedCallback(SelectedIndexProperty, SelectedIndex_PropertyChanged);
    }

    /// <inheritdoc />
    protected override DependencyObject GetContainerForItemOverride() => new TokenItem();

    /// <inheritdoc />
    protected override bool IsItemItsOwnContainerOverride(object item)
    {
        return item is TokenItem;
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        SelectedIndex = _internalSelectedIndex;
        PreviewKeyDown -= TokenView_PreviewKeyDown;
        SizeChanged += TokenView_SizeChanged;
        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.Loaded -= ScrollViewer_Loaded;
        }

        _tokenViewScroller = GetTemplateChild(TokenViewScrollViewerName) as ScrollViewer;

        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.Loaded += ScrollViewer_Loaded;
        }

        PreviewKeyDown += TokenView_PreviewKeyDown;
        OnIsWrappedChanged();
    }

    /// <inheritdoc />
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);

        if (element is TokenItem tokenItem)
        {
            tokenItem.Loaded += Token_Loaded;
            tokenItem.Removing += Token_Removing;

            if (tokenItem.IsRemoveable != true && tokenItem.ReadLocalValue(TokenItem.IsRemoveableProperty) == DependencyProperty.UnsetValue)
            {
                var isRemovableBinding = new Binding()
                {
                    Source = this,
                    Path = new PropertyPath(nameof(CanRemoveTokens)),
                    Mode = BindingMode.OneWay,
                };
                tokenItem.SetBinding(TokenItem.IsRemoveableProperty, isRemovableBinding);
            }
        }
    }

    /// <inheritdoc />
    protected override void OnItemsChanged(object e)
    {
        base.OnItemsChanged(e);
    }

    /// <summary>
    /// Запоминает метод Remove назначенной коллекции для удаления токенов по крестику.
    /// </summary>
    [RequiresUnreferencedCode("Обращение к методу Remove назначенной коллекции источника элементов.")]
    private void ItemsSource_PropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (ItemsSource != null)
        {
            _removeItemsSourceMethod = ItemsSource.GetType().GetMethod("Remove");
        }
        else
        {
            _removeItemsSourceMethod = null;
        }
    }

    private void SelectedIndex_PropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        // Обход https://github.com/microsoft/microsoft-ui-xaml/issues/8257:
        // ловим корректный SelectedIndex до применения шаблона и сохраняем его.
        if (_internalSelectedIndex == -1 && SelectedIndex > -1)
        {
            _internalSelectedIndex = SelectedIndex;
        }
    }

    private bool RemoveItem()
    {
        if (GetCurrentContainerItem() is TokenItem currentContainerItem && currentContainerItem.IsRemoveable)
        {
            Items.Remove(currentContainerItem);
            return true;
        }

        return false;
    }

    private void UpdateScrollButtonsVisibility()
    {
        if (_tokenViewScrollForwardButton != null && _tokenViewScroller != null)
        {
            if (_tokenViewScroller.ScrollableWidth > 0)
            {
                _tokenViewScrollForwardButton.Visibility = Visibility.Visible;
            }
            else
            {
                _tokenViewScrollForwardButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private TokenItem? GetCurrentContainerItem()
    {
        if (XamlRoot != null)
        {
            return FocusManager.GetFocusedElement(XamlRoot) as TokenItem;
        }

        return FocusManager.GetFocusedElement() as TokenItem;
    }

    private void TokenView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateScrollButtonsVisibility();
    }

    private void ScrollTabBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.ChangeView(_tokenViewScroller.HorizontalOffset - _tokenViewScroller.ViewportWidth, null, null);
        }
    }

    private void ScrollTabForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.ChangeView(_tokenViewScroller.HorizontalOffset + _tokenViewScroller.ViewportWidth, null, null);
        }
    }

    private void ScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.Loaded -= ScrollViewer_Loaded;
        }

        if (_tokenViewScrollBackButton != null)
        {
            _tokenViewScrollBackButton.Click -= ScrollTabBackButton_Click;
        }

        if (_tokenViewScrollForwardButton != null)
        {
            _tokenViewScrollForwardButton.Click -= ScrollTabForwardButton_Click;
        }

        if (_tokenViewScroller != null)
        {
            _tokenViewScroller.ViewChanging += TokenViewScroller_ViewChanging;
            _tokenViewScrollBackButton = _tokenViewScroller.FindDescendant(TokenViewScrollBackButtonName) as ButtonBase;
            _tokenViewScrollForwardButton = _tokenViewScroller.FindDescendant(TokenViewScrollForwardButtonName) as ButtonBase;
        }

        if (_tokenViewScrollBackButton != null)
        {
            _tokenViewScrollBackButton.Click += ScrollTabBackButton_Click;
        }

        if (_tokenViewScrollForwardButton != null)
        {
            _tokenViewScrollForwardButton.Click += ScrollTabForwardButton_Click;
        }

        UpdateScrollButtonsVisibility();
    }

    private void TokenViewScroller_ViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (_tokenViewScrollBackButton != null)
        {
            if (e.FinalView.HorizontalOffset < 1)
            {
                _tokenViewScrollBackButton.Visibility = Visibility.Collapsed;
            }
            else if (e.FinalView.HorizontalOffset > 1)
            {
                _tokenViewScrollBackButton.Visibility = Visibility.Visible;
            }
        }

        if (_tokenViewScrollForwardButton != null && _tokenViewScroller != null)
        {
            if (e.FinalView.HorizontalOffset > _tokenViewScroller.ScrollableWidth - 1)
            {
                _tokenViewScrollForwardButton.Visibility = Visibility.Collapsed;
            }
            else if (e.FinalView.HorizontalOffset < _tokenViewScroller.ScrollableWidth - 1)
            {
                _tokenViewScrollForwardButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void Token_Removing(object? sender, TokenItemRemovingEventArgs e)
    {
        if (ItemFromContainer(e.TokenItem) is object item)
        {
            var args = new TokenItemRemovingEventArgs(item, e.TokenItem);
            TokenItemRemoving?.Invoke(this, args);

            if (ItemsSource != null)
            {
                _removeItemsSourceMethod?.Invoke(ItemsSource, new object[] { item });
            }
            else
            {
                if (_tokenViewScroller != null)
                {
                    _tokenViewScroller.UpdateLayout();
                }

                Items.Remove(item);
            }
        }

        UpdateScrollButtonsVisibility();
    }

    private void Token_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TokenItem token)
        {
            token.Loaded -= Token_Loaded;
        }
    }

    private void OnIsWrappedChanged()
    {
        if (_tokenViewScroller != null)
        {
            if (IsWrapped)
            {
                _tokenViewScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            else
            {
                _tokenViewScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            }
        }
    }

    private void TokenView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                e.Handled = MoveFocus(MoveDirection.Previous);
                break;
            case VirtualKey.Right:
                e.Handled = MoveFocus(MoveDirection.Next);
                break;
            case VirtualKey.Back:
            case VirtualKey.Delete:
                e.Handled = RemoveItem();
                break;
        }
    }

    private enum MoveDirection
    {
        Next,
        Previous
    }

    private bool MoveFocus(MoveDirection direction)
    {
        bool retVal = false;

        if (ItemFromContainer(GetCurrentContainerItem()) is DependencyObject currentItem)
        {
            int previousIndex = Items.IndexOf(currentItem);
            int index = previousIndex;

            if (direction == MoveDirection.Previous)
            {
                if (previousIndex > 0)
                {
                    index -= 1;
                }
                else
                {
                    retVal = true;
                }
            }
            else if (direction == MoveDirection.Next)
            {
                if (previousIndex < Items.Count - 1)
                {
                    index += 1;
                }
            }

            // Фокус двигаем только при реальной смене индекса.
            if (index != previousIndex)
            {
                if (ContainerFromIndex(index) is TokenItem newItem)
                {
                    newItem.Focus(FocusState.Keyboard);
                }

                retVal = true;
            }
        }

        return retVal;
    }
}
