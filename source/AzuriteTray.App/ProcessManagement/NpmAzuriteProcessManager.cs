// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal sealed class NpmAzuriteProcessManager() : AzuriteProcessManager(AzuriteSource.Npm, "npm")
{
    private const string AzuriteScriptRelativePath = @"node_modules\azurite\dist\src\azurite.js";

    private readonly string[] azuriteScriptCandidates = [.. FindAzuriteScriptCandidates()];

    public override bool IsAvailable => azuriteScriptCandidates.Any(File.Exists) && FindExecutable("node.exe") is not null;

    protected override string ProcessName => "node";

    protected override AzuriteLaunchTarget ResolveLaunchTarget()
    {
        var azuriteScriptPath = azuriteScriptCandidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("npm Azurite was not found. Install it with 'npm install --global azurite'.");
        var nodePath = FindExecutable("node.exe") ?? throw new FileNotFoundException("Node.js was not found on PATH. Install Node.js before starting npm Azurite.");

        return new AzuriteLaunchTarget(nodePath, [azuriteScriptPath]);
    }

    protected override IEnumerable<string> GetIdentityPaths() => azuriteScriptCandidates;

    private static HashSet<string> FindAzuriteScriptCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(appData, "npm", AzuriteScriptRelativePath)));
        }

        foreach (string directory in GetPathDirectories())
        {
            var commandPath = Path.Combine(directory, "azurite.cmd");
            if (File.Exists(commandPath))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(directory, AzuriteScriptRelativePath)));
            }
        }

        return candidates;
    }
}
