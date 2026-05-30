using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace KTools_App;

/// <summary>
/// Главное окно приложения. Настраивает габариты 792x960 и управляет отображением.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Установка иконки приложения
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Установка точного размера окна из оригинального K-Tools (792 x 960 пикселей)
        AppWindow.Resize(new SizeInt32(800, 960));

        // Навигация по умолчанию на главную страницу
        RootFrame.Navigate(typeof(MainPage));
    }
}

