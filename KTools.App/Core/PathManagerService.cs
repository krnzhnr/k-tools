// -*- coding: utf-8 -*-
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Нестатическая реализация интерфейса <see cref="IPathManager"/>,
/// делегирующая вызовы статическим методам класса <see cref="PathManager"/>.
/// Обеспечивает возможность внедрения зависимостей (DI) для путей приложения.
/// </summary>
public sealed class PathManagerService : IPathManager
{
    /// <inheritdoc/>
    public string GetBaseDirectory()
    {
        return PathManager.GetBaseDirectory();
    }

    /// <inheritdoc/>
    public string GetBinDirectory()
    {
        return PathManager.GetBinDirectory();
    }

    /// <inheritdoc/>
    public string GetSettingsDirectory()
    {
        return PathManager.GetSettingsDirectory();
    }

    /// <inheritdoc/>
    public string GetBinaryPath(string binaryName)
    {
        return PathManager.GetBinaryPath(binaryName);
    }

    /// <inheritdoc/>
    public string GetShortPath(string path)
    {
        return PathManager.GetShortPath(path);
    }
}
