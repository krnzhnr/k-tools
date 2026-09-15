// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Обертка над уникальной временной директорией для файловых тестов.
/// Создает каталог при конструировании и гарантированно удаляет его при Dispose
/// с несколькими попытками на случай транзитных блокировок файлов.
/// </summary>
public sealed class TempDirectoryScope : IDisposable
{
    private const int MaxDisposeRetries = 3;

    /// <summary>
    /// Инициализирует временную директорию с уникальным именем.
    /// </summary>
    public TempDirectoryScope()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "KToolsTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>
    /// Полный путь к корню временной директории.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Создает файл с указанным относительным путем и содержимым.
    /// Промежуточные подкаталоги создаются автоматически.
    /// </summary>
    /// <param name="relativePath">Относительный путь файла внутри директории.</param>
    /// <param name="content">Текстовое содержимое файла.</param>
    /// <returns>Полный путь к созданному файлу.</returns>
    public string CreateFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(RootPath, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(fullPath, content, Encoding.UTF8);
        return fullPath;
    }

    /// <summary>
    /// Возвращает полный путь к относительному пути внутри временной директории.
    /// </summary>
    /// <param name="relativePath">Относительный путь.</param>
    /// <returns>Полный путь.</returns>
    public string GetFullPath(string relativePath)
    {
        return Path.Combine(RootPath, relativePath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        for (int attempt = 1; attempt <= MaxDisposeRetries; attempt++)
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
                return;
            }
            catch (IOException) when (attempt < MaxDisposeRetries)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxDisposeRetries)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
        }
    }
}
