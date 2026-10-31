// -*- coding: utf-8 -*-
using System;
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

    [TestMethod]
    public void ApplyPowerRename_ZeroFileNum_ThrowsArgumentOutOfRange()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Show - ${num:2}");

        Action act = () => script.ApplyPowerRename("FileA", 0, settings);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithStart_OffsetsNumberingFromStart()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Серия ${num:2:10}");

        string first = script.ApplyPowerRename("FileA", 1, settings);
        string second = script.ApplyPowerRename("FileB", 2, settings);
        string third = script.ApplyPowerRename("FileC", 3, settings);

        first.Should().Be("Серия 10");
        second.Should().Be("Серия 11");
        third.Should().Be("Серия 12");
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithStartWithoutPadding_UsesStartAsIs()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Серия ${num:0:10}");

        string first = script.ApplyPowerRename("FileA", 1, settings);

        first.Should().Be("Серия 10");
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithNonPositiveStart_FallsBackToOne()
    {
        var script = CreateScript();
        var zeroStart = LocalSettings(".*", "Серия ${num:2:0}");

        string result = script.ApplyPowerRename("FileA", 1, zeroStart);

        result.Should().Be("Серия 01");
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithExtraSegments_StaysLiteral()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Серия ${num:2:10:5}");

        string result = script.ApplyPowerRename("FileA", 1, settings);

        result.Should().Be("Серия ${num:2:10:5}");
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithEmptySegments_StaysLiteral()
    {
        var script = CreateScript();
        var emptyStart = LocalSettings(".*", "Серия ${num:2:}");
        var emptyPad = LocalSettings(".*", "Серия ${num::10}");

        script.ApplyPowerRename("FileA", 1, emptyStart).Should().Be("Серия ${num:2:}");
        script.ApplyPowerRename("FileA", 1, emptyPad).Should().Be("Серия ${num::10}");
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithOverflowingStart_FallsBackToOne()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Серия ${num:2:99999999999999999999}");

        string result = script.ApplyPowerRename("FileA", 1, settings);

        result.Should().Be("Серия 01");
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithNegativeStart_FallsBackToOne()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Серия ${num:2:-3}");

        string result = script.ApplyPowerRename("FileA", 1, settings);

        result.Should().Be("Серия 01");
    }

    [TestMethod]
    public void ApplyPowerRename_NumWithNegativePad_IgnoresPadding()
    {
        var script = CreateScript();
        var settings = LocalSettings(".*", "Серия ${num:-2:10}");

        string result = script.ApplyPowerRename("FileA", 1, settings);

        result.Should().Be("Серия 10");
    }
}
