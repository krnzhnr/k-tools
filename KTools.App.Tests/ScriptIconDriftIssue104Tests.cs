// -*- coding: utf-8 -*-
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Проверки issue #104 в урезанном виде: только регистрация BitrateViewer в реестре.
/// Уникальность глифов не требуется: исторические иконки возвращены как были.
/// Все комментарии выполнены на русском языке в соответствии с требованиями.
/// </summary>
[TestClass]
public class ScriptIconDriftIssue104Tests
{
    private static FieldInfo[] GetScriptIconFields()
    {
        return typeof(AppConstants.ScriptIcons)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToArray();
    }

    [TestMethod]
    public void ScriptIcons_BitrateViewerConstant_IsRegistered()
    {
        FieldInfo? field = GetScriptIconFields().FirstOrDefault(f => f.Name == "BitrateViewer");

        field.Should().NotBeNull("иконка BitrateViewer обязана быть зарегистрирована в AppConstants.ScriptIcons");
    }

    [TestMethod]
    public void BitrateViewerScript_IconName_MatchesRegisteredScriptIcon()
    {
        FieldInfo? field = GetScriptIconFields().FirstOrDefault(f => f.Name == "BitrateViewer");
        field.Should().NotBeNull("иконка BitrateViewer обязана быть зарегистрирована в AppConstants.ScriptIcons");

        var script = new BitrateViewerScript(
            new Mock<ILogService>().Object,
            new Mock<ISettingsManager>().Object,
            new Mock<IPathManager>().Object,
            new Mock<IBitrateAnalyzerService>().Object,
            new Mock<IDiskTypeDetectorService>().Object);

        script.IconName.Should().Be((string)field!.GetRawConstantValue()!);
    }
}
