// -*- coding: utf-8 -*-
using KTools_App.Core;
using KTools_App.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KTools_App.Converters;

/// <summary>
/// Выбирает шаблон строки журнала по уровню логирования.
 /// Сами цвета заданы в шаблонах стандартными ThemeResource-кистями
/// (см. LogLevelStyle), поэтому строки адаптируются под тему автоматически.
 /// </summary>
public sealed class LogLevelTemplateSelector : DataTemplateSelector
{
    /// <summary>Шаблон строк уровня Debug.</summary>
    public DataTemplate? DebugTemplate { get; set; }

    /// <summary>Шаблон строк уровня Info и неизвестных уровней.</summary>
    public DataTemplate? InfoTemplate { get; set; }

    /// <summary>Шаблон строк уровня Warning.</summary>
    public DataTemplate? WarningTemplate { get; set; }

    /// <summary>Шаблон строк уровня Error.</summary>
    public DataTemplate? ErrorTemplate { get; set; }

    /// <summary>Шаблон строк уровня Fatal.</summary>
    public DataTemplate? FatalTemplate { get; set; }

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item)
    {
        LogLevel level = item is LogItem logItem ? logItem.Level : LogLevel.Info;
        return level switch
        {
            LogLevel.Debug => DebugTemplate ?? InfoTemplate,
            LogLevel.Warning => WarningTemplate ?? InfoTemplate,
            LogLevel.Error => ErrorTemplate ?? InfoTemplate,
            LogLevel.Fatal => FatalTemplate ?? InfoTemplate,
            _ => InfoTemplate,
        };
    }

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
