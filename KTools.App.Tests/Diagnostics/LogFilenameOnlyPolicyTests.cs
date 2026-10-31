// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using FluentAssertions;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Политика «только имя файла» (W-03): в журнал попадает <c>Path.GetFileName</c>,
/// полный путь, UNC-путь и query-строки URL не раскрываются.
/// </summary>
[TestClass]
public sealed class LogFilenameOnlyPolicyTests
{
    private const string PathMarker = "SENTINEL-USER-42";
    private const string FileName = "movie.mkv";

    /// <summary>Корень проекта KTools.App (три уровня вверх от тестовой bin-папки исходников).</summary>
    private static readonly string AppRoot = FindAppRoot();

    private static LogService CreateService(TempDirectoryScope scope)
    {
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000
        };
        return new LogService(options);
    }

    private static string ReadAllShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [TestMethod]
    [DataRow(@"C:\Users\SENTINEL-USER-42\Videos\movie.mkv", "movie.mkv")]
    [DataRow(@"\\server\share\SENTINEL-USER-42\movie.mkv", "movie.mkv")]
    [DataRow(@"C:\Users\SENTINEL-USER-42\Videos\movie.mkv?token=abc", "movie.mkv")]
    [DataRow("https://sentinel.example.com/watch?token=abc#frag", "watch")]
    [DataRow("relative/sub/dir/movie.mkv", "movie.mkv")]
    public void FileNameOnly_KeepsOnlyTheLeafName(string input, string expected)
    {
        // Act
        string result = LogProps.FileNameOnly(input);

        // Assert
        result.Should().Be(expected);
        result.Should().NotContain(PathMarker);
        result.Should().NotContain(Path.DirectorySeparatorChar.ToString());
        result.Should().NotContain("?");
    }

    [TestMethod]
    public void FileNameOnly_EmptyInput_ReturnsUnknownIdentifier()
    {
        // Assert
        LogProps.FileNameOnly(null).Should().Be(LogRedactor.UnknownIdentifier);
        LogProps.FileNameOnly("   ").Should().Be(LogRedactor.UnknownIdentifier);
        LogProps.FileNameOnly(@"C:\Users\SENTINEL-USER-42\").Should().Be(LogRedactor.UnknownIdentifier);
    }

    [TestMethod]
    public void FilenameOnly_ApplicationLog_ContainsOnlyFileName()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string userPath = @"C:\Users\" + PathMarker + @"\Videos\" + FileName;

        // Act — путь попадает и в текст сообщения, и в allowlisted-свойства
        service.Info($"Файл для обработки: {userPath}", "FilenameOnly");
        service.Write(
            "filename_only.properties",
            LogLevel.Info,
            LogStatus.Succeeded,
            "Обработка начата",
            source: "FilenameOnly",
            properties: LogProps.Create("InputName", LogProps.FileNameOnly(userPath)));

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        // Assert
        jsonl.Should().NotContain(PathMarker, "журнал не должен содержать каталог пользователя");
        jsonl.Should().Contain(FileName, "имя файла остаётся диагностически полезным");
    }

    [TestMethod]
    public void FilenameOnly_RawPathProperty_IsStillReducedByRedactor()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string userPath = @"C:\Users\" + PathMarker + @"\Videos\" + FileName;

        // Act — даже если вызывающий код передал полный путь, редактор обязан оставить имя файла
        service.Write(
            "filename_only.raw_property",
            LogLevel.Info,
            LogStatus.Succeeded,
            "Обработка начата",
            source: "FilenameOnly",
            properties: LogProps.Create("InputName", userPath));

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        // Assert
        jsonl.Should().NotContain(PathMarker);
        jsonl.Should().Contain(FileName);
    }

    [TestMethod]
    public void FilenameOnly_UiExport_ContainsOnlyFileName()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string userPath = @"C:\Users\" + PathMarker + @"\Videos\" + FileName;

        // Act
        service.Info($"Файл для обработки: {userPath}", "FilenameOnly");
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string export = service.ReadCurrentLog();

        // Assert
        export.Should().NotBeNullOrWhiteSpace();
        export.Should().NotContain(PathMarker);
        export.Should().Contain(FileName);
    }

    /// <summary>
    /// Метки корня диска («C:\») не раскрывают пользовательские каталоги:
    /// это единственное содержимое значения, состоящее из одного сегмента пути.
    /// </summary>
    private static bool IsDriveRootLabel(string value)
    {
        string candidate = value.Trim().TrimEnd(')').TrimEnd(',').Trim();
        return candidate.Equals("root", StringComparison.Ordinal)
            || candidate.Equals("Path.GetPathRoot", StringComparison.Ordinal);
    }

    [TestMethod]
    public void FilenameOnly_ProductionSources_UseLogPropsForPathProperties()
    {
        // Arrange — allowlisted path-свойства обязаны получать только имя файла
        string[] allowlistedPathKeys = { "FileName", "InputName", "OutputName" };
        List<string> violations = new();

        foreach (string file in Directory.GetFiles(AppRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                || file.EndsWith("LogProps.cs", StringComparison.Ordinal)
                || file.EndsWith("LogRedactor.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string content = File.ReadAllText(file);
            foreach (string key in allowlistedPathKeys)
            {
                foreach (Match match in Regex.Matches(
                    content,
                    "\"" + key + "\"\\s*,?\\s*(?<value>[^\\r\\n]+)"))
                {
                    string value = match.Groups["value"].Value;
                    if (value.Contains("LogProps.FileName", StringComparison.Ordinal)
                        || value.Contains("LogProps.RootName", StringComparison.Ordinal)
                        || value.Contains("LogRedactor.", StringComparison.Ordinal)
                        || IsDriveRootLabel(value)
                        || value.Contains("Name = ", StringComparison.Ordinal)
                        || value.Contains("FileName)", StringComparison.Ordinal)
                        || value.Contains("fileName", StringComparison.Ordinal)
                        || value.Contains("originalName", StringComparison.Ordinal)
                        || value.Contains("dep.", StringComparison.Ordinal)
                        || value.Contains("label", StringComparison.Ordinal)
                        || value.Contains("destFileName", StringComparison.Ordinal)
                        || value.Contains("SafeFileName", StringComparison.Ordinal)
                        || value.Contains("Path.GetFileName", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    violations.Add($"{Path.GetFileName(file)}: {key} => {value.Trim()}");
                }
            }
        }

        // Assert
        violations.Should().BeEmpty("allowlisted path-свойства журнала обязаны получать только имя файла (W-03)");
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
