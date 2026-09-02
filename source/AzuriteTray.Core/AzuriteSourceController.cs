// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzuriteTray.Core.Abstractions;

public sealed class AzuriteSourceController
{
    private readonly IAzuriteSourcePreference sourcePreference;

    public AzuriteSourceController(IEnumerable<IAzuriteProcessManager> processManagers, IAzuriteSourcePreference sourcePreference)
    {
        this.ProcessManagers = processManagers.ToDictionary(manager => manager.Source);
        ArgumentOutOfRangeException.ThrowIfZero(this.ProcessManagers.Count, nameof(processManagers));

        this.sourcePreference = sourcePreference;
        this.SelectedSource = this.SelectInitialSource(sourcePreference.Load());
    }

    public IReadOnlyDictionary<AzuriteSource, IAzuriteProcessManager> ProcessManagers { get; }

    public AzuriteSource SelectedSource { get; private set; }

    public IAzuriteProcessManager SelectedProcessManager => this.ProcessManagers[this.SelectedSource];

    public async Task<IAzuriteProcessManager?> ChangeSourceAsync(AzuriteSource source, CancellationToken token)
    {
        if (source == this.SelectedSource)
        {
            return null;
        }

        var previousSource = this.SelectedSource;
        var previousManager = this.SelectedProcessManager;
        var newManager = this.ProcessManagers[source];
        var restart = false;
        var newManagerStarted = false;
        var preferenceWriteAttempted = false;

        try
        {
            if (!newManager.IsAvailable)
            {
                throw new InvalidOperationException($"{newManager.DisplayName} Azurite is not installed.");
            }

            restart = await Task.Run(previousManager.IsRunning);
            if (restart)
            {
                await previousManager.StopAsync(token);
                newManagerStarted = await Task.Run(newManager.Start);
            }

            preferenceWriteAttempted = true;
            this.sourcePreference.Save(source);
            this.SelectedSource = source;
            return newManager;
        }
        catch (Exception exception)
        {
            var failure = await this.RollbackSourceChangeAsync(
                exception,
                previousSource,
                previousManager,
                newManager,
                restart,
                newManagerStarted,
                preferenceWriteAttempted,
                token);

            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }
    }

    private AzuriteSource SelectInitialSource(AzuriteSource preferredSource)
    {
        if (this.ProcessManagers.TryGetValue(preferredSource, out IAzuriteProcessManager? preferredManager) && preferredManager.IsAvailable)
        {
            return preferredSource;
        }

        return this.ProcessManagers.Values.FirstOrDefault(static manager => manager.IsAvailable)?.Source
            ?? this.ProcessManagers.Values.First().Source;
    }

    private async Task<Exception> RollbackSourceChangeAsync(
        Exception switchException,
        AzuriteSource previousSource,
        IAzuriteProcessManager previousManager,
        IAzuriteProcessManager newManager,
        bool restart,
        bool newManagerStarted,
        bool preferenceWriteAttempted,
        CancellationToken token)
    {
        var failures = new List<Exception> { switchException };
        this.SelectedSource = previousSource;

        if (newManagerStarted)
        {
            try
            {
                await newManager.StopAsync(token);
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
}
