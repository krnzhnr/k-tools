// -*- coding: utf-8 -*-
// Портировано из CommunityToolkit Labs-Windows (MIT): components/TokenView.
// Оригинальная лицензия MIT .NET Foundation сохранена ниже.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KTools_App.UI.Controls;

/// <summary>
/// Свойства зависимости <see cref="TokenItem"/>.
/// </summary>
public partial class TokenItem
{
    /// <summary>
    /// Идентифицирует свойство зависимости <see cref="IsRemoveable"/>.
    /// </summary>
    /// <returns>Идентификатор свойства зависимости <see cref="IsRemoveable"/>.</returns>
    public static readonly DependencyProperty IsRemoveableProperty =
        DependencyProperty.Register(nameof(IsRemoveable), typeof(bool), typeof(TokenItem), new PropertyMetadata(defaultValue: false, (d, e) => ((TokenItem)d).OnIsRemoveablePropertyChanged((bool)e.OldValue, (bool)e.NewValue)));

    /// <summary>
    /// Резервное свойство зависимости для <see cref="Icon"/>.
    /// </summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(IconElement),
        typeof(TokenItem),
        new PropertyMetadata(defaultValue: null, (d, e) => ((TokenItem)d).OnIconPropertyChanged((IconElement?)e.OldValue, (IconElement?)e.NewValue)));

    /// <summary>
    /// Возвращает или задаёт признак возможности удаления токена кнопкой с крестиком.
    /// </summary>
    public bool IsRemoveable
    {
        get { return (bool)GetValue(IsRemoveableProperty); }
        set { SetValue(IsRemoveableProperty, value); }
    }

    /// <summary>
    /// Возвращает или задаёт значок токена.
    /// </summary>
    public IconElement? Icon
    {
        get => (IconElement?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
