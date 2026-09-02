// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using AzuriteTray.Core;

internal sealed class NpmAzuriteProcessManager : AzuriteProcessManager
{
    private const string AzuriteScriptRelativePath = @"node_modules\azurite\dist\src\azurite.js";

    public NpmAzuriteProcessManager() : base(AzuriteSource.Npm, "NPM")
    {
        this.IdentityPath = FindAzuriteScript();
        this.Version = this.IdentityPath is null ? null : FindVersion(this.IdentityPath);
    }

    public override bool IsAvailable => base.IsAvailable && FindExecutable("node.exe") is not null;

    protected override string ProcessName => "node";

    [MemberNotNullWhen(true, nameof(IsAvailable))]
    protected override string? IdentityPath { get; }

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

    private static string? FindVersion(string scriptPath)
    {
        var scriptDirectory = Path.GetDirectoryName(scriptPath) ?? throw new InvalidOperationException("The npm Azurite script path has no directory.");
        var packagePath = Path.GetFullPath( Path.Combine(scriptDirectory, "..", "..", "package.json"));
        if (!File.Exists(packagePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(packagePath);
            using JsonDocument document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("version", out JsonElement version) ? version.GetString() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not read the npm Azurite version: {exception}");
            return null;
        }
    }
}
