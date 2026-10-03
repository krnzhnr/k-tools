// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Тесты защиты кэша настроек от изменяемых ссылочных значений.
/// Проверяют глубокое копирование при чтении и записи, ограниченный канонический
/// снимок для сравнения значений и отсутствие «рваного» JSON при конкурентной работе.
/// </summary>
[TestClass]
public class SettingsMutableReferenceTests
{
    private const int DebounceSettleMilliseconds = 900;

    private Mock<IPathManager> _pathManagerMock = null!;
    private RecordingLogService _logService = null!;
    private string _tempSettingsDir = null!;
    private string _tempSettingsFile = null!;
    private readonly List<SettingsManager> _managers = new();

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _pathManagerMock = new Mock<IPathManager>();
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Refs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");
        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempSettingsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (SettingsManager manager in _managers)
        {
            try
            {
                manager.Dispose();
            }
            catch (Exception)
            {
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    /// <summary>
    /// Проверяет, что SetSetting сохраняет копию: последующая правка исходного списка
    /// не должна ни менять кэш, ни попадать на диск.
    /// </summary>
    [TestMethod]
    public void SetSetting_ListTemplateItem_StoresDeepCopy()
    {
        // Arrange
        var manager = CreateManager();
        var templates = new List<TemplateItem>
        {
            new() { Pattern = "original", Description = "исходный" }
        };

        // Act
        manager.SetSetting("General", "SearchTemplates", templates);
        templates[0].Pattern = "mutated-after-set";
        templates.Add(new TemplateItem { Pattern = "added-after-set", Description = "добавленный" });

        // Assert
        manager.SearchTemplates.Should().HaveCount(1);
        manager.SearchTemplates[0].Pattern.Should().Be("original");

        manager.SaveSettings();
        var reloaded = CreateManager();
        reloaded.SearchTemplates.Should().HaveCount(1);
        reloaded.SearchTemplates[0].Pattern.Should().Be("original", "кэш владеет собственной копией значения");
    }

    /// <summary>
    /// Проверяет, что GetSetting возвращает глубокую копию: правка выданного списка
    /// или его элементов не должна менять кэш и обходить обнаружение изменений.
    /// </summary>
    [TestMethod]
    public void GetSetting_ListTemplateItem_ReturnsDeepCopy()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting(
            "General",
            "SearchTemplates",
            new List<TemplateItem> { new() { Pattern = "keep", Description = "описание" } });
        manager.SaveSettings();

        // Act
        List<TemplateItem> handed = manager.SearchTemplates;
        handed[0].Pattern = "ui-mutation";
        handed.Clear();

        // Assert
        manager.SearchTemplates.Should().HaveCount(1);
        manager.SearchTemplates[0].Pattern.Should().Be("keep", "мутация выданной копии не влияет на кэш");

        PersistenceResult unchanged = manager.SetSetting(
            "General",
            "SearchTemplates",
            new List<TemplateItem> { new() { Pattern = "keep", Description = "описание" } });
        unchanged.ChangedCount.Should().Be(0, "обход через мутацию копии не должен выглядеть как реальное изменение");
    }

    /// <summary>
    /// Проверяет глубокое копирование списка словарей (списки ключевых слов):
    /// добавление, правка и удаление элемента фиксируются только явной записью.
    /// </summary>
    [TestMethod]
    public void ListOfDictionaries_AddEditDelete_ArePersistedOnlyThroughCommit()
    {
        // Arrange
        var manager = CreateManager();
        List<Dictionary<string, object>> words = new()
        {
            new() { ["word"] = "rus", ["active"] = true },
            new() { ["word"] = "eng", ["active"] = false }
        };
        manager.SetSetting("Script_Keywords", "text_patterns", words);
        manager.SaveSettings();

        // Act — UI мутирует полученную копию
        List<Dictionary<string, object>> ui = manager.GetSetting(
            "Script_Keywords",
            "text_patterns",
            new List<Dictionary<string, object>>());
        ui[0]["word"] = "jpn";
        ui.Add(new Dictionary<string, object> { ["word"] = "deu", ["active"] = true });
        ui.RemoveAt(1);

        // Assert — кэш не изменился
        List<Dictionary<string, object>> cached = manager.GetSetting(
            "Script_Keywords",
            "text_patterns",
            new List<Dictionary<string, object>>());
        cached.Should().HaveCount(2);
        cached[0]["word"].ToString().Should().Be("rus");

        // Act — явная фиксация правок
        PersistenceResult commit = manager.SetSetting("Script_Keywords", "text_patterns", ui);
        commit.ChangedCount.Should().Be(1);
        manager.SaveSettings();

        var reloaded = CreateManager();
        List<Dictionary<string, object>> stored = reloaded.GetSetting(
            "Script_Keywords",
            "text_patterns",
            new List<Dictionary<string, object>>());
        stored.Should().HaveCount(2);
        stored.Select(item => item["word"].ToString()).Should().Equal("jpn", "deu");
    }

    /// <summary>
    /// Проверяет, что одинаковое по содержанию значение не считается изменением,
    /// а отличающееся содержимым — считается.
    /// </summary>
    [TestMethod]
    public void ValuesEqual_ComparesBoundedCanonicalSnapshotNotReference()
    {
        // Arrange
        var manager = CreateManager();
        var first = new List<TemplateItem> { new() { Pattern = "a", Description = "1" } };
        manager.SetSetting("General", "SearchTemplates", first);
        manager.SaveSettings();

        // Act — другой экземпляр с тем же содержимым
        var same = new List<TemplateItem> { new() { Pattern = "a", Description = "1" } };
        PersistenceResult unchanged = manager.SetSetting("General", "SearchTemplates", same);

        var other = new List<TemplateItem> { new() { Pattern = "b", Description = "1" } };
        PersistenceResult changed = manager.SetSetting("General", "SearchTemplates", other);

        // Assert
        unchanged.ChangedCount.Should().Be(0, "равное содержимое при разных ссылках — не изменение");
        changed.ChangedCount.Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что сравнение словарей не зависит от порядка ключей.
    /// </summary>
    [TestMethod]
    public void ValuesEqual_Dictionaries_IgnoresKeyOrder()
    {
        // Arrange
        var first = new Dictionary<string, object> { ["b"] = 2, ["a"] = 1 };
        var second = new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 };

        // Act & Assert
        SettingsValueSnapshot.AreEqual(first, second).Should().BeTrue();
    }

    /// <summary>
    /// Проверяет ограниченность канонического снимка: рекурсивная структура
    /// не приводит к зависанию и не превышает установленный предел длины.
    /// </summary>
    [TestMethod]
    public void Describe_DeeplyNestedAndHugeValues_StaysBounded()
    {
        // Arrange
        var deep = new Dictionary<string, object>();
        Dictionary<string, object> current = deep;
        for (int level = 0; level < 200; level++)
        {
            var child = new Dictionary<string, object>();
            current["next"] = child;
            current = child;
        }

        var wide = new List<string>();
        for (int index = 0; index < 20000; index++)
        {
            wide.Add(new string('x', 8) + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Act
        string deepDescription = SettingsValueSnapshot.Describe(deep);
        string wideDescription = SettingsValueSnapshot.Describe(wide);

        // Assert
        deepDescription.Length.Should().BeLessThanOrEqualTo(SettingsValueSnapshot.MaxCanonicalLength + 64);
        wideDescription.Length.Should().BeLessThanOrEqualTo(SettingsValueSnapshot.MaxCanonicalLength + 64);
        SettingsValueSnapshot.Clone(deep).Should().NotBeNull();
        SettingsValueSnapshot.Clone(wide).Should().NotBeNull();
    }

    /// <summary>
    /// Проверяет, что GetAllSettingsInGroup отдаёт копии и не позволяет менять кэш.
    /// </summary>
    [TestMethod]
    public void GetAllSettingsInGroup_ReturnsDeepCopies()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting(
            "Script_Group",
            "text_patterns",
            new List<Dictionary<string, object>> { new() { ["word"] = "rus", ["active"] = true } });
        manager.SaveSettings();

        // Act
        Dictionary<string, object> values = manager.GetAllSettingsInGroup("Script_Group");
        var handed = (List<Dictionary<string, object>>)values["text_patterns"];
        handed.Clear();

        // Assert
        manager.GetAllSettingsInGroup("Script_Group")["text_patterns"]
            .Should().BeAssignableTo<List<Dictionary<string, object>>>()
            .Which.Should().HaveCount(1);
    }

    /// <summary>
    /// Проверяет, что мутация выданной копии во время конкурентной записи
    /// не приводит к «рваному» JSON: файл всегда остаётся разбираемым.
    /// </summary>
    [TestMethod]
    public void SaveSettings_WhileCallerMutatesHandedCopy_NeverProducesTornJson()
    {
        // Arrange
        var manager = CreateManager();
        manager.SetSetting(
            "Script_Concurrency",
            "text_patterns",
            new List<Dictionary<string, object>> { new() { ["word"] = "rus", ["active"] = true } });
        manager.SaveSettings();

        using var stop = new CancellationTokenSource();
        Task mutator = Task.Run(() =>
        {
            int index = 0;
            while (!stop.IsCancellationRequested)
            {
                List<Dictionary<string, object>> copy = manager.GetSetting(
                    "Script_Concurrency",
                    "text_patterns",
                    new List<Dictionary<string, object>>());
                copy.Add(new Dictionary<string, object>
                {
                    ["word"] = "w" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["active"] = index % 2 == 0
                });
                index++;
            }
        });

        // Act
        Exception? failure = null;
        try
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                PersistenceResult result = manager.SaveSettings();
                if (!result.IsSuccess)
                {
                    failure = new InvalidOperationException("неожиданный отказ записи: " + result.ErrorCode);
                    break;
                }

                string json = File.ReadAllText(_tempSettingsFile);
                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    document.RootElement.GetProperty("Script_Concurrency").GetProperty("text_patterns")
                        .EnumerateArray()
                        .Should().OnlyContain(item => item.GetProperty("word").GetString() != null);
                }
                catch (JsonException ex)
                {
                    failure = ex;
                    break;
                }
            }
        }
        finally
        {
            stop.Cancel();
            mutator.Wait(TimeSpan.FromSeconds(30));
        }

        // Assert
        failure.Should().BeNull("параллельная мутация копии не должна порождать невалидный JSON");
        using JsonDocument final = JsonDocument.Parse(File.ReadAllText(_tempSettingsFile));
        final.RootElement.GetProperty("Script_Concurrency").GetProperty("text_patterns")
            .GetArrayLength()
            .Should().Be(1, "в кэш попадают только явно зафиксированные значения");
    }

    /// <summary>
    /// Проверяет, что параллельная запись настроек не приводит к рваному JSON
    /// и не теряет подтверждённые ключи.
    /// </summary>
    [TestMethod]
    public void SetSetting_ConcurrentWrites_AlwaysLeaveValidJson()
    {
        // Arrange
        var manager = CreateManager();
        const int writers = 8;
        const int keysPerWriter = 20;

        // Act
        Parallel.For(0, writers, writer =>
        {
            for (int index = 0; index < keysPerWriter; index++)
            {
                manager.SetSetting(
                    "Concurrent_Json",
                    "key_" + writer + "_" + index,
                    new List<TemplateItem> { new() { Pattern = "p" + index, Description = "d" } });
            }
        });

        PersistenceResult saveResult = manager.SaveSettings();

        // Assert
        saveResult.IsSuccess.Should().BeTrue();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_tempSettingsFile));
        JsonElement group = document.RootElement.GetProperty("Concurrent_Json");
        for (int writer = 0; writer < writers; writer++)
        {
            for (int index = 0; index < keysPerWriter; index++)
            {
                group.GetProperty("key_" + writer + "_" + index)
                    .GetArrayLength()
                    .Should().Be(1, "параллельные записи не теряются и не смешиваются");
            }
        }
    }

    /// <summary>
    /// Проверяет, что значения, прочитанные из файла как JsonElement,
    /// также возвращаются в виде неизменяемой копии без обхода кэша.
    /// </summary>
    [TestMethod]
    public void GetSetting_AfterReload_ReturnsCopyThatDoesNotAffectCache()
    {
        // Arrange
        File.WriteAllText(
            _tempSettingsFile,
            "{ \"General\": { \"SearchTemplates\": [ { \"Pattern\": \"disk\", \"Description\": \"с диска\" } ] } }");
        var manager = CreateManager();

        // Act
        List<TemplateItem> templates = manager.SearchTemplates;
        templates[0].Description = "мутация";

        // Assert
        manager.SearchTemplates[0].Description.Should().Be("с диска");
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        _managers.Add(manager);
        return manager;
    }
}
