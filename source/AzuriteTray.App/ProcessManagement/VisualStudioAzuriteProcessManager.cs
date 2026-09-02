// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AzuriteTray.Core;

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
        if (FindVSWhereExecutable() is string vsWherePath)
        {
            AddVsWhereCandidates(candidates, vsWherePath);
            return candidates;
        }

        var visualStudioDirectory = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrWhiteSpace(visualStudioDirectory))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(visualStudioDirectory, AzuriteRelativePath)));
        }

        AddCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        return candidates;
    }

    private static void AddVsWhereCandidates(HashSet<string> candidates, string vsWherePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = vsWherePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-all");
        startInfo.ArgumentList.Add("-products");
        startInfo.ArgumentList.Add("*");
        startInfo.ArgumentList.Add("-property");
        startInfo.ArgumentList.Add("installationPath");

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("vswhere.exe did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"vswhere.exe exited with code {process.ExitCode}: {error.Trim()}");
        }

        foreach (var installationPath in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(installationPath, AzuriteRelativePath)));
        }
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

    private static string? FindVSWhereExecutable()
    {
        var vsWherePath = FindExecutable("vswhere.exe");
        if (vsWherePath is not null)
        {
            return vsWherePath;
        }

        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        return null;
    }
}
