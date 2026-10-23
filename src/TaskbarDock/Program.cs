using System;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarDock
{
    internal static class Program
    {
        private const string MutexName = "Local\\TaskbarDock.SingleInstance";

        [STAThread]
        private static void Main()
        {
            AppTrace.Log("Starting TaskbarDock " + typeof(Program).Assembly.GetName().Version);
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    AppTrace.Log("A second instance was rejected.");
                    MessageBox.Show(
                        "TaskbarDock ya está ejecutándose en el área de notificaciones.",
                        "TaskbarDock",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    NativeMethods.TryEnablePerMonitorDpi();
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
                    {
                        // Keep running; an unexpected error must never leave the taskbar clipped.
                        AppTrace.Log("Unhandled UI error: " + args.Exception);
                    };
                    AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
                    {
                        AppTrace.Log("Fatal error: " + args.ExceptionObject);
                        FloatingTaskbarEngine.RestoreNativeShape();
                    };
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    AppTrace.Log("Creating application context.");
                    Application.Run(new DockApplicationContext());
                    AppTrace.Log("Application loop ended normally.");
                }
                catch (Exception exception)
                {
                    AppTrace.Log("Fatal startup error: " + exception);
                    FloatingTaskbarEngine.RestoreNativeShape();
                    MessageBox.Show(
                        exception.ToString(),
                        "TaskbarDock no pudo iniciarse",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }
    }
}
