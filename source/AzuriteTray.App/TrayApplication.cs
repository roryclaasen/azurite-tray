// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using H.NotifyIcon.Core;

internal sealed class TrayApplication : IDisposable
{
    private readonly AzuriteProcessManager processManager;
    private readonly TrayIconWithContextMenu trayIcon;
    private readonly PopupMenuItem startItem;
    private readonly PopupMenuItem stopItem;
    private readonly Icon icon;
    private readonly Timer statusTimer;
    private readonly EventWaitHandle activationEvent;
    private readonly ManualResetEventSlim exitSignal = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly CancellationTokenSource shutdownTokenSource = new();

    private RegisteredWaitHandle? activationRegistration;
    private bool statusErrorReported;
    private int disposed;

    public TrayApplication(AzuriteProcessManager processManager, EventWaitHandle activationEvent)
    {
        this.processManager = processManager;
        this.activationEvent = activationEvent;

        using Stream iconStream = H.Resources.icon_ico.AsStream();
        using var sourceIcon = new Icon(iconStream);
        icon = (Icon)sourceIcon.Clone();

        startItem = new PopupMenuItem("Start Azurite", (_, _) => _ = StartAsync());
        stopItem = new PopupMenuItem("Stop Azurite", (_, _) => _ = StopAsync());

        trayIcon = new TrayIconWithContextMenu("AzuriteTray")
        {
            Icon = icon.Handle,
            ToolTip = "Azurite - Checking status",
            ContextMenu = new PopupMenu
            {
                Items =
                {
                    startItem,
                    stopItem,
                    new PopupMenuSeparator(),
                    new PopupMenuItem("Exit", (_, _) => _ = ExitAsync())
                }
            }
        };

        statusTimer = new Timer(static state => _ = ((TrayApplication)state!).RefreshStatusAsync(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Run()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);

        trayIcon.Create();
        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut)
                {
                    ((TrayApplication)state!).NotifyAlreadyRunning();
                }
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
        statusTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        exitSignal.Wait();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        shutdownTokenSource.Cancel();
        statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        activationRegistration?.Unregister(null);
        statusTimer.Dispose();
        trayIcon.Dispose();
        icon.Dispose();
        shutdownTokenSource.Dispose();
        operationLock.Dispose();
        exitSignal.Dispose();
    }

    private async Task StartAsync()
    {
        await operationLock.WaitAsync().ConfigureAwait(false);

        SetOperationInProgress(true);

        try
        {
            if (await Task.Run(processManager.Start))
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
            operationLock.Release();
            await RefreshStatusAsync();
        }
    }

    private async Task StopAsync() => await StopAzuriteAsync(showNotification: true);

    private async Task ExitAsync()
    {
        if (await StopAzuriteAsync(showNotification: false))
        {
            statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            exitSignal.Set();
        }
    }

    private async Task<bool> StopAzuriteAsync(bool showNotification)
    {
        await operationLock.WaitAsync().ConfigureAwait(false);

        SetOperationInProgress(true);

        try
        {
            bool stopped = await processManager.StopAsync(shutdownTokenSource.Token);

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
            operationLock.Release();

            if (showNotification)
            {
                await RefreshStatusAsync();
            }
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (disposed != 0 || !await operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            bool running = await Task.Run(processManager.IsRunning);
            startItem.Enabled = !running;
            stopItem.Enabled = running;
            trayIcon.UpdateToolTip(running ? "Azurite - Running" : "Azurite - Stopped");
            statusErrorReported = false;
        }
        catch (Exception exception)
        {
            startItem.Enabled = false;
            stopItem.Enabled = false;
            trayIcon.UpdateToolTip("Azurite - Status unavailable");

            if (!statusErrorReported)
            {
                statusErrorReported = true;
                ShowError("Azurite status could not be determined.", exception);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    private void SetOperationInProgress(bool value)
    {
        startItem.Enabled = !value;
        stopItem.Enabled = !value;
    }

    private void ShowNotification(string message, NotificationIcon icon)
    {
        if (disposed == 0)
        {
            trayIcon.ShowNotification("Azurite", message, icon);
        }
    }

    private void NotifyAlreadyRunning()
    {
        if (disposed != 0 || !trayIcon.IsCreated)
        {
            return;
        }

        try
        {
            ShowNotification("Azurite Tray is already running.", NotificationIcon.Info);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine(exception);
        }
    }

    private void ShowError(string message, Exception exception) => ShowNotification($"{message} {exception.Message}", NotificationIcon.Error);
}
