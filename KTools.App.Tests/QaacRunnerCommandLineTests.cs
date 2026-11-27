// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using KTools_App.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests;

/// <summary>
/// Проверки наличия открытой командной строки в событиях запуска конвейера QAAC (issue #109).
/// Все комментарии на русском языке.
/// </summary>
[TestClass]
public sealed class QaacRunnerCommandLineTests
{
    [TestMethod]
    public void PipelineLaunchProperties_Always_IncludesCommandLine()
    {
        // Arrange
        MethodInfo? method = typeof(QaacRunner).GetMethod(
            "PipelineLaunchProperties",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("метод формирования свойств запуска обязан существовать");

        string arguments = "-V 127 - -o \"out.m4a\"";
        string binaryPath = @"C:\tools\qaac64.exe";

        // Act
        var result = (Dictionary<string, object?>)method!.Invoke(null, new object[] { arguments, binaryPath })!;

        // Assert
        result.Should().ContainKey("CommandLine", "сбой QAAC невоспроизводим без открытой командной строки в process.started");
        string? commandLine = result["CommandLine"] as string;
        commandLine.Should().NotBeNullOrWhiteSpace();
        commandLine.Should().Contain(arguments);
        commandLine.Should().Contain("qaac64");
        // Формат обязан совпадать с DirectProcessRunner: "<путь>" <аргументы>.
        commandLine.Should().Be($"\"{binaryPath}\" {arguments}");
    }

    [TestMethod]
    public void PipelineLaunchProperties_DifferentBinaries_ProducesDistinctCommandLines()
    {
        // Arrange
        MethodInfo? method = typeof(QaacRunner).GetMethod(
            "PipelineLaunchProperties",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        // Act
        var ffmpeg = (Dictionary<string, object?>)method!.Invoke(null, new object[] { "-v error -i \"in.wav\" -f wav -", @"C:\tools\ffmpeg.exe" })!;
        var qaac = (Dictionary<string, object?>)method!.Invoke(null, new object[] { "-V 127 - -o \"out.m4a\"", @"C:\tools\qaac64.exe" })!;

        // Assert
        ffmpeg.Should().ContainKey("CommandLine");
        qaac.Should().ContainKey("CommandLine");
        ((string?)ffmpeg["CommandLine"]).Should().Contain("ffmpeg");
        ((string?)qaac["CommandLine"]).Should().Contain("qaac64");
        ffmpeg["CommandLine"].Should().NotBe(qaac["CommandLine"]);
    }
}
