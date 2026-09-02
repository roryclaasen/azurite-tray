// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzuriteTray.App.ProcessManagement;
using AzuriteTray.Core;
using H.NotifyIcon.Core;

internal sealed class TrayApplication : IDisposable
{
    private readonly IReadOnlyDictionary<AzuriteSource, AzuriteProcessManager> processManagers;
    private readonly AzuriteSourcePreference sourcePreference;
    private readonly TrayIconWithContextMenu trayIcon;
    private readonly PopupMenuItem startItem;
    private readonly PopupMenuItem stopItem;
    private readonly PopupMenuItem npmSourceItem;
    private readonly PopupMenuItem visualStudioSourceItem;
    private readonly Icon icon;
    private readonly Timer statusTimer;
    private readonly EventWaitHandle activationEvent;
    private readonly ManualResetEventSlim exitSignal = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly CancellationTokenSource shutdownTokenSource = new();

    private RegisteredWaitHandle? activationRegistration;
    private AzuriteSource selectedSource;
    private bool statusErrorReported;
    private int disposed;

    public TrayApplication(IEnumerable<AzuriteProcessManager> processManagers, AzuriteSourcePreference sourcePreference, EventWaitHandle activationEvent)
    {
        this.processManagers = processManagers.ToDictionary(manager => manager.Source);
        this.sourcePreference = sourcePreference;
        this.activationEvent = activationEvent;
        this.selectedSource = this.SelectInitialSource(sourcePreference.Load());

        using Stream iconStream = H.Resources.icon_ico.AsStream();
        using var sourceIcon = new Icon(iconStream);
        this.icon = (Icon)sourceIcon.Clone();

        this.startItem = new PopupMenuItem("Start Azurite", (_, _) => _ = this.StartAsync());
        this.stopItem = new PopupMenuItem("Stop Azurite", (_, _) => _ = this.StopAsync());
        this.npmSourceItem = new PopupMenuItem("npm", (_, _) => _ = this.ChangeSourceAsync(AzuriteSource.Npm));
        this.visualStudioSourceItem = new PopupMenuItem("Visual Studio", (_, _) => _ = this.ChangeSourceAsync(AzuriteSource.VisualStudio));

        this.trayIcon = new TrayIconWithContextMenu("AzuriteTray")
        {
            Icon = this.icon.Handle,
            ToolTip = "Azurite - Checking status",
            ContextMenu = new PopupMenu
            {
                Items =
                {
                    this.startItem,
                    this.stopItem,
                    new PopupMenuSeparator(),
                    new PopupSubMenu("Azurite source")
                    {
                        Items =
                        {
                            this.npmSourceItem,
                            this.visualStudioSourceItem
                        }
                    },
                    new PopupMenuSeparator(),
                    new PopupMenuItem("Exit", (_, _) => _ = this.ExitAsync())
                }
            }
        };

        this.statusTimer = new Timer(
            static state => _ = ((TrayApplication)state!).RefreshStatusAsync(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public void Run()
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        this.trayIcon.Create();
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
        this.statusTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
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
        this.trayIcon.Dispose();
        this.icon.Dispose();
        this.shutdownTokenSource.Dispose();
        this.operationLock.Dispose();
        this.exitSignal.Dispose();
    }

    private AzuriteProcessManager SelectedProcessManager => this.processManagers[this.selectedSource];

    private async Task StartAsync()
    {
        await this.operationLock.WaitAsync().ConfigureAwait(false);
        this.SetOperationInProgress(true);

        try
        {
            if (await Task.Run(this.SelectedProcessManager.Start))
            {
                this.ShowNotification("Azurite has started.", NotificationIcon.Info);
            }
        }
        catch (Exception exception)
        {
            this.ShowError("Azurite could not be started.", exception);
        }
        finally
        {
            this.SetOperationInProgress(false);
            this.operationLock.Release();
            await this.RefreshStatusAsync();
        }
    }

    private async Task StopAsync() => await this.StopAzuriteAsync(showNotification: true);

    private async Task ExitAsync()
    {
        if (await this.StopAzuriteAsync(showNotification: false))
        {
            this.statusTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            this.exitSignal.Set();
        }
    }

    private async Task<bool> StopAzuriteAsync(bool showNotification)
    {
        await this.operationLock.WaitAsync().ConfigureAwait(false);
        this.SetOperationInProgress(true);

        try
        {
            bool stopped = await this.SelectedProcessManager.StopAsync(this.shutdownTokenSource.Token);

            if (stopped && showNotification)
            {
                this.ShowNotification("Azurite has stopped.", NotificationIcon.Info);
            }

            return true;
        }
        catch (Exception exception)
        {
            this.ShowError("Azurite could not be stopped.", exception);
            return false;
        }
        finally
        {
            this.SetOperationInProgress(false);
            this.operationLock.Release();

            if (showNotification)
            {
                await this.RefreshStatusAsync();
            }
        }
    }

    private async Task ChangeSourceAsync(AzuriteSource source)
    {
        await this.operationLock.WaitAsync().ConfigureAwait(false);
        AzuriteSource previousSource = this.selectedSource;
        AzuriteProcessManager currentManager = this.SelectedProcessManager;
        AzuriteProcessManager newManager = this.processManagers[source];
        bool restart = false;
        bool newManagerStarted = false;
        bool preferenceWriteAttempted = false;

        try
        {
            if (source == this.selectedSource)
            {
                return;
            }

            if (!newManager.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"{newManager.DisplayName} Azurite is not installed.");
            }

            this.SetOperationInProgress(true);
            restart = await Task.Run(currentManager.IsRunning);

            if (restart)
            {
                await currentManager.StopAsync(this.shutdownTokenSource.Token);
                newManagerStarted = await Task.Run(newManager.Start);
            }

            preferenceWriteAttempted = true;
            this.sourcePreference.Save(source);
            this.selectedSource = source;

            this.ShowNotification(
                $"Using {newManager.DisplayName} Azurite.",
                NotificationIcon.Info);
        }
        catch (Exception exception)
        {
            Exception failure = await this.RollbackSourceChangeAsync(
                exception,
                previousSource,
                currentManager,
                newManager,
                restart,
                newManagerStarted,
                preferenceWriteAttempted);
            this.ShowError("The Azurite source could not be changed.", failure);
        }
        finally
        {
            this.SetOperationInProgress(false);
            this.operationLock.Release();
            await this.RefreshStatusAsync();
        }
    }

    private async Task<Exception> RollbackSourceChangeAsync(
        Exception switchException,
        AzuriteSource previousSource,
        AzuriteProcessManager previousManager,
        AzuriteProcessManager newManager,
        bool restart,
        bool newManagerStarted,
        bool preferenceWriteAttempted)
    {
        var failures = new List<Exception> { switchException };
        this.selectedSource = previousSource;

        if (newManagerStarted)
        {
            try
            {
                await newManager.StopAsync(this.shutdownTokenSource.Token);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (restart)
        {
            try
            {
                await Task.Run(previousManager.Start);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (preferenceWriteAttempted)
        {
            try
            {
                this.sourcePreference.Save(previousSource);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures.Count == 1
            ? switchException
            : new AggregateException("The source change and its rollback both failed.", failures);
    }

    private async Task RefreshStatusAsync()
    {
        if (this.disposed != 0 || !await this.operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            AzuriteProcessManager manager = this.SelectedProcessManager;
            bool running = await Task.Run(manager.IsRunning);
            this.startItem.Enabled = !running && manager.IsAvailable;
            this.stopItem.Enabled = running;
            this.UpdateSourceMenu();
            this.trayIcon.UpdateToolTip(
                $"Azurite ({manager.DisplayName}) - {(running ? "Running" : "Stopped")}");
            this.statusErrorReported = false;
        }
        catch (Exception exception)
        {
            this.startItem.Enabled = false;
            this.stopItem.Enabled = false;
            this.trayIcon.UpdateToolTip("Azurite - Status unavailable");

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

    private AzuriteSource SelectInitialSource(AzuriteSource preferredSource)
    {
        if (this.processManagers[preferredSource].IsAvailable)
        {
            return preferredSource;
        }

        return this.processManagers.Values.FirstOrDefault(manager => manager.IsAvailable)?.Source
            ?? preferredSource;
    }

    private void UpdateSourceMenu()
    {
        this.npmSourceItem.Checked = this.selectedSource == AzuriteSource.Npm;
        this.visualStudioSourceItem.Checked = this.selectedSource == AzuriteSource.VisualStudio;
        this.npmSourceItem.Enabled = this.processManagers[AzuriteSource.Npm].IsAvailable;
        this.visualStudioSourceItem.Enabled = this.processManagers[AzuriteSource.VisualStudio].IsAvailable;
    }

    private void SetOperationInProgress(bool value)
    {
        this.startItem.Enabled = !value;
        this.stopItem.Enabled = !value;
        this.npmSourceItem.Enabled = !value;
        this.visualStudioSourceItem.Enabled = !value;
    }

    private void ShowNotification(string message, NotificationIcon notificationIcon)
    {
        if (this.disposed == 0)
        {
            this.trayIcon.ShowNotification("Azurite", message, notificationIcon);
        }
    }

    private void NotifyAlreadyRunning()
    {
        if (this.disposed != 0 || !this.trayIcon.IsCreated)
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

    private void ShowError(string message, Exception exception) =>
        this.ShowNotification($"{message} {exception.Message}", NotificationIcon.Error);
}
