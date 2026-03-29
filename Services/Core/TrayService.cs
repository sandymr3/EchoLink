#if WINDOWS
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using EchoLink.Views;

namespace EchoLink.Services;

/// <summary>
/// System tray management service for Windows.
/// Provides minimize-to-tray functionality with context menu.
/// </summary>
public class TrayService : IDisposable
{
    private readonly LoggingService _log = LoggingService.Instance;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _isDisposed;

    /// <summary>
    /// Initialize the tray icon with the main window.
    /// Call this once when the main window is created.
    /// </summary>
    public void Initialize(MainWindow window)
    {
        if (_trayIcon != null)
        {
            _log.Warning("[TrayService] Already initialized");
            return;
        }

        _mainWindow = window;

        // Create tray icon
        _trayIcon = new TaskbarIcon
        {
            Icon = new Avalonia.Media.Imaging.Icon(
                AssetLoader.Open(new Uri("avares://EchoLink/Assets/avalonia-logo.ico"))),
            ToolTipText = "EchoLink - Mesh Network Active"
        };

        // Left-click: Restore window
        _trayIcon.TrayLeftMouseDown += (s, e) =>
        {
            _log.Debug("[TrayService] Left-click detected");
            RestoreWindow();
        };

        // Right-click: Show context menu
        _trayIcon.TrayRightMouseDown += (s, e) =>
        {
            _log.Debug("[TrayService] Right-click detected");
            ShowContextMenu();
        };

        // Double-click: Restore window
        _trayIcon.TrayMouseDoubleClick += (s, e) =>
        {
            _log.Debug("[TrayService] Double-click detected");
            RestoreWindow();
        };

        _log.Info("[TrayService] Initialized successfully");
    }

    /// <summary>
    /// Show the tray context menu.
    /// </summary>
    public void ShowContextMenu()
    {
        if (_trayIcon == null)
        {
            _log.Warning("[TrayService] Cannot show menu - not initialized");
            return;
        }

        var menu = new NativeMenu();
        
        var openItem = new NativeMenuItem("Open EchoLink");
        openItem.Click += (s, e) => RestoreWindow();
        menu.Add(openItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (s, e) => ExitApplication();
        menu.Add(exitItem);

        _trayIcon.Menu = menu;
        _trayIcon.ShowMenu();
    }

    /// <summary>
    /// Restore the main window from tray.
    /// </summary>
    public void RestoreWindow()
    {
        if (_mainWindow == null)
        {
            _log.Warning("[TrayService] Cannot restore - no window reference");
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.Focus();

            // Restore normal shutdown mode so close button works properly
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
        });

        _log.Info("[TrayService] Window restored");
    }

    /// <summary>
    /// Hide the main window to tray.
    /// Must be called from UI thread.
    /// </summary>
    public void HideToTray()
    {
        if (_mainWindow == null)
        {
            _log.Warning("[TrayService] Cannot hide - no window reference");
            return;
        }

        // Hide window immediately (must be on UI thread)
        _mainWindow.Hide();
        _log.Info("[TrayService] Window hidden to tray");
    }

    /// <summary>
    /// Exit the application completely.
    /// Called from tray menu "Exit" option.
    /// </summary>
    private void ExitApplication()
    {
        _log.Info("[TrayService] Exit requested from tray menu");

        // Shutdown the application
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;

        if (_trayIcon != null)
        {
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _log.Info("[TrayService] Disposed");
    }
}
#else
namespace EchoLink.Services;

/// <summary>
/// Stub TrayService for non-Windows platforms.
/// </summary>
public class TrayService
{
    public void Initialize(object window) { }
    public void ShowContextMenu() { }
    public void RestoreWindow() { }
    public void HideToTray() { }
    public void Dispose() { }
}
#endif
