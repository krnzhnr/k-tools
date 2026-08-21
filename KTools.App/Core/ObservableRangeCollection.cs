// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace KTools_App.Core;

/// <summary>
/// Реализация ObservableCollection с поддержкой пакетных операций добавления и замены элементов,
/// вызывающих однократное уведомление об изменении коллекции (Reset).
/// Предотвращает зависания и сбои виртуализации списков в WinUI 3.
/// </summary>
/// <typeparam name="T">Тип элементов коллекции.</typeparam>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Инициализирует новый пустой экземпляр ObservableRangeCollection.
    /// </summary>
    public ObservableRangeCollection()
        : base()
    {
    }

    /// <summary>
    /// Инициализирует новый экземпляр ObservableRangeCollection с элементами из указанной коллекции.
    /// </summary>
    public ObservableRangeCollection(IEnumerable<T> collection)
        : base(collection)
    {
    }

    /// <summary>
    /// Заменяет все элементы коллекции новым набором элементов с отправкой единственного уведомления Reset.
    /// </summary>
    public void ReplaceRange(IEnumerable<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        CheckReentrancy();

        Items.Clear();
        foreach (var item in collection)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Добавляет диапазон элементов в конец коллекции с отправкой единственного уведомления Reset.
    /// </summary>
    public void AddRange(IEnumerable<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        CheckReentrancy();

        foreach (var item in collection)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
