using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using GLook.Services;

namespace GLook
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceKey = "GLook.Desktop";
        private const int SwRestore = 9;
        private const uint MbIconInformation = 0x00000040;
        private const uint MbSetForeground = 0x00010000;

        private AppInstance? registeredAppInstance;

        public Window? MainWindow { get; private set; }

        public WindowsNotificationService? NotificationService { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            UnhandledException += (_, args) =>
            {
                try
                {
                    var diagnosticsPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GLook",
                        "last-startup-exception.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(diagnosticsPath)!);
                    File.WriteAllText(diagnosticsPath, args.Exception.ToString());
                }
                catch
                {
                }
            };
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            var commandLineArguments = Environment.GetCommandLineArgs();
            var runReadOnlySelfTest = commandLineArguments.Any(argument =>
                string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase));
            var runLabelLifecycleTest = commandLineArguments.Any(argument =>
                string.Equals(argument, "--self-test-label-lifecycle", StringComparison.OrdinalIgnoreCase));
            var runMailActionTest = commandLineArguments.Any(argument =>
                string.Equals(argument, "--self-test-mail-actions", StringComparison.OrdinalIgnoreCase));
            var runNotificationTest = commandLineArguments.Any(argument =>
                string.Equals(argument, "--self-test-notification", StringComparison.OrdinalIgnoreCase));
            var runSyncAllTest = commandLineArguments.Any(argument =>
                string.Equals(argument, "--self-test-sync-all", StringComparison.OrdinalIgnoreCase));
            if (runReadOnlySelfTest || runLabelLifecycleTest || runMailActionTest || runNotificationTest || runSyncAllTest)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMinutes(runSyncAllTest ? 30 : 2));
                if (runSyncAllTest)
                {
                    await AppSelfTestRunner.RunSyncAllAsync(timeout.Token);
                }
                else if (runNotificationTest)
                {
                    await AppSelfTestRunner.RunNotificationDeliveryAsync(timeout.Token);
                }
                else if (runMailActionTest)
                {
                    await AppSelfTestRunner.RunMailActionLifecycleAsync(timeout.Token);
                }
                else if (runLabelLifecycleTest)
                {
                    await AppSelfTestRunner.RunLabelLifecycleAsync(timeout.Token);
                }
                else
                {
                    await AppSelfTestRunner.RunAsync(timeout.Token);
                }

                Exit();
                return;
            }

            registeredAppInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!registeredAppInstance.IsCurrent)
            {
                var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
                await registeredAppInstance.RedirectActivationToAsync(activationArguments);
                _ = MessageBoxW(
                    IntPtr.Zero,
                    "GLook is already running. The existing window has been brought to the front.",
                    "GLook",
                    MbIconInformation | MbSetForeground);
                Exit();
                return;
            }

            registeredAppInstance.Activated += OnAppInstanceActivated;

            MainWindow ??= new Window();
            MainWindow.Title = "GLook";
            MainWindow.ExtendsContentIntoTitleBar = false;
            MainWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "GLook.ico");
            if (File.Exists(iconPath))
            {
                MainWindow.AppWindow.SetIcon(iconPath);
            }

            NotificationService ??= new WindowsNotificationService();
            NotificationService.Register();
            MainWindow.Closed += (_, _) =>
            {
                NotificationService?.Dispose();
                NotificationService = null;
            };

            if (MainWindow.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            if (MainWindow.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                MainWindow.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            MainWindow.Activate();
        }

        private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        {
            _ = MainWindow?.DispatcherQueue.TryEnqueue(ActivateMainWindow);
        }

        private void ActivateMainWindow()
        {
            if (MainWindow is null)
            {
                return;
            }

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
            _ = ShowWindowAsync(windowHandle, SwRestore);
            MainWindow.Activate();
            _ = SetForegroundWindow(windowHandle);
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr windowHandle, int commandShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);
    }
}
