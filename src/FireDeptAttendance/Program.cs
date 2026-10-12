using Velopack;

namespace FireDeptAttendance;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks and exits early when needed.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
