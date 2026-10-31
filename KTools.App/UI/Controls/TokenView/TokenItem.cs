// -*- coding: utf-8 -*-
// Портировано из CommunityToolkit Labs-Windows (MIT): components/TokenView.
// Оригинальная лицензия MIT .NET Foundation сохранена ниже.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace KTools_App.UI.Controls;

/// <summary>
/// Контейнер одного токена в <see cref="TokenView"/>: текст, значок и кнопка удаления.
/// Основан на экспериментальном TokenItem из CommunityToolkit Labs.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
[ContentProperty(Name = nameof(Content))]
[TemplatePart(Name = TokenItemRemoveButtonName, Type = typeof(ButtonBase))]
public partial class TokenItem : ListViewItem
{
    internal const string IconLeftState = "IconLeft";
    internal const string IconOnlyState = "IconOnly";
    internal const string ContentOnlyState = "ContentOnly";
    internal const string RemoveButtonVisibleState = "RemoveButtonVisible";
    internal const string RemoveButtonNotVisibleState = "RemoveButtonNotVisible";
    internal const string TokenItemRemoveButtonName = "PART_RemoveButton";

    private ButtonBase? _tokenItemRemoveButton;

    /// <summary>
    /// Возникает при нажатии кнопки удаления токена.
    /// </summary>
    public event EventHandler<TokenItemRemovingEventArgs>? Removing;

    /// <summary>
    /// Создаёт новый экземпляр <see cref="TokenItem"/>.
    /// </summary>
    public TokenItem()
    {
        DefaultStyleKey = typeof(TokenItem);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_tokenItemRemoveButton != null)
        {
            _tokenItemRemoveButton.Click -= TokenItemRemoveButton_Click;
        }

        _tokenItemRemoveButton = GetTemplateChild(TokenItemRemoveButtonName) as ButtonBase;

        if (_tokenItemRemoveButton != null)
        {
            _tokenItemRemoveButton.Click += TokenItemRemoveButton_Click;
        }

        IconChanged();
        ContentChanged();
        IsRemoveableChanged();
    }

    private void TokenItemRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsRemoveable)
        {
            Removing?.Invoke(this, new TokenItemRemovingEventArgs(Content, this));
        }
    }

    /// <inheritdoc />
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        ContentChanged();
    }

    private void ContentChanged()
    {
        if (Content != null)
        {
            VisualStateManager.GoToState(this, IconLeftState, true);
        }
        else
        {
            VisualStateManager.GoToState(this, IconOnlyState, true);
        }
    }

    /// <summary>
    /// Вызывается при смене значка токена.
    /// </summary>
    protected virtual void OnIconPropertyChanged(IconElement? oldValue, IconElement? newValue)
    {
        IconChanged();
    }

    private void IconChanged()
    {
        if (Icon != null)
        {
            VisualStateManager.GoToState(this, IconLeftState, true);
        }
        else
        {
            VisualStateManager.GoToState(this, ContentOnlyState, true);
        }
    }

    /// <summary>
    /// Вызывается при смене признака удаляемости токена.
    /// </summary>
    protected virtual void OnIsRemoveablePropertyChanged(bool oldValue, bool newValue)
    {
        IsRemoveableChanged();
    }

    private void IsRemoveableChanged()
    {
        if (IsRemoveable)
        {
            VisualStateManager.GoToState(this, RemoveButtonVisibleState, true);
        }
        else
        {
            VisualStateManager.GoToState(this, RemoveButtonNotVisibleState, true);
        }
    }
}
