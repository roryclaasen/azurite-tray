// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core.Abstractions;

using System.Threading;
using System.Threading.Tasks;

public interface IAzuriteProcessManager
{
    AzuriteSource Source { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    bool IsRunning();

    bool Start();

    Task<bool> StopAsync(CancellationToken token);
}
