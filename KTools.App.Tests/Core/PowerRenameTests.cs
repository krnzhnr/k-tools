// -*- coding: utf-8 -*-
using System.Collections.Generic;
using FluentAssertions;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты безопасной замены PowerRename в базовом скрипте.
/// Проверяют отсутствие удвоения при шаблоне ".*" и сохранение вставок "^" и "$".
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class PowerRenameTests
{
    private static StubScript CreateScript()
    {
        return new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
    }

    private static Dictionary<string, object> LocalSettings(string search, string replace)
    {
        return new Dictionary<string, object>
        {
            { "LocalRenameOverride", true },
            { "LocalRenameSearch", search },
            { "LocalRenameReplace", replace },
            { "LocalRenameUseRegex", true },
            { "LocalRenameCaseSensitive", false }
        };
    }

    [TestMethod]
    public void ApplyPowerRename_DotStarPattern_ReplacesOnce()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Show - ${num:2}");

        string result = script.ApplyPowerRename("Nige_Jouzu_no_Wakagimi_S02E12_YS", 1, settings);

        result.Should().Be("Show - 01");
    }

    [TestMethod]
    public void ApplyPowerRename_DotStarWithCounter_ReplacesOncePerFile()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Show - ${num:2}");

        string first = script.ApplyPowerRename("FileA", 1, settings);
        string second = script.ApplyPowerRename("FileB", 2, settings);

        first.Should().Be("Show - 01");
        second.Should().Be("Show - 02");
    }

    [TestMethod]
    public void ApplyPowerRename_CaretPattern_InsertsPrefixOnce()
    {
        var script = CreateScript();
        var settings = LocalSettings("^", "X");

        string result = script.ApplyPowerRename("abc", 1, settings);

        result.Should().Be("Xabc");
    }

    [TestMethod]
    public void ApplyPowerRename_DollarPattern_AppendsSuffixOnce()
    {
        var script = CreateScript();
        var settings = LocalSettings("$", "X");

        string result = script.ApplyPowerRename("abc", 1, settings);

        result.Should().Be("abcX");
    }

    [TestMethod]
    public void ApplyPowerRename_GroupPattern_ReplacesCorrectly()
    {
        var script = CreateScript();
        var settings = LocalSettings(@" - (\d+) TG", @" - [$1]");

        string result = script.ApplyPowerRename("Yomi no Tsugai - 10 TG", 1, settings);

        result.Should().Be("Yomi no Tsugai - [10]");
    }

    [TestMethod]
    public void ApplyPowerRename_StarQuantifier_ReplacesNonEmptyMatchesOnly()
    {
        var script = CreateScript();
        var settings = LocalSettings("a*", "X");

        string result = script.ApplyPowerRename("aba", 1, settings);

        result.Should().Be("XbX");
    }
}
