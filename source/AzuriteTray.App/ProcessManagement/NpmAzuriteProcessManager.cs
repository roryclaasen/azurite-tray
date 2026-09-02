// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using AzuriteTray.Core;

internal sealed class NpmAzuriteProcessManager() : AzuriteProcessManager(AzuriteSource.Npm, "npm")
{
    private const string AzuriteScriptRelativePath = @"node_modules\azurite\dist\src\azurite.js";

    public override bool IsAvailable => base.IsAvailable && FindExecutable("node.exe") is not null;

    protected override string ProcessName => "node";

    [MemberNotNullWhen(true, nameof(IsAvailable))]
    protected override string? IdentityPath { get; } = FindAzuriteScript();

    protected override AzuriteLaunchTarget ResolveLaunchTarget()
    {
        var azuriteScriptPath = this.IdentityPath ?? throw new FileNotFoundException("npm Azurite was not found. Install it with 'npm install --global azurite'.");
        var nodePath = FindExecutable("node.exe") ?? throw new FileNotFoundException("Node.js was not found on PATH. Install Node.js before starting npm Azurite.");

        return new AzuriteLaunchTarget(nodePath, [azuriteScriptPath]);
    }

    private static string? FindAzuriteScript()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrWhiteSpace(appData))
        {
            var appDataPath = Path.GetFullPath(Path.Combine(appData, "npm", AzuriteScriptRelativePath));
            if (File.Exists(appDataPath))
            {
                return appDataPath;
            }
        }

        foreach (string directory in GetPathDirectories())
        {
            var commandPath = Path.Combine(directory, "azurite.cmd");
            if (File.Exists(commandPath))
            {
                var scriptPath = Path.GetFullPath(Path.Combine(directory, AzuriteScriptRelativePath));
                if (File.Exists(scriptPath))
                {
                    return scriptPath;
                }
            }
        }

        return null;
    }
}
