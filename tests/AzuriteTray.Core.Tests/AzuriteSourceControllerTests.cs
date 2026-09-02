// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AzuriteTray.Core.Abstractions;

[TestClass]
public sealed class AzuriteSourceControllerTests(TestContext testContext)
{
    private readonly TestContext testContext = testContext;

    [TestMethod]
    public void Constructor_WithAvailablePreference_SelectsPreferredSource()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true);
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: true);
        var preference = new FakeSourcePreference(AzuriteSource.VisualStudio);

        var controller = new AzuriteSourceController([npm, visualStudio], preference);

        Assert.AreEqual(AzuriteSource.VisualStudio, controller.SelectedSource);
        Assert.AreSame(visualStudio, controller.SelectedProcessManager);
    }

    [TestMethod]
    public void Constructor_WithUnavailablePreference_SelectsFirstAvailableSource()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true);
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: false);
        var preference = new FakeSourcePreference(AzuriteSource.VisualStudio);

        var controller = new AzuriteSourceController([npm, visualStudio], preference);

        Assert.AreEqual(AzuriteSource.Npm, controller.SelectedSource);
        Assert.AreSame(npm, controller.SelectedProcessManager);
    }

    [TestMethod]
    public void Constructor_WithNoAvailableSources_SelectsFirstSource()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: false);
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: false);
        var preference = new FakeSourcePreference(AzuriteSource.VisualStudio);

        var controller = new AzuriteSourceController([npm, visualStudio], preference);

        Assert.AreEqual(AzuriteSource.Npm, controller.SelectedSource);
        Assert.AreSame(npm, controller.SelectedProcessManager);
    }

    [TestMethod]
    public async Task ChangeSourceAsync_WithSelectedSource_DoesNothing()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true);
        var preference = new FakeSourcePreference(AzuriteSource.Npm);
        var controller = new AzuriteSourceController([npm], preference);

        var result = await controller.ChangeSourceAsync(
            AzuriteSource.Npm,
            this.testContext.CancellationToken);

        Assert.IsNull(result);
        Assert.AreEqual(0, npm.StartCount);
        Assert.AreEqual(0, npm.StopCount);
        Assert.IsEmpty(preference.SavedSources);
    }

    [TestMethod]
    public async Task ChangeSourceAsync_WithUnavailableSource_ThrowsWithoutChangingSource()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true);
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: false);
        var preference = new FakeSourcePreference(AzuriteSource.Npm);
        var controller = new AzuriteSourceController([npm, visualStudio], preference);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => controller.ChangeSourceAsync(
                AzuriteSource.VisualStudio,
                this.testContext.CancellationToken));

        Assert.AreEqual(AzuriteSource.Npm, controller.SelectedSource);
        Assert.AreEqual(0, npm.StopCount);
        Assert.AreEqual(0, visualStudio.StartCount);
        Assert.IsEmpty(preference.SavedSources);
    }

    [TestMethod]
    public async Task ChangeSourceAsync_WithStoppedSource_SavesAndSelectsNewSource()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true);
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: true);
        var preference = new FakeSourcePreference(AzuriteSource.Npm);
        var controller = new AzuriteSourceController([npm, visualStudio], preference);

        var result = await controller.ChangeSourceAsync(
            AzuriteSource.VisualStudio,
            this.testContext.CancellationToken);

        Assert.AreSame(visualStudio, result);
        Assert.AreEqual(AzuriteSource.VisualStudio, controller.SelectedSource);
        Assert.HasCount(1, preference.SavedSources);
        Assert.AreEqual(AzuriteSource.VisualStudio, preference.SavedSources[0]);
        Assert.AreEqual(0, npm.StopCount);
        Assert.AreEqual(0, visualStudio.StartCount);
    }

    [TestMethod]
    public async Task ChangeSourceAsync_WithRunningSource_RestartsWithNewSource()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true)
        {
            IsRunningResult = true
        };
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: true);
        var controller = new AzuriteSourceController(
            [npm, visualStudio],
            new FakeSourcePreference(AzuriteSource.Npm));

        await controller.ChangeSourceAsync(
            AzuriteSource.VisualStudio,
            this.testContext.CancellationToken);

        Assert.AreEqual(1, npm.StopCount);
        Assert.AreEqual(1, visualStudio.StartCount);
        Assert.IsTrue(visualStudio.IsRunningResult);
    }

    [TestMethod]
    public async Task ChangeSourceAsync_WhenNewSourceStartFails_RestoresPreviousSource()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true)
        {
            IsRunningResult = true
        };
        var startException = new InvalidOperationException("Start failed.");
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: true)
        {
            StartException = startException
        };
        var preference = new FakeSourcePreference(AzuriteSource.Npm);
        var controller = new AzuriteSourceController([npm, visualStudio], preference);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => controller.ChangeSourceAsync(
                AzuriteSource.VisualStudio,
                this.testContext.CancellationToken));

        Assert.AreSame(startException, exception);
        Assert.AreEqual(AzuriteSource.Npm, controller.SelectedSource);
        Assert.AreEqual(1, npm.StopCount);
        Assert.AreEqual(1, npm.StartCount);
        Assert.IsTrue(npm.IsRunningResult);
        Assert.IsEmpty(preference.SavedSources);
    }

    [TestMethod]
    public async Task ChangeSourceAsync_WhenChangeAndRollbackFail_AggregatesExceptions()
    {
        var npm = new FakeProcessManager(AzuriteSource.Npm, isAvailable: true)
        {
            IsRunningResult = true
        };
        var rollbackException = new InvalidOperationException("Rollback stop failed.");
        var visualStudio = new FakeProcessManager(AzuriteSource.VisualStudio, isAvailable: true)
        {
            StopException = rollbackException
        };
        var saveException = new IOException("Preference save failed.");
        var preference = new FakeSourcePreference(AzuriteSource.Npm)
        {
            SaveException = saveException
        };
        var controller = new AzuriteSourceController([npm, visualStudio], preference);

        var exception = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => controller.ChangeSourceAsync(
                AzuriteSource.VisualStudio,
                this.testContext.CancellationToken));

        Assert.AreEqual(AzuriteSource.Npm, controller.SelectedSource);
        Assert.AreEqual(1, npm.StopCount);
        Assert.AreEqual(1, npm.StartCount);
        Assert.AreEqual(1, visualStudio.StartCount);
        Assert.AreEqual(1, visualStudio.StopCount);
        Assert.HasCount(3, exception.InnerExceptions);
        Assert.AreSame(saveException, exception.InnerExceptions[0]);
        Assert.AreSame(rollbackException, exception.InnerExceptions[1]);
        Assert.AreSame(saveException, exception.InnerExceptions[2]);
    }

    private sealed class FakeProcessManager(
        AzuriteSource source,
        bool isAvailable) : IAzuriteProcessManager
    {
        public AzuriteSource Source { get; } = source;

        public string DisplayName => this.Source.ToString();

        public bool IsAvailable { get; } = isAvailable;

        public bool IsRunningResult { get; set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Exception? StartException { get; init; }

        public Exception? StopException { get; init; }

        public bool IsRunning() => this.IsRunningResult;

        public bool Start()
        {
            this.StartCount++;
            if (this.StartException is not null)
            {
                throw this.StartException;
            }

            this.IsRunningResult = true;
            return true;
        }

        public Task<bool> StopAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            this.StopCount++;
            if (this.StopException is not null)
            {
                throw this.StopException;
            }

            this.IsRunningResult = false;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeSourcePreference(AzuriteSource loadedSource)
        : IAzuriteSourcePreference
    {
        public List<AzuriteSource> SavedSources { get; } = [];

        public Exception? SaveException { get; init; }

        public AzuriteSource Load() => loadedSource;

        public void Save(AzuriteSource source)
        {
            if (this.SaveException is not null)
            {
                throw this.SaveException;
            }

            this.SavedSources.Add(source);
        }
    }
}
