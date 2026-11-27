// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using FluentAssertions;
using KTools_App.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Issue #131: CommandLine срезает пути — команду не повторить.
/// Отдельный режим без срезки: полные пути сохраняются для воспроизводимости,
/// секреты и имя пользователя при этом маскируются.
/// </summary>
[TestClass]
public sealed class CommandLineReproducibilityTests
{
    private readonly LogRedactor _redactor = new();

    private static string AsString(IReadOnlyDictionary<string, object?> properties, string key) =>
        Convert.ToString(properties[key], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    [TestMethod]
    public void RedactProperties_CommandLineWithFullPaths_PreservesDirectoriesForReproducibility()
    {
        // Arrange — типичная команда кодирования с абсолютными путями.
        string cmd = "\"C:\\tools\\ffmpeg.exe\" -i \"D:\\Videos\\input.mkv\" -c:v libx265 \"D:\\Videos\\output.mkv\"";
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CommandLine"] = cmd
        };

        // Act.
        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        // Assert — команду можно повторить: каталоги не срезаны до имён файлов.
        string redacted = AsString(result, "CommandLine");
        redacted.Should().Contain("D:\\Videos\\input.mkv", "иначе команду из журнала не повторить");
        redacted.Should().Contain("D:\\Videos\\output.mkv");
        redacted.Should().Contain("ffmpeg.exe");
    }

    [TestMethod]
    public void RedactProperties_CommandLineWithSecrets_RedactsSecretsButKeepsPaths()
    {
        // Arrange — секреты в командной строке обязаны маскироваться, пути — нет.
        const string Sentinel = "SENTINEL-PASSWORD-131";
        string cmd = "\"C:\\tools\\ffmpeg.exe\" -i \"D:\\Videos\\input.mkv\" password=" + Sentinel;
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CommandLine"] = cmd
        };

        // Act.
        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        // Assert.
        string redacted = AsString(result, "CommandLine");
        redacted.Should().NotContain(Sentinel, "секрет не должен утекать даже в воспроизводимой строке");
        redacted.Should().Contain(LogRedactor.RedactedMarker);
        redacted.Should().Contain("D:\\Videos\\input.mkv", "путь при этом сохраняется для повтора команды");
    }

    [TestMethod]
    public void RedactProperties_CommandLineWithUserProfile_MasksUserNameButKeepsStructure()
    {
        // Arrange — имя пользователя приватно, остальная структура пути нужна для повтора.
        string cmd = "\"C:\\tools\\ffmpeg.exe\" -i \"C:\\Users\\SENTINEL-USER-42\\Videos\\movie.mkv\"";
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CommandLine"] = cmd
        };

        // Act.
        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        // Assert.
        string redacted = AsString(result, "CommandLine");
        redacted.Should().NotContain("SENTINEL-USER-42", "имя пользователя не должно утекать в журнал");
        redacted.Should().Contain("Videos\\movie.mkv", "подкаталог и имя файла сохраняются для воспроизводимости");
        redacted.Should().Contain("C:\\Users\\", "корень профиля сохраняется как ориентир для подстановки");
    }
}
