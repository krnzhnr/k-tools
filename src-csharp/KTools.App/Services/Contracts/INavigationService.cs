// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Абстракция службы навигации для изоляции ViewModels от конкретных UI-фреймворков.
/// Позволяет ViewModels выполнять переходы между страницами без прямой зависимости от Frame.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Выполняет навигацию на указанный тип страницы с опциональным параметром.
    /// </summary>
    void NavigateTo(Type pageType, object? parameter = null);

    /// <summary>
    /// Выполняет навигацию назад к предыдущей странице в стеке.
    /// </summary>
    void GoBack();

    /// <summary>
    /// Указывает, доступна ли навигация назад.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// Событие, вызываемое после завершения навигации. Передаёт имя типа целевой страницы.
    /// </summary>
    event EventHandler<string>? Navigated;
}
