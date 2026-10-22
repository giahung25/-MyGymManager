using System.Configuration;
using System.Data;
using System.Windows;

namespace MyGymManager.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        using var db = new MyGymManager.Data.AppDbContext();
        await MyGymManager.Data.DbInitializer.InitializeAsync(db);
    }
}

