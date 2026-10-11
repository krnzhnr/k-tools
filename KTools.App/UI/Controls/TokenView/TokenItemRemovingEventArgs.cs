// -*- coding: utf-8 -*-
// Портировано из CommunityToolkit Labs-Windows (MIT): components/TokenView.
// Оригинальная лицензия MIT .NET Foundation сохранена ниже.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace KTools_App.UI.Controls;

/// <summary>
/// Аргументы события удаления токена: удаляемый элемент и его контейнер.
/// </summary>
public class TokenItemRemovingEventArgs : EventArgs
{
    /// <summary>
    /// Инициализирует новый экземпляр <see cref="TokenItemRemovingEventArgs"/>.
    /// </summary>
    /// <param name="item">Удаляемый элемент.</param>
    /// <param name="tokenItem">Закрываемый контейнер <see cref="TokenItem"/>.</param>
    public TokenItemRemovingEventArgs(object item, TokenItem tokenItem)
    {
        Item = item;
        TokenItem = tokenItem;
    }

    /// <summary>
    /// Возвращает удаляемый элемент.
    /// </summary>
    public object Item { get; private set; }

    /// <summary>
    /// Возвращает закрываемый контейнер токена.
    /// </summary>
    public TokenItem TokenItem { get; private set; }
}
