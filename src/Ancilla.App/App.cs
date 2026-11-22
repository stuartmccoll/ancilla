namespace Ancilla.App;

public sealed class App(MainPage page) : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(page) { Title = "Ancilla", MinimumWidth = 480, MinimumHeight = 480 };
}
