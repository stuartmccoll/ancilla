namespace Ancilla.App.WinUI;

/// <summary>The WinUI entry point; everything else happens in <see cref="MauiProgram"/>.</summary>
public partial class App : MauiWinUIApplication
{
    public App() => InitializeComponent();

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
