// Copyright (c) Rory Claasen. All rights reserved.

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AzuriteTray.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AzuriteProcessManager _processManager;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly Icon? _customIcon;

    private bool _operationInProgress;
    private bool _statusCheckInProgress;
    private bool _statusErrorReported;
    private bool _disposed;

    public TrayApplicationContext(AzuriteProcessManager processManager)
    {
        _processManager = processManager;

        _startItem = new ToolStripMenuItem("Start Azurite");
        _stopItem = new ToolStripMenuItem("Stop Azurite");
        var exitItem = new ToolStripMenuItem("Exit");

        _startItem.Click += StartItem_Click;
        _stopItem.Click += StopItem_Click;
        exitItem.Click += ExitItem_Click;

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_startItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        if (processManager.IconPath is not null)
        {
            _customIcon = new Icon(processManager.IconPath);
        }

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _customIcon ?? SystemIcons.Application,
            Text = "Azurite - Checking status",
            Visible = true
        };

        _statusTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000,
            Enabled = true
        };
        _statusTimer.Tick += StatusTimer_Tick;
    }

    protected override void ExitThreadCore()
    {
        DisposeResources();
        base.ExitThreadCore();
    }

    private async void StartItem_Click(object? sender, EventArgs e)
    {
        if (_operationInProgress)
        {
            return;
        }

        SetOperationInProgress(true);

        try
        {
            bool started = await Task.Run(_processManager.Start);

            if (started)
            {
                ShowNotification("Azurite has started.");
            }
        }
        catch (Exception exception)
        {
            ShowError("Azurite could not be started.", exception);
        }
        finally
        {
            SetOperationInProgress(false);
            await RefreshStatusAsync();
        }
    }

    private async void StopItem_Click(object? sender, EventArgs e)
    {
        await StopAzuriteAsync();
    }

    private async void ExitItem_Click(object? sender, EventArgs e)
    {
        if (_operationInProgress)
        {
            return;
        }

        if (await StopAzuriteAsync())
        {
            ExitThread();
        }
    }

    private async void StatusTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async Task<bool> StopAzuriteAsync()
    {
        if (_operationInProgress)
        {
            return false;
        }

        SetOperationInProgress(true);

        try
        {
            bool stopped = await _processManager.StopAsync(CancellationToken.None);

            if (stopped)
            {
                ShowNotification("Azurite has stopped.");
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
            await RefreshStatusAsync();
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_operationInProgress || _statusCheckInProgress)
        {
            return;
        }

        _statusCheckInProgress = true;

        try
        {
            bool running = await Task.Run(_processManager.IsRunning);
            _startItem.Enabled = !running;
            _stopItem.Enabled = running;
            _notifyIcon.Text = running ? "Azurite - Running" : "Azurite - Stopped";
            _statusErrorReported = false;
        }
        catch (Exception exception)
        {
            _startItem.Enabled = false;
            _stopItem.Enabled = false;
            _notifyIcon.Text = "Azurite - Status unavailable";

            if (!_statusErrorReported)
            {
                _statusErrorReported = true;
                ShowError("Azurite status could not be determined.", exception);
            }
        }
        finally
        {
            _statusCheckInProgress = false;
        }
    }

    private void SetOperationInProgress(bool value)
    {
        _operationInProgress = value;
        _startItem.Enabled = !value;
        _stopItem.Enabled = !value;
    }

    private void ShowNotification(string message)
    {
        _notifyIcon.ShowBalloonTip(
            timeout: 2000,
            tipTitle: "Azurite",
            tipText: message,
            tipIcon: ToolTipIcon.Info);
    }

    private static void ShowError(string message, Exception exception)
    {
        MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
            "Azurite Tray",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void DisposeResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _customIcon?.Dispose();
    }
}
