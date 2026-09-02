// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal sealed class VisualStudioAzuriteProcessManager() : AzuriteProcessManager(AzuriteSource.VisualStudio, "Visual Studio")
{
    private const string AzuriteRelativePath = @"Common7\IDE\Extensions\Microsoft\Azure Storage Emulator\azurite.exe";

    private readonly string[] azuriteExecutableCandidates = [.. FindAzuriteExecutableCandidates()];

    public override bool IsAvailable => azuriteExecutableCandidates.Any(File.Exists);

    protected override string ProcessName => "azurite";

    protected override AzuriteLaunchTarget ResolveLaunchTarget()
    {
        var executablePath = azuriteExecutableCandidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Visual Studio Azurite was not found. Install the Azure development workload in Visual Studio.");
        return new AzuriteLaunchTarget(executablePath, []);
    }

    protected override IEnumerable<string> GetIdentityPaths() => azuriteExecutableCandidates;

    private static HashSet<string> FindAzuriteExecutableCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visualStudioDirectory = Environment.GetEnvironmentVariable("VSINSTALLDIR");

        if (!string.IsNullOrWhiteSpace(visualStudioDirectory))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(visualStudioDirectory, AzuriteRelativePath)));
        }

        AddCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        return candidates;
    }

    private static void AddCandidates(HashSet<string> candidates, string programFilesDirectory)
    {
        var visualStudioRoot = Path.Combine(programFilesDirectory, "Microsoft Visual Studio");

        if (!Directory.Exists(visualStudioRoot))
        {
            return;
        }

        foreach (string versionDirectory in Directory.EnumerateDirectories(visualStudioRoot))
        {
            foreach (string editionDirectory in Directory.EnumerateDirectories(versionDirectory))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(editionDirectory, AzuriteRelativePath)));
            }
        }
    }
}
