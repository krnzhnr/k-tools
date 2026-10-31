// -*- coding: utf-8 -*-
// Портировано из CommunityToolkit Labs-Windows (MIT): components/TokenView.
// Оригинальная лицензия MIT .NET Foundation сохранена ниже.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml;

namespace KTools_App.UI.Controls;

/// <summary>
/// Свойства зависимости <see cref="TokenView"/>.
/// </summary>
public partial class TokenView
{
    /// <summary>
    /// Резервное свойство зависимости для <see cref="IsWrapped"/>.
    /// </summary>
    public static readonly DependencyProperty IsWrappedProperty = DependencyProperty.Register(
        nameof(IsWrapped),
        typeof(bool),
        typeof(TokenView),
        new PropertyMetadata(defaultValue: false, (d, e) => ((TokenView)d).OnIsWrappedPropertyChanged((bool)e.OldValue, (bool)e.NewValue)));

    /// <summary>
    /// Резервное свойство зависимости для <see cref="CanRemoveTokens"/>.
    /// </summary>
    public static readonly DependencyProperty CanRemoveTokensProperty = DependencyProperty.Register(
      nameof(CanRemoveTokens),
      typeof(bool),
      typeof(TokenView),
      new PropertyMetadata(defaultValue: false, (d, e) => ((TokenView)d).OnCanRemoveTokensPropertyChanged((bool)e.OldValue, (bool)e.NewValue)));

    /// <summary>
    /// Возвращает или задаёт признак возможности удаления токенов.
    /// </summary>
    public bool CanRemoveTokens
    {
        get => (bool)GetValue(CanRemoveTokensProperty);
        set => SetValue(CanRemoveTokensProperty, value);
    }

    /// <summary>
    /// Возвращает или задаёт перенос токенов на новые строки вместо горизонтальной прокрутки.
    /// </summary>
    public bool IsWrapped
    {
        get => (bool)GetValue(IsWrappedProperty);
        set => SetValue(IsWrappedProperty, value);
    }

    /// <summary>
    /// Вызывается при смене <see cref="IsWrapped"/>.
    /// </summary>
    protected virtual void OnIsWrappedPropertyChanged(bool oldValue, bool newValue)
    {
        OnIsWrappedChanged();
    }

    /// <summary>
    /// Вызывается при смене <see cref="CanRemoveTokens"/>.
    /// </summary>
    protected virtual void OnCanRemoveTokensPropertyChanged(bool oldValue, bool newValue)
    {
        OnCanRemoveTokensChanged();
    }

    private void OnCanRemoveTokensChanged()
    {
    }
}
