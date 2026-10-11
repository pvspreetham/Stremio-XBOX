using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using StremioXbox.Core;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace StremioXbox
{
    /// <summary>
    /// Hosts Stremio Web in WebView2, plays handed-over streams in the native Xbox player,
    /// and shows an on-screen debug log (View button).
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private const string StremioUrl = "https://web.stremio.com/";
        private const int VisibleLogLines = 40;

        // MF_MT_TRANSFER_FUNCTION: 15 = SMPTE ST 2084 (PQ / HDR10), 16 = HLG.
        private static readonly Guid TransferFunctionKey = new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93");

        private readonly List<string> _log = new List<string>();
        private readonly DisplayModeManager _displayModes = new DisplayModeManager();
        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private WebView2 _webView;
        private MediaPlayer _mediaPlayer;
        private PlayRequest _current;
        private bool _nativePlayerEnabled = true;
        private bool _displayRequestActive;
        private DispatcherTimer _diagnosticsTimer;
        private long _fileSize; // From the probe; 0 until known.
        private bool _bitrateLogged;
        private Stopwatch _paceClock;
        private TimeSpan _lastPosition;
        private double _lastDownload;
        private int _paceTicks;
        private readonly GamepadInput _gamepad = new GamepadInput();
        private DispatcherTimer _osdTimer;
        private DateTime _osdHideAt;
        private bool _cursorMode = ReadSetting("cursorMode", false);
        private bool _attachSubtitles = true; // LB in the debug log toggles it (stutter tests in 0.1.3.1).
        private HttpChunkCache _cache;
        private int _connections = 6; // 0 = hand the URL to the player directly; RB in the debug log cycles it.

        // Restart seek modes (Menu in the debug log cycles them): seconds to jump forward before returning;
        // positive = after playback starts, negative = before playing (0.1.3.7), 0 = off.
        private static readonly int[] RestartSeekModes = { 10, 30, 60, -60, 0 };
        private int _restartSeekMode;

        // Page zoom as real browser zoom (Left in the debug log cycles it, saved). Before a reinstall the page
        // sat at ~70% from a zoom level WebView2 had stored in the app data; at 100% the side menu runs off
        // screen. CSS zoom only shrank the 1920x1080 layout into a corner, so the app instead tells Chromium
        // the screen is 1920/zoom x 1080/zoom CSS px at zoom device pixels (DevTools Emulation), which is
        // what browser zoom does.
        private static readonly double[] ZoomSteps = { 0.6, 0.67, 0.7, 0.75, 0.8, 0.9, 1.0 };
        private double _pageZoom = ReadSetting("pageZoom", 1.0); // 100% by default (user decision); Left in the debug log tries others.
        private readonly bool _systemCursor = ReadSetting("systemCursor", false); // As set at this launch.
        private bool _systemCursorNext = ReadSetting("systemCursor", false); // Applies at the next launch.
        private bool _keyboardVisible; // Xbox on-screen keyboard is up: the app leaves the controller to it.

        // The app's own cursor, in page DIPs (the Xbox system cursor never engaged on the WebView2).
        private double _cursorX = 960;
        private double _cursorY = 540;
        private int _lastCursorMove;
        private double _pendingScroll;
        private int _lastBackTick = Environment.TickCount - 1000;
        private bool _pageAtHome = true; // From shell.js route messages.
        private long _lastCacheBytes;
        private int _subtitleChoice; // 0 = off, n = n-th subtitle track
        private string _osdMessage;

        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKeyActivated;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _gamepad.Pressed += OnPadButton;
            _gamepad.Polled += OnPadPolled;
            _gamepad.Start();
            WatchKeyboard();
            ApplyCursorMode();

            var version = Package.Current.Id.Version;
            Log("info", string.Format("Stremio-XBOX {0}.{1}.{2}", version.Major, version.Minor, version.Build));
            Log("info", DisplayModeManager.DescribeDisplay());
            UpdateDebugHeader();

            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            // Let video start without a real user gesture: controller input reaches the page as
            // synthetic events, which Chromium's autoplay policy would otherwise ignore.
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--autoplay-policy=no-user-gesture-required");
            _webView = _systemCursor ? new WebView2() : new WebView2 { RequiresPointer = RequiresPointer.Never };
            RootGrid.Children.Insert(0, _webView);
            _webView.WebMessageReceived += OnWebMessageReceived;
            _webView.NavigationCompleted += OnNavigationCompleted;

            try
            {
                await _webView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                Log("error", "WebView2 failed to start: " + ex.Message);
                SetDebugVisible(true);
                return;
            }

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsGeneralAutofillEnabled = false; // Autofill pops the virtual keyboard on Xbox.

            StartRequestLogging();

            // Links that would open a new window launch Edge and push this app to the background.
            _webView.CoreWebView2.NewWindowRequested += (sender, args) =>
            {
                args.Handled = true;
                Log("warn", "Blocked new window: " + args.Uri);
            };

            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Resources/shell.js"));
                var script = await FileIO.ReadTextAsync(file);
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
            }
            catch (Exception ex)
            {
                Log("error", "Could not inject shell script: " + ex.Message);
                SetDebugVisible(true);
            }

            _webView.Source = new Uri(StremioUrl);
            _webView.Focus(FocusState.Programmatic);
        }

        // Diagnostics for the missing home-screen catalogs (0.1.3.10): logs every request the page makes
        // to a host other than Stremio Web's own (addon manifests and catalogs), its status and time, and
        // requests that never get a response. Stremio's core fetches from a web worker, so shell.js cannot
        // see these; WebView2 can when the filter includes all source kinds.
        private const int LoggedRequests = 400;
        private readonly Dictionary<string, int> _pendingRequests = new Dictionary<string, int>();
        private int _requestLogs;
        private DispatcherTimer _requestTimer;

        private void StartRequestLogging()
        {
            var core = _webView.CoreWebView2;
            try
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                Log("info", "Request logging: all source kinds (page and workers)");
            }
            catch (Exception ex)
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                Log("warn", "Request logging: page only (" + ex.GetType().Name + ")");
            }

            core.WebResourceRequested += (sender, args) =>
            {
                var uri = args.Request.Uri;
                if (!IsLoggedRequest(uri, args.ResourceContext))
                {
                    return;
                }

                lock (_pendingRequests)
                {
                    _pendingRequests[args.Request.Method + " " + uri] = Environment.TickCount;
                }

                LogRequest("info", string.Format("Request {0} {1} {2}", args.ResourceContext, args.Request.Method, DescribeUri(uri)));
            };

            core.WebResourceResponseReceived += (sender, args) =>
            {
                var uri = args.Request.Uri;
                int started;
                lock (_pendingRequests)
                {
                    var key = args.Request.Method + " " + uri;
                    if (!_pendingRequests.TryGetValue(key, out started))
                    {
                        return;
                    }

                    _pendingRequests.Remove(key);
                }

                var status = args.Response.StatusCode;
                LogRequest(status >= 200 && status < 400 ? "info" : "warn", string.Format("Response {0} {1} in {2} ms: {3}",
                    status, args.Response.ReasonPhrase, unchecked(Environment.TickCount - started), DescribeUri(uri)));
            };

            // Requests still unanswered after 15 s (failed DNS/TLS/CORS or hung) are reported once.
            _requestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _requestTimer.Tick += (sender, e) =>
            {
                var now = Environment.TickCount;
                var stale = new List<KeyValuePair<string, int>>();
                lock (_pendingRequests)
                {
                    foreach (var pair in _pendingRequests)
                    {
                        if (unchecked(now - pair.Value) > 15000)
                        {
                            stale.Add(pair);
                        }
                    }

                    foreach (var pair in stale)
                    {
                        _pendingRequests.Remove(pair.Key);
                    }
                }

                foreach (var pair in stale)
                {
                    var space = pair.Key.IndexOf(' ');
                    LogRequest("warn", string.Format("No response after {0} s: {1} {2}", unchecked(now - pair.Value) / 1000,
                        pair.Key.Substring(0, space), DescribeUri(pair.Key.Substring(space + 1))));
                }
            };
            _requestTimer.Start();
        }

        private static bool IsLoggedRequest(string uri, CoreWebView2WebResourceContext context)
        {
            if (context == CoreWebView2WebResourceContext.Image || context == CoreWebView2WebResourceContext.Font ||
                context == CoreWebView2WebResourceContext.Stylesheet || context == CoreWebView2WebResourceContext.Media)
            {
                return false;
            }

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                return false;
            }

            return !parsed.Host.EndsWith("web.stremio.com", StringComparison.OrdinalIgnoreCase);
        }

        // Host and path, with long path segments (addon configs, API keys) shortened.
        private static string DescribeUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return uri.Length > 120 ? uri.Substring(0, 120) + "..." : uri;
            }

            var segments = parsed.AbsolutePath.Split('/').Select(segment => segment.Length > 32 ? "<" + segment.Length + " chars>" : segment);
            var text = parsed.Host + string.Join("/", segments) + (parsed.Query.Length > 0 ? "?<query>" : string.Empty);
            return text.Length > 200 ? text.Substring(0, 200) + "..." : text;
        }

        private void LogRequest(string level, string text)
        {
            if (Interlocked.Increment(ref _requestLogs) <= LoggedRequests)
            {
                LogAsync(level, text);
            }
        }

        private static T ReadSetting<T>(string key, T fallback)
        {
            try
            {
                return ApplicationData.Current.LocalSettings.Values[key] is T value ? value : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static void SaveSetting(string key, object value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception)
            {
            }
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task ApplyZoomAsync()
        {
            var core = _webView?.CoreWebView2;
            if (core == null)
            {
                return;
            }

            var width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : Window.Current.Bounds.Width;
            var height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : Window.Current.Bounds.Height;
            try
            {
                if (_pageZoom >= 0.999)
                {
                    await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
                }
                else
                {
                    await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", string.Format(
                        "{{\"width\":{0},\"height\":{1},\"deviceScaleFactor\":{2},\"mobile\":false}}",
                        (int)Math.Round(width / _pageZoom), (int)Math.Round(height / _pageZoom), Num(_pageZoom)));
                }

                Log("info", string.Format("Page zoom {0}% (window {1}x{2})", Math.Round(_pageZoom * 100), Math.Round(width), Math.Round(height)));
            }
            catch (Exception ex)
            {
                Log("warn", "Page zoom failed: " + ex.Message);
            }
        }

        // ---- The app's own cursor -------------------------------------------------------------
        private void SetCursorMode(bool enabled)
        {
            _cursorMode = enabled;
            SaveSetting("cursorMode", enabled);
            ApplyCursorMode();
            Log("info", "Cursor " + (enabled ? "on (left stick moves, A clicks, right stick scrolls, stick click turns it off)" : "off"));
            UpdateDebugHeader();
        }

        private void ApplyCursorMode()
        {
            if (_systemCursor)
            {
                _cursorMode = false; // The Xbox cursor replaces the app's own.
            }

            _gamepad.StickIsCursor = _cursorMode || _systemCursor;
            CursorLayer.Visibility = _cursorMode && PlayerLayer.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            PlaceCursor();
        }

        private void PlaceCursor()
        {
            Canvas.SetLeft(CursorDot, _cursorX - (CursorDot.Width / 2));
            Canvas.SetTop(CursorDot, _cursorY - (CursorDot.Height / 2));
        }

        private void OnPadPolled(Windows.Gaming.Input.GamepadReading reading)
        {
            // Hidden while the native player is up; frozen while the debug log or a text dialog is open.
            var visible = _cursorMode && PlayerLayer.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (CursorLayer.Visibility != visible)
            {
                CursorLayer.Visibility = visible;
            }

            var active = visible == Visibility.Visible && !_keyboardVisible && DebugPanel.Visibility != Visibility.Visible;

            if (!active)
            {
                return;
            }

            // Left stick: speed grows with the square of the deflection (fine aim near the centre).
            var x = reading.LeftThumbstickX;
            var y = reading.LeftThumbstickY;
            var magnitude = Math.Sqrt((x * x) + (y * y));
            if (magnitude > 0.15)
            {
                var speed = 1500 * ((magnitude - 0.15) / 0.85) * ((magnitude - 0.15) / 0.85) / 60; // DIPs per poll at full tilt: 25
                var width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1920;
                var height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 1080;
                _cursorX = Math.Max(0, Math.Min(width - 1, _cursorX + (x / magnitude * speed)));
                _cursorY = Math.Max(0, Math.Min(height - 1, _cursorY - (y / magnitude * speed)));
                PlaceCursor();
                if (unchecked(Environment.TickCount - _lastCursorMove) > 33)
                {
                    _lastCursorMove = Environment.TickCount;
                    SendMouse("mouseMoved", "none", 0, 0);
                }
            }

            // Right stick: wheel scroll at the cursor.
            var scroll = reading.RightThumbstickY;
            if (Math.Abs(scroll) > 0.2)
            {
                _pendingScroll += -scroll * 30;
            }

            if (Math.Abs(_pendingScroll) >= 60)
            {
                SendMouse("mouseWheel", "none", 0, _pendingScroll);
                _pendingScroll = 0;
            }
        }

        private void ClickAtCursor()
        {
            SendMouse("mousePressed", "left", 1, 0);
            SendMouse("mouseReleased", "left", 1, 0);
        }

        // Trusted input through DevTools; coordinates are page CSS px (DIPs divided by the page zoom).
        private void SendMouse(string type, string button, int clickCount, double deltaY)
        {
            var core = _webView?.CoreWebView2;
            if (core == null)
            {
                return;
            }

            var json = string.Format(
                "{{\"type\":\"{0}\",\"x\":{1},\"y\":{2},\"button\":\"{3}\",\"clickCount\":{4},\"deltaX\":0,\"deltaY\":{5}}}",
                type, Num(_cursorX / _pageZoom), Num(_cursorY / _pageZoom), button, clickCount, Num(deltaY));
            _ = SendDevToolsAsync(core, "Input.dispatchMouseEvent", json);
        }

        private async Task SendDevToolsAsync(CoreWebView2 core, string method, string json)
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(method, json);
            }
            catch (Exception ex)
            {
                Log("warn", method + " failed: " + ex.Message);
            }
        }

        // ---- On-screen keyboard -------------------------------------------------------------------
        // A on a text field focuses it and WebView2 opens the Xbox keyboard (default behaviour). While the
        // keyboard is up the app must not act on the controller too, or the D-pad moved the page highlight
        // off the field and the typed text went nowhere (0.1.3.11 first test).
        private void WatchKeyboard()
        {
            try
            {
                var pane = Windows.UI.ViewManagement.InputPane.GetForCurrentView();
                pane.Showing += (sender, e) => SetKeyboardVisible(true, "InputPane");
                pane.Hiding += (sender, e) => SetKeyboardVisible(false, "InputPane");
            }
            catch (Exception ex)
            {
                Log("warn", "InputPane unavailable: " + ex.Message);
            }

            try
            {
                var view = Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView();
                view.PrimaryViewShowing += (sender, e) => SetKeyboardVisible(true, "CoreInputView");
                view.PrimaryViewHiding += (sender, e) => SetKeyboardVisible(false, "CoreInputView");
            }
            catch (Exception ex)
            {
                Log("warn", "CoreInputView unavailable: " + ex.Message);
            }
        }

        // ---- Typing ---------------------------------------------------------------------------------
        // WebView2's Xbox keyboard delivered keys only right after focus had come back to the WebView2 from a
        // native text box (after the text dialog, and after the invisible typing box let go); after a plain
        // startup focus, the keyboard opened but no key arrived. So before every page text field, focus
        // passes through the hidden ProxyText and back, then the page focuses the field again.
        private bool _priming;
        private bool _typingReady; // One pass per page load was enough before (password after the dialog).

        private async Task PrimeTypingAsync(double fieldId, string reason, bool alwaysRefocus)
        {
            if (_priming || _webView == null)
            {
                return;
            }

            if (_typingReady)
            {
                if (alwaysRefocus)
                {
                    SendRefocusField(fieldId);
                }

                return;
            }

            _priming = true;
            try
            {
                // Focusable only during the pass: otherwise, when the page is hidden for the player, focus fell
                // to this box and A (pause) opened the keyboard.
                ProxyText.IsTabStop = true;
                var viaBox = ProxyText.Focus(FocusState.Keyboard);
                await Task.Delay(120);
                var back = _webView.Focus(FocusState.Keyboard);
                ProxyText.IsTabStop = false;
                await Task.Delay(80);
                _typingReady = viaBox && back && fieldId > 0;
                SendRefocusField(fieldId);
                Log("info", "Typing focus pass (" + reason + "): native box " + (viaBox ? "ok" : "FAILED") + ", WebView2 " + (back ? "ok" : "FAILED"));
            }
            catch (Exception ex)
            {
                Log("warn", "Typing focus pass failed: " + ex.Message);
            }
            finally
            {
                _priming = false;
            }
        }

        private void SendRefocusField(double fieldId)
        {
            if (fieldId <= 0)
            {
                return;
            }

            var message = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("refocusField"),
                ["id"] = JsonValue.CreateNumberValue(fieldId),
            };
            _webView?.CoreWebView2?.PostWebMessageAsString(message.Stringify());
        }

        private void SetKeyboardVisible(bool visible, string source)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_keyboardVisible != visible)
                {
                    _keyboardVisible = visible;
                    Log("info", "On-screen keyboard " + (visible ? "shown" : "hidden") + " (" + source + ")");
                }
            });
        }

        private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                Log("info", "Loaded " + sender.Source);
                SendConfigToPage();
                if (PlayerLayer.Visibility != Visibility.Visible)
                {
                    // At startup the Focus call runs before the page exists; focus it again once loaded.
                    _typingReady = false;
                    _ = PrimeTypingAsync(0, "page loaded", false);
                }
                _ = ApplyZoomAsync();
            }
            else
            {
                Log("error", "Navigation failed: " + args.WebErrorStatus);
                SetDebugVisible(true);
            }
        }

        private void OnWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string raw;
            try
            {
                raw = args.TryGetWebMessageAsString();
            }
            catch (Exception)
            {
                return;
            }

            // Only our own messages have a string "type"; Stremio Web's desktop-shell IPC uses numbers.
            if (!JsonObject.TryParse(raw, out var message) ||
                !message.ContainsKey("type") ||
                message["type"].ValueType != JsonValueType.String)
            {
                return;
            }

            switch (message.GetNamedString("type", string.Empty))
            {
                case "log":
                    Log(message.GetNamedString("level", "info"), "web: " + message.GetNamedString("text", string.Empty));
                    break;
                case "play":
                    StartNativePlayback(PlayRequest.FromJson(message));
                    break;
                case "textFocus":
                    _ = PrimeTypingAsync(message.GetNamedNumber("id", 0), "field focused by page", false);
                    break;
                case "typingStart":
                    _ = PrimeTypingAsync(message.GetNamedNumber("id", 0), "A on field", true);
                    break;
                case "route":
                    _pageAtHome = message.GetNamedBoolean("home", true);
                    break;
            }
        }

        private void SendConfigToPage()
        {
            var config = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("config"),
                ["nativePlayer"] = JsonValue.CreateBooleanValue(_nativePlayerEnabled),
            };

            try
            {
                _webView?.CoreWebView2?.PostWebMessageAsString(config.Stringify());
            }
            catch (Exception ex)
            {
                Log("error", "Could not send config to page: " + ex.Message);
            }
        }

        private void StartNativePlayback(PlayRequest request)
        {
            if (!_nativePlayerEnabled)
            {
                return;
            }

            StopPlayback(resetDisplay: false);
            _current = request;
            Log("info", "Native play: " + request);
            Log("info", "Stream URL: " + request.Url);
            _fileSize = 0;
            _bitrateLogged = false;
            if (request.HasRequestHeaders)
            {
                Log("warn", "This stream asks for custom request headers; the native player does not send them yet.");
            }

            _mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.Movie,
            };
            _mediaPlayer.MediaOpened += OnMediaOpened;
            _mediaPlayer.MediaFailed += OnMediaFailed;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.PlaybackSession.BufferingStarted += OnBufferingStarted;
            _mediaPlayer.PlaybackSession.BufferingEnded += OnBufferingEnded;

            Player.SetMediaPlayer(_mediaPlayer);
            PlayerLayer.Visibility = Visibility.Visible;
            _typingReady = false; // The page loses focus to the player; set typing up again afterwards.

            // Hide Stremio Web during native playback so the page stops rendering behind the video.
            if (_webView != null)
            {
                _webView.Visibility = Visibility.Collapsed;
            }

            _ = OpenSourceAsync(request, _mediaPlayer);
            Player.Focus(FocusState.Programmatic);
        }

        // Resolves the addon URL (redirects, size), then either feeds the player from the app's own
        // multi-connection RAM buffer or, as a fallback, hands it the URL directly.
        private async Task OpenSourceAsync(PlayRequest request, MediaPlayer player)
        {
            try
            {
                var probe = await HttpChunkCache.ResolveAsync(request.Url, LogAsync);
                if (player != _mediaPlayer)
                {
                    return; // Stopped while resolving.
                }

                _fileSize = probe?.Size ?? 0;
                var contentType = GuessContentType(request, probe?.ContentType);
                MediaSource source;
                if (_connections > 0 && probe != null && probe.AcceptsRanges && probe.Size > 0 && contentType != null)
                {
                    var ahead = PickReadAheadBytes();
                    _cache = new HttpChunkCache(request.Url, probe.FinalUrl, probe.Size, _connections, ahead, LogAsync);
                    _lastCacheBytes = 0;
                    source = MediaSource.CreateFromStream(new BufferedHttpStream(_cache, 0), contentType);
                    Log("info", string.Format("Buffered playback: {0} connections, {1} MB read-ahead, {2}", _connections, ahead >> 20, contentType));
                }
                else
                {
                    Log("info", "Direct URL playback (" + (_connections == 0 ? "selected in debug log" :
                        probe == null ? "probe failed" : !probe.AcceptsRanges ? "no range support" : contentType == null ? "unknown container" : "size unknown") + ")");
                    source = MediaSource.CreateFromUri(new Uri(request.Url));
                }

                if (_attachSubtitles)
                {
                    AddExternalSubtitles(source, request);
                }
                else
                {
                    Log("info", "External subtitles: off (" + request.Subtitles.Count + " available)");
                }

                var item = new MediaPlaybackItem(source);
                item.TimedMetadataTracksChanged += (sender2, args2) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogSubtitleTracks(sender2));
                _subtitleChoice = 0;
                player.Source = item;
            }
            catch (Exception ex)
            {
                Log("error", "Could not open stream: " + ex.Message);
                StopPlayback(resetDisplay: true);
                SetDebugVisible(true);
            }
        }

        // Leaves room for the app (about 1.3 GB during 4K playback) inside the console's memory budget.
        private static long PickReadAheadBytes()
        {
            const long mb = 1024 * 1024;
            var free = (long)MemoryManager.AppMemoryUsageLimit - (long)MemoryManager.AppMemoryUsage;
            return Math.Max(128 * mb, Math.Min(768 * mb, free - (1024 * mb)));
        }

        private static string GuessContentType(PlayRequest request, string probedType)
        {
            var text = string.Join(" ", request.FileName, request.Description, request.Name, request.Url);
            var match = Regex.Match(text, @"\.(mkv|mp4|m4v|webm|mov)(?![a-z0-9])", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "mkv":
                        return "video/x-matroska";
                    case "webm":
                        return "video/webm";
                    case "mov":
                        return "video/quicktime";
                    default:
                        return "video/mp4";
                }
            }

            return !string.IsNullOrEmpty(probedType) && probedType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? probedType : null;
        }

        // Some encodes (e.g. x265 Blu-ray MKVs) carry HDR only in the HEVC bitstream, and the Xbox reports
        // no transfer function for them. Used only in that case.
        private static bool NameSaysHdr(PlayRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var text = string.Join(" ", request.FileName, request.Description, request.Name);
            return Regex.IsMatch(text, @"(?<![a-z])(hdr|hdr10|hdr10\+|dv|dovi|dolby.?vision)(?![a-z])", RegexOptions.IgnoreCase);
        }

        // Logs (0.1.3.5) show the player reading a second position far ahead of playback, and playback
        // slowing whenever that read waits; a manual +60 s then back during playback removed it every time,
        // while the same seek done before playing (0.1.3.7) did not hold on every file.
        private async Task RestartSeekBeforePlayAsync(MediaPlayer player, int seconds)
        {
            var session = player.PlaybackSession;
            if (_cache == null || !session.CanSeek || session.NaturalDuration < TimeSpan.FromSeconds(seconds + 60))
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            var forward = await SeekAndWaitAsync(session, TimeSpan.FromSeconds(seconds));
            var back = await SeekAndWaitAsync(session, TimeSpan.Zero);
            Log(forward && back ? "info" : "warn", string.Format("Restart seek before play (+{0} s, then 0:00) {1} in {2} ms",
                seconds, forward && back ? "done" : "timed out", watch.ElapsedMilliseconds));
        }

        // Same as a manual skip: once playing, jump forward, stay a second, and jump back.
        private async Task RestartSeekAfterPlayAsync(MediaPlayer player, int seconds)
        {
            var session = player.PlaybackSession;
            if (_cache == null || !session.CanSeek || session.NaturalDuration < TimeSpan.FromSeconds(seconds + 60))
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            while (player == _mediaPlayer && watch.Elapsed < TimeSpan.FromSeconds(15) &&
                !(session.PlaybackState == MediaPlaybackState.Playing && session.Position >= TimeSpan.FromSeconds(1)))
            {
                await Task.Delay(100);
            }

            if (player != _mediaPlayer)
            {
                return;
            }

            var from = session.Position;
            Log("info", string.Format("Restart seek after play: +{0} s from {1:hh\\:mm\\:ss} (waited {2} ms for playback)", seconds, from, watch.ElapsedMilliseconds));
            watch.Restart();
            var forward = await SeekAndWaitAsync(session, from + TimeSpan.FromSeconds(seconds));
            await Task.Delay(1000);
            var back = player == _mediaPlayer && await SeekAndWaitAsync(session, from);
            Log(forward && back ? "info" : "warn", string.Format("Restart seek after play {0} in {1} ms",
                forward && back ? "done" : "timed out", watch.ElapsedMilliseconds));
        }

        private string DescribeRestartSeek()
        {
            var seconds = RestartSeekModes[_restartSeekMode];
            return seconds == 0 ? "off" : seconds > 0 ? "+" + seconds + " s after play" : "+" + (-seconds) + " s before play";
        }

        private async Task<bool> SeekAndWaitAsync(MediaPlaybackSession session, TimeSpan target)
        {
            var done = new TaskCompletionSource<bool>();
            Windows.Foundation.TypedEventHandler<MediaPlaybackSession, object> handler = (s, e) => done.TrySetResult(true);
            session.SeekCompleted += handler;
            try
            {
                _cache?.SeekStarted();
                session.Position = target;
                return await Task.WhenAny(done.Task, Task.Delay(10000)) == done.Task;
            }
            finally
            {
                session.SeekCompleted -= handler;
            }
        }

        // Holds playback until ~15 s of video is in RAM (or 15 s pass), so the first busy scenes have headroom.
        private async Task PrebufferAsync(MediaPlayer player)
        {
            var cache = _cache;
            var duration = player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (cache == null || duration <= 0)
            {
                return;
            }

            var bytesPerSecond = cache.Size / duration;
            var target = (long)Math.Min(cache.AheadBytes / 2, bytesPerSecond * 15);
            var watch = Stopwatch.StartNew();
            _osdMessage = "Buffering...";
            ShowOsd();
            while (cache.BufferedAheadBytes < target && watch.Elapsed < TimeSpan.FromSeconds(15) && player == _mediaPlayer)
            {
                await Task.Delay(250);
            }

            var buffered = cache.BufferedAheadBytes;
            _osdMessage = null;
            Log("info", string.Format("Prebuffered {0} MB ({1:0.0} s of video) in {2} ms", buffered >> 20, buffered / bytesPerSecond, watch.ElapsedMilliseconds));
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                var video = DescribeMedia(sender, out var width, out var height, out var frameRate, out var transferFunction, out var subtype);
                Log("info", video);

                // Decided from what the decoder reports, not from release names: Dolby Vision only when
                // the Xbox decodes it as DV (subtype DVHE/DVH1); DV MKVs decode as HEVC + HDR10 base layer.
                var range = IsDolbyVisionSubtype(subtype) ? VideoRange.DolbyVision : RangeFromTransferFunction(transferFunction);
                var fromName = false;
                if (range == VideoRange.Sdr && transferFunction == null && NameSaysHdr(_current))
                {
                    range = VideoRange.Hdr10;
                    fromName = true;
                }

                Log("info", "Video range: " + range + " (decoder subtype " + subtype + ", transfer function " + (transferFunction?.ToString() ?? "unknown") + (fromName ? ", HDR from release name" : string.Empty) + ")");
                Log("info", await _displayModes.SwitchAsync(width, height, frameRate, range));

                if (!_displayRequestActive)
                {
                    _displayRequest.RequestActive();
                    _displayRequestActive = true;
                }

                var restartSeek = RestartSeekModes[_restartSeekMode];
                if (restartSeek < 0)
                {
                    await RestartSeekBeforePlayAsync(sender, -restartSeek);
                }

                await PrebufferAsync(sender);
                if (sender != _mediaPlayer)
                {
                    return;
                }

                sender.Play();
                StartDiagnostics();
                ShowOsd();
                if (restartSeek > 0)
                {
                    await RestartSeekAfterPlayAsync(sender, restartSeek);
                }
            });
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                var code = args.ExtendedErrorCode == null ? string.Empty : string.Format(" (0x{0:X8})", args.ExtendedErrorCode.HResult);
                LogPlaybackStatus("at failure");
                Log("error", "Native player failed: " + args.Error + code + " " + args.ErrorMessage);
                StopPlayback(resetDisplay: true);
                SetDebugVisible(true);
            });
        }

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (sender == _mediaPlayer)
                {
                    StopPlayback(resetDisplay: true);
                }
            });
        }

        private void StopPlayback(bool resetDisplay)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.MediaOpened -= OnMediaOpened;
                _mediaPlayer.MediaFailed -= OnMediaFailed;
                _mediaPlayer.MediaEnded -= OnMediaEnded;
                _mediaPlayer.PlaybackSession.BufferingStarted -= OnBufferingStarted;
                _mediaPlayer.PlaybackSession.BufferingEnded -= OnBufferingEnded;
                Player.SetMediaPlayer(null);
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            if (_cache != null)
            {
                // Release the read-ahead buffers now so the next playback gets the full budget
                // (0.1.3.4 got 524 MB instead of 768 MB right after a previous file).
                _cache.Dispose();
                _cache = null;
                GC.Collect();
            }

            _diagnosticsTimer?.Stop();
            _osdTimer?.Stop();
            Osd.Visibility = Visibility.Collapsed;
            PlayerLayer.Visibility = Visibility.Collapsed;
            if (_webView != null)
            {
                _webView.Visibility = Visibility.Visible;
            }

            if (_displayRequestActive)
            {
                _displayRequest.RequestRelease();
                _displayRequestActive = false;
            }

            if (resetDisplay)
            {
                _ = _displayModes.ResetAsync();
                _webView?.Focus(FocusState.Programmatic);
            }
        }

        private static string DescribeMedia(MediaPlayer player, out uint width, out uint height, out double frameRate, out uint? transferFunction, out string subtype)
        {
            subtype = string.Empty;
            width = player.PlaybackSession.NaturalVideoWidth;
            height = player.PlaybackSession.NaturalVideoHeight;
            frameRate = 0;
            transferFunction = null;
            var parts = new List<string>();

            if (player.Source is MediaPlaybackItem item)
            {
                if (item.VideoTracks.Count > 0)
                {
                    var props = item.VideoTracks[0].GetEncodingProperties();
                    subtype = props.Subtype ?? string.Empty;
                    if (props.FrameRate.Denominator != 0)
                    {
                        frameRate = (double)props.FrameRate.Numerator / props.FrameRate.Denominator;
                    }

                    if (props.Properties.TryGetValue(TransferFunctionKey, out var value) && value != null)
                    {
                        transferFunction = Convert.ToUInt32(value);
                    }

                    parts.Add(string.Format("video {0} {1}x{2} {3:0.###}fps", props.Subtype, width, height, frameRate));
                }

                parts.AddRange(item.AudioTracks.Select(t =>
                {
                    var audio = t.GetEncodingProperties();
                    return string.Format("audio {0} {1}ch [{2}]", audio.Subtype, audio.ChannelCount, t.Language);
                }));
            }

            return "Opened: " + (parts.Count == 0 ? "no track info" : string.Join(", ", parts));
        }

        private static bool IsDolbyVisionSubtype(string subtype)
        {
            var value = (subtype ?? string.Empty).ToUpperInvariant();
            return value.Contains("DVH") || value.Contains("DOVI") || value.Contains("DVAV");
        }

        private static VideoRange RangeFromTransferFunction(uint? transferFunction)
        {
            switch (transferFunction)
            {
                case 15:
                    return VideoRange.Hdr10;
                case 16:
                    return VideoRange.Hlg;
                default:
                    return VideoRange.Sdr;
            }
        }

        private void StartDiagnostics()
        {
            if (_diagnosticsTimer == null)
            {
                _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _diagnosticsTimer.Tick += (sender, e) =>
                {
                    LogPace();
                    if (++_paceTicks % 6 == 0)
                    {
                        LogPlaybackStatus("status");
                    }
                };
            }

            _paceTicks = 0;
            _paceClock = null;
            _diagnosticsTimer.Start();
        }

        // Every 5 s: how far playback advanced against the wall clock (a stall shows as media < wall
        // while Playing) and the download rate from DownloadProgress x file size.
        private void LogPace()
        {
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                var position = session.Position;
                double download;
                try
                {
                    download = session.DownloadProgress;
                }
                catch (Exception)
                {
                    download = -1;
                }

                var state = session.PlaybackState;
                if (_paceClock == null)
                {
                    _paceClock = Stopwatch.StartNew();
                    _lastPosition = position;
                    _lastDownload = download;
                    return;
                }

                var wall = _paceClock.Elapsed.TotalSeconds;
                _paceClock.Restart();
                var media = (position - _lastPosition).TotalSeconds;
                var rate = "n/a";
                var cache = _cache;
                if (cache != null)
                {
                    var bytes = cache.DownloadedBytes;
                    var bytesPerSecond = (bytes - _lastCacheBytes) / wall;
                    _lastCacheBytes = bytes;
                    var ahead = cache.BufferedAheadBytes;
                    var aheadSeconds = _fileSize > 0 && session.NaturalDuration.TotalSeconds > 0 ? ahead / (_fileSize / session.NaturalDuration.TotalSeconds) : 0;
                    rate = string.Format("{0:0} Mbps, buffer {1} MB ({2:0} s), waited reads {3}", bytesPerSecond * 8 / 1000000, ahead >> 20, aheadSeconds, cache.WaitedReads);
                }
                else if (_fileSize > 0 && download >= 0 && _lastDownload >= 0 && wall > 0)
                {
                    var bytesPerSecond = (download - _lastDownload) * _fileSize / wall;
                    rate = (bytesPerSecond / 1048576).ToString("0.0") + " MB/s (" + (bytesPerSecond * 8 / 1000000).ToString("0") + " Mbps)";
                }

                var stalled = state == MediaPlaybackState.Playing && media < wall * 0.9;
                Log(stalled ? "warn" : "info", string.Format(
                    "Pace{0}: {1:hh\\:mm\\:ss} +{2:0.0}s media / {3:0.0}s wall, download {4} ({5:P1}), {6}",
                    stalled ? " STALL" : string.Empty, position, media, wall, rate, download, state));
                if (cache != null)
                {
                    Log("info", cache.TakeViewSummary());
                }

                _lastPosition = position;
                _lastDownload = download;

                var duration = session.NaturalDuration.TotalSeconds;
                if (!_bitrateLogged && _fileSize > 0 && duration > 0)
                {
                    _bitrateLogged = true;
                    Log("info", string.Format("File {0:0.00} GB, average bitrate {1:0.0} Mbps", _fileSize / 1073741824.0, _fileSize * 8 / duration / 1000000));
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Pace unavailable: " + ex.GetType().Name);
            }
        }

        private void LogPlaybackStatus(string reason)
        {
            // Each value is read separately: on Xbox some PlaybackSession properties throw
            // (InvalidCastException seen in 0.1.1), and logging must never block playback control.
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                Log("info", "Playback " + reason + ": " +
                    Read(() => session.Position.ToString(@"hh\:mm\:ss")) + " / " +
                    Read(() => session.NaturalDuration.ToString(@"hh\:mm\:ss")) +
                    ", state " + Read(() => session.PlaybackState) +
                    ", downloaded " + Read(() => session.DownloadProgress.ToString("P1")) +
                    ", memory " + Read(() => MemoryManager.AppMemoryUsage >> 20) + " / " + Read(() => MemoryManager.AppMemoryUsageLimit >> 20) + " MB");
            }
            catch (Exception ex)
            {
                Log("warn", "Playback status unavailable: " + ex.GetType().Name);
            }
        }

        private static string Read<T>(Func<T> read)
        {
            try
            {
                return Convert.ToString(read());
            }
            catch (Exception ex)
            {
                return "n/a(" + ex.GetType().Name + ")";
            }
        }

        private void OnBufferingStarted(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogPlaybackStatus("buffering started"));
        }

        private void OnBufferingEnded(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogPlaybackStatus("buffering ended"));
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = HandleBack();
        }

        // One B press can reach both the system back event and the page; act on it once.
        private bool HandleBack()
        {
            if (unchecked(Environment.TickCount - _lastBackTick) < 500)
            {
                return true;
            }

            _lastBackTick = Environment.TickCount;
            if (_keyboardVisible)
            {
                return true; // B closes the keyboard; do not also go back.
            }

            if (PlayerLayer.Visibility == Visibility.Visible)
            {
                LogPlaybackStatus("stopped by user");
                StopPlayback(resetDisplay: true);
                Log("info", "Playback stopped by user");
                return true;
            }

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                SetDebugVisible(false);
                return true;
            }

            // Off the home screen, go back in Stremio's own history (WebView2's GoBack ignores pushState
            // routes). On home, leave B unhandled as before.
            if (_webView?.CoreWebView2 != null && !_pageAtHome)
            {
                _ = _webView.CoreWebView2.ExecuteScriptAsync("window.__sxBack && window.__sxBack()");
                return true;
            }

            return false;
        }

        // Keyboard shortcuts for testing on a PC. Controller input comes from GamepadInput.
        private void OnAcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.EventType != CoreAcceleratorKeyEventType.KeyDown || args.KeyStatus.WasKeyDown)
            {
                return;
            }

            switch (args.VirtualKey)
            {
                case VirtualKey.F1:
                    OnPadButton(PadButton.View);
                    args.Handled = true;
                    break;
                case VirtualKey.F2:
                    OnPadButton(PadButton.Y);
                    args.Handled = true;
                    break;
                case VirtualKey.F3:
                    OnPadButton(PadButton.X);
                    args.Handled = true;
                    break;
            }
        }

        private void OnPadButton(PadButton button)
        {
            if (_keyboardVisible)
            {
                return; // The Xbox keyboard takes the controller.
            }

            if (button == PadButton.View)
            {
                SetDebugVisible(DebugPanel.Visibility != Visibility.Visible);
                return;
            }

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                if (button == PadButton.Y)
                {
                    _nativePlayerEnabled = !_nativePlayerEnabled;
                    SendConfigToPage();
                    UpdateDebugHeader();
                }
                else if (button == PadButton.X)
                {
                    SetCursorMode(!_cursorMode);
                }
                else if (button == PadButton.Right)
                {
                    _systemCursorNext = !_systemCursorNext;
                    SaveSetting("systemCursor", _systemCursorNext);
                    Log("info", "System cursor " + (_systemCursorNext ? "ON" : "OFF") + " after the app restarts");
                    UpdateDebugHeader();
                }
                else if (button == PadButton.Left)
                {
                    var next = Array.FindIndex(ZoomSteps, step => step > _pageZoom + 0.001);
                    _pageZoom = next < 0 ? ZoomSteps[0] : ZoomSteps[next];
                    SaveSetting("pageZoom", _pageZoom);
                    _ = ApplyZoomAsync();
                    UpdateDebugHeader();
                }
                else if (button == PadButton.RightShoulder)
                {
                    _connections = _connections == 0 ? 1 : _connections == 1 ? 2 : _connections == 2 ? 4 : _connections == 4 ? 6 : 0;
                    Log("info", "Download mode: " + DescribeConnections() + " (applies to the next playback)");
                    UpdateDebugHeader();
                }
                else if (button == PadButton.Menu)
                {
                    _restartSeekMode = (_restartSeekMode + 1) % RestartSeekModes.Length;
                    Log("info", "Restart seek: " + DescribeRestartSeek() + " (applies to the next playback)");
                    UpdateDebugHeader();
                }
                else if (button == PadButton.LeftShoulder)
                {
                    _attachSubtitles = !_attachSubtitles;
                    Log("info", "Attach external subtitles: " + (_attachSubtitles ? "ON" : "OFF") + " (applies to the next playback)");
                    UpdateDebugHeader();
                }

                return;
            }

            if (PlayerLayer.Visibility == Visibility.Visible)
            {
                HandlePlayerButton(button);
                return;
            }

            if (_systemCursor)
            {
                // The Xbox cursor owns the left stick and A (real clicks); the D-pad still moves the highlight.
                if (button == PadButton.A || button == PadButton.LeftStick)
                {
                    return;
                }
            }
            else if (button == PadButton.LeftStick)
            {
                SetCursorMode(!_cursorMode);
                return;
            }

            if (_cursorMode)
            {
                if (button == PadButton.A)
                {
                    ClickAtCursor();
                }

                return;
            }

            switch (button)
            {
                case PadButton.Up:
                    SendNavigation("up");
                    break;
                case PadButton.Down:
                    SendNavigation("down");
                    break;
                case PadButton.Left:
                    SendNavigation("left");
                    break;
                case PadButton.Right:
                    SendNavigation("right");
                    break;
                case PadButton.A:
                    SendNavigation("activate");
                    break;
            }
        }

        private void SendNavigation(string action)
        {
            try
            {
                var message = new JsonObject
                {
                    ["type"] = JsonValue.CreateStringValue("nav"),
                    ["action"] = JsonValue.CreateStringValue(action),
                };
                _webView?.CoreWebView2?.PostWebMessageAsString(message.Stringify());
            }
            catch (Exception ex)
            {
                Log("error", "Could not send navigation: " + ex.Message);
            }
        }

        private void HandlePlayerButton(PadButton button)
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null)
            {
                return;
            }

            switch (button)
            {
                case PadButton.Y:
                    CycleAudioTrack();
                    break;
                case PadButton.X:
                    CycleSubtitles();
                    break;
                case PadButton.A:
                case PadButton.Menu:
                    if (session.PlaybackState == MediaPlaybackState.Playing)
                    {
                        _mediaPlayer.Pause();
                    }
                    else
                    {
                        _mediaPlayer.Play();
                    }

                    break;
                case PadButton.Left:
                    Seek(session, -10);
                    break;
                case PadButton.Right:
                    Seek(session, 10);
                    break;
                case PadButton.LeftShoulder:
                    Seek(session, -60);
                    break;
                case PadButton.RightShoulder:
                    Seek(session, 60);
                    break;
            }

            ShowOsd();
        }

        private void Seek(MediaPlaybackSession session, double seconds)
        {
            if (!session.CanSeek)
            {
                return;
            }

            var target = session.Position + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }

            if (session.NaturalDuration > TimeSpan.Zero && target > session.NaturalDuration)
            {
                target = session.NaturalDuration - TimeSpan.FromSeconds(1);
            }

            _cache?.SeekStarted();
            session.Position = target;
        }

        private void AddExternalSubtitles(MediaSource source, PlayRequest request)
        {
            foreach (var subtitle in request.Subtitles)
            {
                try
                {
                    var label = string.IsNullOrEmpty(subtitle.Label) ? subtitle.Language : subtitle.Label;
                    var language = subtitle.Language;
                    var timedText = TimedTextSource.CreateFromUri(new Uri(subtitle.Url));
                    timedText.Resolved += (sender, args) =>
                    {
                        if (args.Error != null)
                        {
                            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Log("warn", "Subtitle failed (" + label + "): " + args.Error.ErrorCode));
                            return;
                        }

                        foreach (var track in args.Tracks)
                        {
                            track.Label = label;
                        }
                    };
                    source.ExternalTimedTextSources.Add(timedText);
                }
                catch (Exception ex)
                {
                    Log("warn", "Subtitle skipped (" + subtitle.Label + "): " + ex.Message);
                }
            }

            Log("info", "External subtitles: " + request.Subtitles.Count);
        }

        private static bool IsSubtitleTrack(TimedMetadataTrack track)
        {
            return track.TimedMetadataKind == TimedMetadataKind.Subtitle ||
                   track.TimedMetadataKind == TimedMetadataKind.Caption ||
                   track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle;
        }

        private static string DescribeSubtitle(TimedMetadataTrack track)
        {
            var label = string.IsNullOrEmpty(track.Label) ? (track.Language + " (embedded)") : track.Label;
            return track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle ? label + " [image]" : label;
        }

        private void LogSubtitleTracks(MediaPlaybackItem item)
        {
            try
            {
                var tracks = item.TimedMetadataTracks.Where(IsSubtitleTrack).Select(DescribeSubtitle).ToList();
                Log("info", "Subtitle tracks: " + tracks.Count + (tracks.Count > 0 ? " - " + string.Join(", ", tracks.Take(15)) : string.Empty));
            }
            catch (Exception ex)
            {
                Log("warn", "Could not list subtitle tracks: " + ex.Message);
            }
        }

        private void CycleSubtitles()
        {
            if (!(_mediaPlayer?.Source is MediaPlaybackItem item))
            {
                return;
            }

            try
            {
                var tracks = item.TimedMetadataTracks;
                var indices = Enumerable.Range(0, tracks.Count).Where(i => IsSubtitleTrack(tracks[i])).ToList();
                if (indices.Count == 0)
                {
                    _osdMessage = "No subtitles";
                    return;
                }

                _subtitleChoice = (_subtitleChoice + 1) % (indices.Count + 1);
                foreach (var index in indices)
                {
                    tracks.SetPresentationMode((uint)index, TimedMetadataTrackPresentationMode.Disabled);
                }

                if (_subtitleChoice == 0)
                {
                    _osdMessage = "Subtitles: off";
                }
                else
                {
                    var index = indices[_subtitleChoice - 1];
                    tracks.SetPresentationMode((uint)index, TimedMetadataTrackPresentationMode.PlatformPresented);
                    _osdMessage = "Subtitles " + _subtitleChoice + "/" + indices.Count + ": " + DescribeSubtitle(tracks[index]);
                }

                Log("info", _osdMessage);
            }
            catch (Exception ex)
            {
                _osdMessage = "Subtitles unavailable";
                Log("warn", "Subtitle switch failed: " + ex.Message);
            }
        }

        private void CycleAudioTrack()
        {
            if (!(_mediaPlayer?.Source is MediaPlaybackItem item))
            {
                return;
            }

            try
            {
                var tracks = item.AudioTracks;
                if (tracks.Count < 2)
                {
                    _osdMessage = "Only one audio track";
                    return;
                }

                var next = (tracks.SelectedIndex + 1) % tracks.Count;
                tracks.SelectedIndex = next;
                var props = tracks[next].GetEncodingProperties();
                var label = string.IsNullOrEmpty(tracks[next].Label) ? string.Empty : " " + tracks[next].Label;
                _osdMessage = string.Format("Audio {0}/{1}: [{2}] {3} {4}ch{5}", next + 1, tracks.Count, tracks[next].Language, props.Subtype, props.ChannelCount, label);
                Log("info", _osdMessage);
            }
            catch (Exception ex)
            {
                _osdMessage = "Audio switch failed";
                Log("warn", "Audio switch failed: " + ex.Message);
            }
        }

        private void ShowOsd()
        {
            if (_osdTimer == null)
            {
                _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _osdTimer.Tick += (sender, e) => UpdateOsd();
            }

            _osdHideAt = DateTime.UtcNow.AddSeconds(4);
            Osd.Visibility = Visibility.Visible;
            UpdateOsd();
            _osdTimer.Start();
        }

        private void UpdateOsd()
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null)
            {
                _osdTimer?.Stop();
                Osd.Visibility = Visibility.Collapsed;
                return;
            }

            var paused = session.PlaybackState != MediaPlaybackState.Playing;
            if (!paused && DateTime.UtcNow >= _osdHideAt)
            {
                _osdTimer.Stop();
                _osdMessage = null;
                Osd.Visibility = Visibility.Collapsed;
                return;
            }

            var duration = session.NaturalDuration;
            OsdTitle.Text = _current?.ToString() ?? string.Empty;
            OsdInfo.Text = _osdMessage ?? string.Empty;
            OsdInfo.Visibility = string.IsNullOrEmpty(_osdMessage) ? Visibility.Collapsed : Visibility.Visible;
            OsdProgress.Value = duration.TotalSeconds > 0 ? session.Position.TotalSeconds / duration.TotalSeconds : 0;
            OsdTime.Text = string.Format(
                "{0}   {1:h\\:mm\\:ss} / {2:h\\:mm\\:ss}",
                paused ? (session.PlaybackState == MediaPlaybackState.Buffering ? "Buffering" : "Paused") : "Playing",
                session.Position,
                duration);
        }

        private void SetDebugVisible(bool visible)
        {
            DebugPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDebugHeader()
        {
            DebugHeader.Text = "Debug log  |  View: hide  |  Y: native player " + (_nativePlayerEnabled ? "ON" : "OFF") +
                "  |  X: cursor " + (_cursorMode ? "ON" : "OFF") + "  |  Right: system cursor " + (_systemCursorNext ? "ON" : "OFF") + (_systemCursorNext != _systemCursor ? " (restart)" : string.Empty) + "  |  Left: zoom " + Math.Round(_pageZoom * 100) + "%" +
                "  |  LB: subtitles " + (_attachSubtitles ? "ON" : "OFF") + "  |  RB: " + DescribeConnections() +
                "  |  Menu: seek " + DescribeRestartSeek() + "  |  B: close";
        }

        private string DescribeConnections()
        {
            return _connections == 0 ? "direct URL" : _connections + " connection" + (_connections == 1 ? string.Empty : "s");
        }

        private void LogAsync(string level, string text)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Log(level, text));
        }

        private void Log(string level, string text)
        {
            FileLog.Write(level, text);
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + level.ToUpperInvariant() + " " + text;

            _log.Add(line.Length > 600 ? line.Substring(0, 600) + "..." : line);
            if (_log.Count > 500)
            {
                _log.RemoveRange(0, _log.Count - 500);
            }

            if (DebugText != null)
            {
                DebugText.Text = string.Join("\n", _log.Skip(Math.Max(0, _log.Count - VisibleLogLines)));
            }
        }
    }
}
