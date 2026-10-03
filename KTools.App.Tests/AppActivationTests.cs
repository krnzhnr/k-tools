// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для логики активации и перенаправления аргументов (Single-Instance).
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class AppActivationTests
{
    private string _pendingArgsDir = null!;

    [TestInitialize]
    public void Setup()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _pendingArgsDir = Path.Combine(appData, "KTools", "PendingArgs");
    }

    /// <summary>
    /// Проверяет, что аргументы командной строки записываются в канал отложенных
    /// аргументов как данные (без рефлексии и без попадания в журнал).
    /// </summary>
    [TestMethod]
    public void PendingArgsChannel_ValidArgs_CreatesFileWithArguments()
    {
        // Arrange
        string[] testArgs = ["--script", "metadata_cleanup", "C:\\test.mp4"];

        // Очищаем директорию перед тестом
        if (Directory.Exists(_pendingArgsDir))
        {
            foreach (var file in Directory.GetFiles(_pendingArgsDir, "*.txt"))
            {
                try { File.Delete(file); } catch { }
            }
        }

        // Act
        bool written = PendingArgsChannel.TryWrite(_pendingArgsDir, testArgs, out string? errorCode);

        // Assert
        written.Should().BeTrue("канал отложенных аргументов должен принять данные", errorCode);
        Directory.Exists(_pendingArgsDir).Should().BeTrue();
        var files = Directory.GetFiles(_pendingArgsDir, "*.txt");
        files.Should().NotBeEmpty("Файл с отложенными аргументами должен быть создан");

        string createdFile = files[0];
        try
        {
            string[] readArgs = File.ReadAllLines(createdFile);
            readArgs.Should().Equal(testArgs);
        }
        finally
        {
            // Очистка
            if (File.Exists(createdFile))
            {
                File.Delete(createdFile);
            }
        }
    }
}
