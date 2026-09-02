// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AzuriteTray.App.ProcessManagement;
using AzuriteTray.Core;
using H.NotifyIcon.Core;

internal sealed class TrayApplication : IDisposable
{
    private readonly AzuriteSourceController sourceController;
    private readonly AzuriteTrayView trayView;
    private readonly Timer statusTimer;
    private readonly EventWaitHandle activationEvent;
    private readonly ManualResetEventSlim exitSignal = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly CancellationTokenSource shutdownTokenSource = new();

    private RegisteredWaitHandle? activationRegistration;
    private bool statusErrorReported;
    private int disposed;

    public TrayApplication(IEnumerable<AzuriteProcessManager> processManagers, AzuriteSourcePreference sourcePreference, EventWaitHandle activationEvent)
    {
        this.activationEvent = activationEvent;
        this.sourceController = new AzuriteSourceController(processManagers, sourcePreference);
        this.trayView = new AzuriteTrayView(
            this.sourceController.ProcessManagers,
            () => _ = this.StartAsync(),
            () => _ = this.StopAsync(),
            source => _ = this.ChangeSourceAsync(source),
            () => _ = this.ExitAsync());

        this.statusTimer = new Timer(static state => _ = ((TrayApplication)state!).RefreshStatusAsync(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Run()
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        this.trayView.Create();
        this.activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            this.activationEvent,
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

        this.statusTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));
        this.exitSignal.Wait();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        this.shutdownTokenSource.Cancel();
        this.statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        this.activationRegistration?.Unregister(null);
        this.statusTimer.Dispose();
        this.trayView.Dispose();
        this.shutdownTokenSource.Dispose();
        this.operationLock.Dispose();
        this.exitSignal.Dispose();
    }

    private async Task StartAsync() => await this.ExecuteOperationAsync(
        async () =>
        {
            if (await Task.Run(this.sourceController.SelectedProcessManager.Start))
            {
                this.ShowNotification("Azurite has started.", NotificationIcon.Info);
            }
        },
        "Azurite could not be started.");

    private async Task StopAsync() => await this.ExecuteOperationAsync(() => this.StopAzuriteAsync(showNotification: true), "Azurite could not be stopped.");

    private async Task ExitAsync()
    {
        if (await this.ExecuteOperationAsync(() => this.StopAzuriteAsync(showNotification: false), "Azurite could not be stopped.", refreshStatus: false))
        {
            this.statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            this.exitSignal.Set();
        }
    }

    private async Task StopAzuriteAsync(bool showNotification)
    {
        var stopped = await this.sourceController.SelectedProcessManager.StopAsync(this.shutdownTokenSource.Token);
        if (stopped && showNotification)
        {
            this.ShowNotification("Azurite has stopped.", NotificationIcon.Info);
        }
    }

    private async Task ChangeSourceAsync(AzuriteSource source) => await this.ExecuteOperationAsync(
        async () =>
        {
            var manager = await this.sourceController.ChangeSourceAsync(source, this.shutdownTokenSource.Token);
            if (manager is not null)
            {
                this.ShowNotification($"Using {manager.DisplayName} Azurite.", NotificationIcon.Info);
            }
        },
        "The Azurite source could not be changed.");

    private async Task<bool> ExecuteOperationAsync(Func<Task> operation, string errorMessage, bool refreshStatus = true)
    {
        await this.operationLock.WaitAsync().ConfigureAwait(false);
        this.trayView.SetOperationInProgress(true);

        try
        {
            await operation.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            this.ShowError(errorMessage, exception);
            return false;
        }
        finally
        {
            this.trayView.SetOperationInProgress(false);
            this.operationLock.Release();

            if (refreshStatus)
            {
                await this.RefreshStatusAsync();
            }
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (this.disposed != 0 || !await this.operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var manager = this.sourceController.SelectedProcessManager;
            var running = await Task.Run(manager.IsRunning);
            this.trayView.UpdateStatus(this.sourceController.SelectedSource, manager, running);
            this.statusErrorReported = false;
        }
        catch (Exception exception)
        {
            this.trayView.UpdateStatusUnavailable();

            if (!this.statusErrorReported)
            {
                this.statusErrorReported = true;
                this.ShowError("Azurite status could not be determined.", exception);
            }
        }
        finally
        {
            this.operationLock.Release();
        }
    }

    private void ShowNotification(string message, NotificationIcon notificationIcon)
    {
        if (this.disposed == 0)
        {
            this.trayView.ShowNotification(message, notificationIcon);
        }
    }

    private void NotifyAlreadyRunning()
    {
        if (this.disposed != 0 || !this.trayView.IsCreated)
        {
            return;
        }

        try
        {
            this.ShowNotification("Azurite Tray is already running.", NotificationIcon.Info);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine(exception);
        }
    }

    private void ShowError(string message, Exception exception) => this.ShowNotification($"{message} {exception.Message}", NotificationIcon.Error);
}
