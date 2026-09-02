// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.Collections.Generic;
using System.Drawing;
using AzuriteTray.Core;
using AzuriteTray.Core.Abstractions;
using H.NotifyIcon.Core;

internal sealed class AzuriteTrayView : IDisposable
{
    private readonly IReadOnlyDictionary<AzuriteSource, IAzuriteProcessManager> processManagers;
    private readonly IReadOnlyDictionary<AzuriteSource, PopupMenuItem> sourceItems;
    private readonly TrayIconWithContextMenu trayIcon;
    private readonly PopupMenuItem startItem;
    private readonly PopupMenuItem stopItem;
    private readonly Icon icon;

    public AzuriteTrayView(IReadOnlyDictionary<AzuriteSource, IAzuriteProcessManager> processManagers, Action start, Action stop, Action<AzuriteSource> changeSource, Action exit)
    {
        this.processManagers = processManagers;
        this.startItem = new PopupMenuItem("Start Azurite", (_, _) => start.Invoke());
        this.stopItem = new PopupMenuItem("Stop Azurite", (_, _) => stop.Invoke());

        var sourceMenu = new PopupSubMenu("Azurite source");
        var sourceItems = new Dictionary<AzuriteSource, PopupMenuItem>();
        foreach (IAzuriteProcessManager manager in processManagers.Values)
        {
            var sourceItem = new PopupMenuItem(manager.DisplayName, (_, _) => changeSource.Invoke(manager.Source));
            sourceItems.Add(manager.Source, sourceItem);
            sourceMenu.Items.Add(sourceItem);
        }

        this.sourceItems = sourceItems;

        using var iconStream = H.Resources.icon_ico.AsStream();
        using var sourceIcon = new Icon(iconStream);
        this.icon = (Icon)sourceIcon.Clone();

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
                    sourceMenu,
                    new PopupMenuSeparator(),
                    new PopupMenuItem("Exit", (_, _) => exit())
                }
            }
        };
    }

    public bool IsCreated => this.trayIcon.IsCreated;

    public void Create() => this.trayIcon.Create();

    public void SetOperationInProgress(bool value)
    {
        this.startItem.Enabled = !value;
        this.stopItem.Enabled = !value;

        foreach ((AzuriteSource source, PopupMenuItem item) in this.sourceItems)
        {
            item.Enabled = !value && this.processManagers[source].IsAvailable;
        }
    }

    public void UpdateStatus(AzuriteSource selectedSource, IAzuriteProcessManager manager, bool running)
    {
        this.startItem.Enabled = !running && manager.IsAvailable;
        this.stopItem.Enabled = running;

        foreach ((AzuriteSource source, PopupMenuItem item) in this.sourceItems)
        {
            item.Checked = selectedSource == source;
            item.Enabled = this.processManagers[source].IsAvailable;
        }

        this.trayIcon.UpdateToolTip($"Azurite ({manager.DisplayName}) - {(running ? "Running" : "Stopped")}");
    }

    public void UpdateStatusUnavailable()
    {
        this.startItem.Enabled = false;
        this.stopItem.Enabled = false;
        this.trayIcon.UpdateToolTip("Azurite - Status unavailable");
    }

    public void ShowNotification(string message, NotificationIcon notificationIcon)
    {
        this.trayIcon.ShowNotification("Azurite", message, notificationIcon);
    }

    public void Dispose()
    {
        this.trayIcon.Dispose();
        this.icon.Dispose();
    }
}
