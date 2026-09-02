// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using H.NotifyIcon.Core;

internal sealed class TrayApplication : IDisposable
{
    private readonly AzuriteProcessManager _processManager;
    private readonly TrayIconWithContextMenu _trayIcon;
    private readonly PopupMenuItem _startItem;
    private readonly PopupMenuItem _stopItem;
    private readonly Icon _icon;
    private readonly System.Threading.Timer _statusTimer;
    private readonly ManualResetEventSlim _exitSignal = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownTokenSource = new();
    private readonly RegisteredWaitHandle _activationRegistration;

    private bool _statusErrorReported;
    private int _disposed;

    public TrayApplication(
        AzuriteProcessManager processManager,
        EventWaitHandle activationEvent)
    {
        _processManager = processManager;
        _icon = processManager.IconPath is null
            ? (Icon)SystemIcons.Application.Clone()
            : new Icon(processManager.IconPath);

        _startItem = new PopupMenuItem("Start Azurite", (_, _) => _ = StartAsync());
        _stopItem = new PopupMenuItem("Stop Azurite", (_, _) => _ = StopAsync());

        _trayIcon = new TrayIconWithContextMenu("AzuriteTray")
        {
            Icon = _icon.Handle,
            ToolTip = "Azurite - Checking status",
            ContextMenu = new PopupMenu
            {
                Items =
                {
                    _startItem,
                    _stopItem,
                    new PopupMenuSeparator(),
                    new PopupMenuItem("Exit", (_, _) => _ = ExitAsync())
                }
            }
        };

        _statusTimer = new System.Threading.Timer(
            static state => _ = ((TrayApplication)state!).RefreshStatusAsync(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut)
                {
                    ((TrayApplication)state!).ShowNotification(
                        "Azurite Tray is already running.",
                        NotificationIcon.Info);
                }
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Run()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _trayIcon.Create();
        _statusTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        _exitSignal.Wait();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdownTokenSource.Cancel();
        _statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activationRegistration.Unregister(null);
        _statusTimer.Dispose();
        _trayIcon.Dispose();
        _icon.Dispose();
        _shutdownTokenSource.Dispose();
        _operationLock.Dispose();
        _exitSignal.Dispose();
    }

    private async Task StartAsync()
    {
        if (!await _operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        SetOperationInProgress(true);

        try
        {
            bool started = await Task.Run(_processManager.Start);

            if (started)
            {
                ShowNotification("Azurite has started.", NotificationIcon.Info);
            }
        }
        catch (Exception exception)
        {
            ShowError("Azurite could not be started.", exception);
        }
        finally
        {
            SetOperationInProgress(false);
            _operationLock.Release();
            await RefreshStatusAsync();
        }
    }

    private async Task StopAsync()
    {
        _ = await StopAzuriteAsync(showNotification: true);
    }

    private async Task ExitAsync()
    {
        if (await StopAzuriteAsync(showNotification: false))
        {
            _statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _exitSignal.Set();
        }
    }

    private async Task<bool> StopAzuriteAsync(bool showNotification)
    {
        if (!await _operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return false;
        }

        SetOperationInProgress(true);

        try
        {
            bool stopped = await _processManager.StopAsync(_shutdownTokenSource.Token);

            if (stopped && showNotification)
            {
                ShowNotification("Azurite has stopped.", NotificationIcon.Info);
            }

            return true;
        }
        catch (Exception exception)
        {
            ShowError("Azurite could not be stopped.", exception);
            return false;
        }
        finally
        {
            SetOperationInProgress(false);
            _operationLock.Release();

            if (showNotification)
            {
                await RefreshStatusAsync();
            }
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_disposed != 0 ||
            !await _operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            bool running = await Task.Run(_processManager.IsRunning);
            _startItem.Enabled = !running;
            _stopItem.Enabled = running;
            _trayIcon.ToolTip = running ? "Azurite - Running" : "Azurite - Stopped";
            _statusErrorReported = false;
        }
        catch (Exception exception)
        {
            _startItem.Enabled = false;
            _stopItem.Enabled = false;
            _trayIcon.ToolTip = "Azurite - Status unavailable";

            if (!_statusErrorReported)
            {
                _statusErrorReported = true;
                ShowError("Azurite status could not be determined.", exception);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void SetOperationInProgress(bool value)
    {
        _startItem.Enabled = !value;
        _stopItem.Enabled = !value;
    }

    private void ShowNotification(string message, NotificationIcon icon)
    {
        if (_disposed == 0)
        {
            _trayIcon.ShowNotification("Azurite", message, icon);
        }
    }

    private void ShowError(string message, Exception exception)
    {
        ShowNotification($"{message} {exception.Message}", NotificationIcon.Error);
    }
}
