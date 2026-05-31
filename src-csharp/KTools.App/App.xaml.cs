using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using KTools_App.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace KTools_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Глобальная статическая ссылка на главное окно приложения.
    /// </summary>
    public static Window? CurrentMainWindow { get; private set; }
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Инициализируем логирование при старте приложения
        LogService.Instance.Info("=== Запуск приложения K-Tools C# Edition ===", "App");
        
        string settingsDir = PathManager.GetSettingsDirectory();
        LogService.Instance.Info($"Конфигурация приложения успешно инициализирована. Папка: {settingsDir}", "SettingsManager");

        // При первом запуске автоматически инициализируем все настройки по умолчанию в settings.json (как в оригинале)
        LogService.Instance.DebugLog("Выполняется автоматическая инициализация настроек по умолчанию...", "App");
        _ = ScriptRegistry.Instance.Scripts;

        _window = new MainWindow();
        CurrentMainWindow = _window;
        _window.Activate();
    }
}
