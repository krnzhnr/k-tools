// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Reflection;
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
    /// Тестирует приватный метод WriteArgsToFile из класса Program с помощью рефлексии.
    /// Проверяет, что аргументы командной строки корректно записываются во временный файл на диске.
    /// </summary>
    [TestMethod]
    public void WriteArgsToFile_ValidArgs_CreatesFileWithArguments()
    {
        // Arrange
        string[] testArgs = ["--script", "metadata_cleanup", "C:\\test.mp4"];
        var method = typeof(Program).GetMethod("WriteArgsToFile", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("Метод WriteArgsToFile должен существовать в классе Program");

        // Очищаем директорию перед тестом
        if (Directory.Exists(_pendingArgsDir))
        {
            foreach (var file in Directory.GetFiles(_pendingArgsDir, "*.txt"))
            {
                try { File.Delete(file); } catch { }
            }
        }

        // Act
        method!.Invoke(null, new object[] { testArgs });

        // Assert
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
