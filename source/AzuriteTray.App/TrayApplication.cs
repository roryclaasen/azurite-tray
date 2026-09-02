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

    public TrayApplication(
        IEnumerable<AzuriteProcessManager> processManagers,
        AzuriteSourcePreference sourcePreference,
        EventWaitHandle activationEvent)
    {
        this.processManagers = processManagers.ToDictionary(manager => manager.Source);
        this.sourcePreference = sourcePreference;
        this.activationEvent = activationEvent;
        selectedSource = SelectInitialSource(sourcePreference.Load());

        using Stream iconStream = H.Resources.icon_ico.AsStream();
        using var sourceIcon = new Icon(iconStream);
        icon = (Icon)sourceIcon.Clone();

        startItem = new PopupMenuItem("Start Azurite", (_, _) => _ = StartAsync());
        stopItem = new PopupMenuItem("Stop Azurite", (_, _) => _ = StopAsync());
        npmSourceItem = new PopupMenuItem(
            "npm",
            (_, _) => _ = ChangeSourceAsync(AzuriteSource.Npm));
        visualStudioSourceItem = new PopupMenuItem(
            "Visual Studio",
            (_, _) => _ = ChangeSourceAsync(AzuriteSource.VisualStudio));

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
                    new PopupSubMenu("Azurite source")
                    {
                        Items =
                        {
                            npmSourceItem,
                            visualStudioSourceItem
                        }
                    },
                    new PopupMenuSeparator(),
                    new PopupMenuItem("Exit", (_, _) => _ = ExitAsync())
                }
            }
        };

        statusTimer = new Timer(
            static state => _ = ((TrayApplication)state!).RefreshStatusAsync(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
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

    private AzuriteProcessManager SelectedProcessManager => processManagers[selectedSource];

    private async Task StartAsync()
    {
        await operationLock.WaitAsync().ConfigureAwait(false);
        SetOperationInProgress(true);

        try
        {
            if (await Task.Run(SelectedProcessManager.Start))
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
            bool stopped = await SelectedProcessManager.StopAsync(shutdownTokenSource.Token);

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

    private async Task ChangeSourceAsync(AzuriteSource source)
    {
        await operationLock.WaitAsync().ConfigureAwait(false);
        AzuriteSource previousSource = selectedSource;
        AzuriteProcessManager currentManager = SelectedProcessManager;
        AzuriteProcessManager newManager = processManagers[source];
        bool restart = false;
        bool newManagerStarted = false;
        bool preferenceWriteAttempted = false;

        try
        {
            if (source == selectedSource)
            {
                return;
            }

            if (!newManager.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"{newManager.DisplayName} Azurite is not installed.");
            }

            SetOperationInProgress(true);
            restart = await Task.Run(currentManager.IsRunning);

            if (restart)
            {
                await currentManager.StopAsync(shutdownTokenSource.Token);
                newManagerStarted = await Task.Run(newManager.Start);
            }

            preferenceWriteAttempted = true;
            sourcePreference.Save(source);
            selectedSource = source;

            ShowNotification(
                $"Using {newManager.DisplayName} Azurite.",
                NotificationIcon.Info);
        }
        catch (Exception exception)
        {
            Exception failure = await RollbackSourceChangeAsync(
                exception,
                previousSource,
                currentManager,
                newManager,
                restart,
                newManagerStarted,
                preferenceWriteAttempted);
            ShowError("The Azurite source could not be changed.", failure);
        }
        finally
        {
            SetOperationInProgress(false);
            operationLock.Release();
            await RefreshStatusAsync();
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
        selectedSource = previousSource;

        if (newManagerStarted)
        {
            try
            {
                await newManager.StopAsync(shutdownTokenSource.Token);
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
                sourcePreference.Save(previousSource);
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
        if (disposed != 0 || !await operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            AzuriteProcessManager manager = SelectedProcessManager;
            bool running = await Task.Run(manager.IsRunning);
            startItem.Enabled = !running && manager.IsAvailable;
            stopItem.Enabled = running;
            UpdateSourceMenu();
            trayIcon.UpdateToolTip(
                $"Azurite ({manager.DisplayName}) - {(running ? "Running" : "Stopped")}");
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

    private AzuriteSource SelectInitialSource(AzuriteSource preferredSource)
    {
        if (processManagers[preferredSource].IsAvailable)
        {
            return preferredSource;
        }

        return processManagers.Values.FirstOrDefault(manager => manager.IsAvailable)?.Source
            ?? preferredSource;
    }

    private void UpdateSourceMenu()
    {
        npmSourceItem.Checked = selectedSource == AzuriteSource.Npm;
        visualStudioSourceItem.Checked = selectedSource == AzuriteSource.VisualStudio;
        npmSourceItem.Enabled = processManagers[AzuriteSource.Npm].IsAvailable;
        visualStudioSourceItem.Enabled = processManagers[AzuriteSource.VisualStudio].IsAvailable;
    }

    private void SetOperationInProgress(bool value)
    {
        startItem.Enabled = !value;
        stopItem.Enabled = !value;
        npmSourceItem.Enabled = !value;
        visualStudioSourceItem.Enabled = !value;
    }

    private void ShowNotification(string message, NotificationIcon notificationIcon)
    {
        if (disposed == 0)
        {
            trayIcon.ShowNotification("Azurite", message, notificationIcon);
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

    private void ShowError(string message, Exception exception) =>
        ShowNotification($"{message} {exception.Message}", NotificationIcon.Error);
}
