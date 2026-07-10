// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для проверки функциональности разбора аргументов командной строки.
/// Все комментарии и документация выполнены строго на русском языке.
/// </summary>
[TestClass]
public class CommandLineParserTests
{
    private string _tempFilePath = null!;

    [TestInitialize]
    public void Setup()
    {
        // Создаем временный файл для проверки существования файлов парсером
        _tempFilePath = Path.GetTempFileName();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Удаляем временный файл после каждого теста
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }
    }

    [TestMethod]
    public void SplitCommandLine_SimpleArgs_ReturnsCorrectArray()
    {
        // Arrange
        string commandLine = "--script transmuxing C:\\test.mp4";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(3);
        result[0].Should().Be("--script");
        result[1].Should().Be("transmuxing");
        result[2].Should().Be("C:\\test.mp4");
    }

    [TestMethod]
    public void SplitCommandLine_WithQuotes_PreservesSpacesInQuotes()
    {
        // Arrange
        string commandLine = "--script \"metadata cleanup\" \"C:\\My Folder\\test file.mkv\"";

        // Act
        var result = App.SplitCommandLine(commandLine);

        // Assert
        result.Should().HaveCount(3);
        result[0].Should().Be("--script");
        result[1].Should().Be("metadata cleanup");
        result[2].Should().Be("C:\\My Folder\\test file.mkv");
    }

    [TestMethod]
    public void ParseCommandLineArray_WithScriptAndFiles_ExtractsCorrectly()
    {
        // Arrange
        // Используем путь к реальному временному файлу, так как парсер проверяет существование файлов на диске
        string[] args = ["--script", "video_encoding", _tempFilePath];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().Be("video_encoding");
        files.Should().ContainSingle().Which.Should().Be(_tempFilePath);
    }

    [TestMethod]
    public void ParseCommandLineArray_NoFilesExist_ReturnsEmptyFilesList()
    {
        // Arrange
        // Указываем путь к несуществующему файлу
        string[] args = ["--script", "video_encoding", "C:\\nonexistent_file_12345.mp4"];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().Be("video_encoding");
        files.Should().BeEmpty();
    }

    [TestMethod]
    public void ParseCommandLineArray_NoScriptFlag_ReturnsOnlyFiles()
    {
        // Arrange
        string[] args = [_tempFilePath];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().BeNull();
        files.Should().ContainSingle().Which.Should().Be(_tempFilePath);
    }

    /// <summary>
    /// Проверяет разбор пустых аргументов.
    /// </summary>
    [TestMethod]
    public void ParseCommandLineArray_EmptyArgs_ReturnsNullAndEmptyList()
    {
        // Arrange
        string[] args = Array.Empty<string>();

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().BeNull();
        files.Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет обработку неизвестного флага.
    /// </summary>
    [TestMethod]
    public void ParseCommandLineArray_InvalidFlag_SkipsFlagAndReturnsEmptyFiles()
    {
        // Arrange
        string[] args = ["--unknown-flag", "value"];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().BeNull();
        files.Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет поведение, когда флаг --script передан без значения.
    /// </summary>
    [TestMethod]
    public void ParseCommandLineArray_ScriptFlagWithNoValue_ReturnsNull()
    {
        // Arrange
        string[] args = ["--script"];

        // Act
        var (script, files) = App.ParseCommandLineArray(args);

        // Assert
        script.Should().BeNull();
        files.Should().BeEmpty();
    }
}
