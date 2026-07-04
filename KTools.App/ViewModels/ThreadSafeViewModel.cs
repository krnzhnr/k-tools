// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KTools_App.ViewModels;

/// <summary>
/// Базовый класс для моделей представления (ViewModel), обеспечивающий потокобезопасную
/// отправку уведомлений об изменении свойств (PropertyChanged) в поток пользовательского интерфейса (UI).
/// Все комментарии выполнены исключительно на русском языке в соответствии с регламентом.
/// </summary>
public abstract class ThreadSafeViewModel : ObservableObject
{
    private readonly DispatcherQueue? _dispatcherQueue;

    /// <summary>
    /// Инициализирует новый экземпляр класса ThreadSafeViewModel и захватывает текущий DispatcherQueue.
    /// </summary>
    protected ThreadSafeViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Вызывает событие PropertyChanged. Если вызов происходит не в потоке UI,
    /// он автоматически маршалируется через DispatcherQueue.
    /// </summary>
    /// <param name="e">Аргументы события PropertyChanged.</param>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => base.OnPropertyChanged(e));
        }
        else
        {
            base.OnPropertyChanged(e);
        }
    }
}
