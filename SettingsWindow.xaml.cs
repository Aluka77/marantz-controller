using System.Windows;
using System.Windows.Input;

namespace MarantzController;

/// <summary>
/// A ritkábban használt vevő-beállítások (hangszóró-kalibráció, surround
/// paraméterek, rendszer). Nem modális, és UGYANAZT a MainViewModel-t kapja
/// DataContext-nek, mint a főablak – így nincs külön adat-átvezetés.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
