// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using FluentAssertions;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Формулировка опасных операций замены исходного файла (T-07..T-10).
/// Проверка читает production-код четырёх заменяющих скриптов, а не собственные
/// константы теста: собственный литерал всегда «проходит» сам и ничего не защищает.
/// </summary>
[TestClass]
public sealed class SourceReplacementWordingTests
{
    private const string SafeLabel = "Заменить исходный файл готовым результатом после успешной проверки";
    private const string SafeWarningTitle = "Замена исходного файла";
    private const string SafeWarningText =
        "Исходный файл заменяется готовым результатом только после успешной проверки. До подтверждения результата исходный файл сохраняется.";

    /// <summary>Опасные формулировки: обещают подмену/копирование оригинала до проверки.</summary>
    private static readonly string[] DangerousPhrases =
    {
        "Подменить оригинал",
        "скопировать оригинал и заменить",
        "оригинал финальным",
        "подмена оригинала",
        "заменить оригинал без проверки"
    };

    /// <summary>Скрипты, которые заменяют исходный файл готовым результатом.</summary>
    private static readonly string[] ReplacingScripts =
    {
        "MetadataCleanupScript.cs",
        "StreamManagementScript.cs",
        "StreamReplacementScript.cs",
        "VideoEncodingScript.cs"
    };

    /// <summary>Корень проекта KTools.App (три уровня вверх от тестовой bin-папки исходников).</summary>
    private static readonly string AppRoot = FindAppRoot();

    private static string ReadScript(string script) =>
        File.ReadAllText(Path.Combine(AppRoot, "Scripts", script));

    /// <summary>
    /// Опасные формулировки не должны появляться ни в одном production-скрипте:
    /// раньше та же константа проверялась только сама с собой и не читала код.
    /// </summary>
    [TestMethod]
    public void SourceReplacement_ProductionScripts_ContainNoDangerousWording()
    {
        // Arrange
        string[] productionScripts = Directory.GetFiles(Path.Combine(AppRoot, "Scripts"), "*.cs");

        // Act
        var offenders = productionScripts
            .Select(file => new { Name = Path.GetFileName(file), Text = File.ReadAllText(file) })
            .SelectMany(entry => DangerousPhrases
                .Where(phrase => entry.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                .Select(phrase => $"{entry.Name}: «{phrase}»"))
            .ToList();

        // Assert
        productionScripts.Should().HaveCountGreaterThan(
            10,
            "скан должен охватывать реальные скрипты, иначе проверка вакуумна");
        offenders.Should().BeEmpty(
            "формулировки «Подменить оригинал» и «скопировать оригинал и заменить» обещают необратимое действие до проверки результата");
    }

    /// <summary>
    /// Каждый заменяющий скрипт обязан объявлять безопасную формулировку и
    /// предупреждение о сохранении исходника: без них опасная операция неподтверждена.
    /// </summary>
    [TestMethod]
    public void SourceReplacement_ReplacingScripts_DeclareSafeLabelAndWarning()
    {
        // Act / Assert
        ReplacingScripts.Should().HaveCount(4, "список заменяющих скриптов задаётся явно");
        foreach (string script in ReplacingScripts)
        {
            string source = ReadScript(script);
            source.Should().Contain(SafeLabel, "{0} обязан использовать безопасную формулировку", script);
            source.Should().Contain("requiresWarning: true", "{0} обязан показывать предупреждение", script);
            source.Should().Contain(SafeWarningTitle, "{0} обязан использовать понятный заголовок предупреждения", script);
            source.Should().Contain(SafeWarningText, "{0} обязан явно сообщать о сохранении исходника", script);
        }
    }

    /// <summary>
    /// Скрипт, объявляющий безопасную формулировку, обязан реально содержать её
    /// в production-коде, а не только в константе теста: самопроверка ловит
    /// подмену реализации заглушкой с тем же именем.
    /// </summary>
    [TestMethod]
    public void SourceReplacement_ProductionScripts_ActuallyContainSafeWarningText()
    {
        // Act
        int matched = ReplacingScripts
            .Where(script => Regex.IsMatch(
                ReadScript(script),
                "Исходный файл заменяется готовым результатом только после успешной проверки",
                RegexOptions.CultureInvariant))
            .Count();

        // Assert
        matched.Should().Be(
            ReplacingScripts.Length,
            "текст предупреждения о сохранении исходника обязан присутствовать в каждом заменяющем скрипте");
    }

    /// <summary>
    /// Сообщение о неудачной замене не должно утверждать, что подмена выполнена.
    /// </summary>
    [TestMethod]
    public void SourceReplacement_FailureMessages_DoNotPromiseSuccess()
    {
        // Act
        string[] offenders = ReplacingScripts
            .Where(script => ReadScript(script)
                .Contains("Не удалось подменить оригинал", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Assert
        offenders.Should().BeEmpty(
            "сообщение о неудачной замене не должно утверждать, что подмена выполнена");
    }

    /// <summary>
    /// Прочие скрипты не должны вводить опасную формулировку вне списка заменяющих:
    /// это защита от регрессии при добавлении новых операций над исходником.
    /// </summary>
    [TestMethod]
    public void SourceReplacement_DangerousWording_NotIntroducedByOtherScripts()
    {
        // Arrange
        HashSet<string> replacing = new(ReplacingScripts, StringComparer.OrdinalIgnoreCase);

        // Act
        string[] offenders = Directory.GetFiles(Path.Combine(AppRoot, "Scripts"), "*.cs")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !replacing.Contains(name!))
            .Where(name => DangerousPhrases.Any(phrase => File
                .ReadAllText(Path.Combine(AppRoot, "Scripts", name!))
                .Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            .ToArray()!;

        // Assert
        offenders.Should().BeEmpty("опасная формулировка замены оригинала недопустима в любом скрипте");
    }

    private static string FindAppRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "KTools.App", "MainPage.xaml");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir, "KTools.App");
            }

            string? parent = Path.GetDirectoryName(dir);
            if (parent is null)
            {
                break;
            }

            dir = parent;
        }

        throw new InvalidOperationException(
            "Не удалось найти корень проекта KTools.App от " + AppContext.BaseDirectory);
    }
}
