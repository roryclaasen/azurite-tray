// Copyright (c) Rory Claasen. All rights reserved.

using System;
using System.Threading;
using System.Windows.Forms;

namespace AzuriteTray.App;

internal static class Program
{
    private const string MutexName = @"Local\RoryClaasen.AzuriteTray";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "Azurite Tray is already running.",
                "Azurite Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(new AzuriteProcessManager()));
        GC.KeepAlive(mutex);
    }
}
