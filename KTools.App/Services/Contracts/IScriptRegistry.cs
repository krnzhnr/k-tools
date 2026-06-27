// -*- coding: utf-8 -*-
using System.Collections.Generic;
using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс реестра доступных в K-Tools скриптов обработки медиафайлов.
/// </summary>
public interface IScriptRegistry
{
    /// <summary>
    /// Возвращает полный список зарегистрированных скриптов.
    /// </summary>
    List<AbstractScript> Scripts { get; }

    /// <summary>
    /// Возвращает экземпляр скрипта по его уникальному имени.
    /// </summary>
    AbstractScript? GetScriptByName(string name);
}
