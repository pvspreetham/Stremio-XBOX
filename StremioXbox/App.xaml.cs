using System;
using StremioXbox.Core;
using Windows.ApplicationModel.Activation;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace StremioXbox
{
    /// <summary>Application entry point.</summary>
    public sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            FileLog.Start();
            FileLog.Write("info", "App starting");

            // Render at 1:1 so Stremio Web lays out at 1920x1080 rather than the Xbox's default 200% scale.
            FileLog.Write("info", "Layout scaling disabled: " + ApplicationViewScaling.TrySetDisableLayoutScaling(true));

            // No Xbox system cursor: controller navigation and the app's own cursor are handled in MainPage
            // (per-control RequiresPointer on the WebView2 never showed a cursor).
            // System cursor (Right in the debug log, saved, applies at launch): leave the app-wide pointer mode
            // at its default like the initial build, where the Xbox cursor clicked fields for real and the
            // keyboard typed into them.
            var systemCursor = false;
            try
            {
                systemCursor = Windows.Storage.ApplicationData.Current.LocalSettings.Values["systemCursor"] is bool saved && saved;
            }
            catch (Exception)
            {
            }

            if (!systemCursor)
            {
                RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            }

            FileLog.Write("info", "System cursor: " + (systemCursor ? "ON (initial-build pointer mode)" : "off"));

            UnhandledException += (sender, e) =>
            {
                FileLog.Write("error", "Unhandled exception: " + e.Exception);
                e.Handled = true;
            };
            Suspending += (sender, e) => FileLog.Write("info", "Lifecycle: suspending");
            Resuming += (sender, e) => FileLog.Write("info", "Lifecycle: resuming");
            EnteredBackground += (sender, e) => FileLog.Write("info", "Lifecycle: entered background");
            LeavingBackground += (sender, e) => FileLog.Write("info", "Lifecycle: leaving background");
            MemoryManager.AppMemoryUsageLimitChanging += (sender, e) =>
                FileLog.Write("warn", string.Format("Memory limit changing {0} -> {1} MB (usage {2} MB)", e.OldLimit >> 20, e.NewLimit >> 20, MemoryManager.AppMemoryUsage >> 20));
            MemoryManager.AppMemoryUsageIncreased += (sender, e) =>
                FileLog.Write("warn", "Memory usage level now " + MemoryManager.AppMemoryUsageLevel + " (" + (MemoryManager.AppMemoryUsage >> 20) + " MB of " + (MemoryManager.AppMemoryUsageLimit >> 20) + " MB)");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame rootFrame))
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += (sender, args) => throw new InvalidOperationException("Failed to load page " + args.SourcePageType.FullName);

                // Use the whole screen; the TV-safe area is left to the web UI.
                ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
                Window.Current.Content = rootFrame;
            }

            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }

                Window.Current.Activate();
            }
        }
    }
}
