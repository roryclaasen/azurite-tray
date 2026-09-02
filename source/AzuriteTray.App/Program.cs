// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.Threading;

internal static class Program
{
    private const string MutexName = @"Local\RoryClaasen.AzuriteTray";
    private const string ActivationEventName = @"Local\RoryClaasen.AzuriteTray.Activate";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        using var activationEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivationEventName);

        if (!createdNew)
        {
            activationEvent.Set();
            return;
        }

        using var application = new TrayApplication(new AzuriteProcessManager(), activationEvent);
        application.Run();
        GC.KeepAlive(mutex);
    }
}
