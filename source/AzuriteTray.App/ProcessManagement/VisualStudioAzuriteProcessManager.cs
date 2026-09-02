// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.Diagnostics;
using System.IO;
using AzuriteTray.Core;

internal sealed class VisualStudioAzuriteProcessManager() : AzuriteProcessManager(AzuriteSource.VisualStudio, "Visual Studio")
{
    private const string AzuriteRelativePath = @"Common7\IDE\Extensions\Microsoft\Azure Storage Emulator\azurite.exe";

    protected override string ProcessName => "azurite";

    protected override string? IdentityPath { get; } = FindAzuriteExecutable();

    protected override AzuriteLaunchTarget ResolveLaunchTarget()
    {
        var executablePath = this.IdentityPath ?? throw new FileNotFoundException("Visual Studio Azurite was not found. Install the Azure development workload in Visual Studio.");
        return new AzuriteLaunchTarget(executablePath, []);
    }

    private static string? FindAzuriteExecutable()
    {
        if (FindVSWhereExecutable() is string vsWherePath)
        {
            return FindVsWhereAzuriteExecutable(vsWherePath);
        }

        var visualStudioDirectory = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrWhiteSpace(visualStudioDirectory))
        {
            var environmentPath = Path.GetFullPath(Path.Combine(visualStudioDirectory, AzuriteRelativePath));
            if (File.Exists(environmentPath))
            {
                return environmentPath;
            }
        }

        return FindAzuriteExecutable(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            ?? FindAzuriteExecutable(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    }

    private static string? FindVsWhereAzuriteExecutable(string vsWherePath)
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
            var executablePath = Path.GetFullPath(Path.Combine(installationPath, AzuriteRelativePath));
            if (File.Exists(executablePath))
            {
                return executablePath;
            }
        }

        return null;
    }

    private static string? FindAzuriteExecutable(string programFilesDirectory)
    {
        var visualStudioRoot = Path.Combine(programFilesDirectory, "Microsoft Visual Studio");
        if (!Directory.Exists(visualStudioRoot))
        {
            return null;
        }

        foreach (string versionDirectory in Directory.EnumerateDirectories(visualStudioRoot))
        {
            foreach (string editionDirectory in Directory.EnumerateDirectories(versionDirectory))
            {
                var executablePath = Path.GetFullPath(Path.Combine(editionDirectory, AzuriteRelativePath));
                if (File.Exists(executablePath))
                {
                    return executablePath;
                }
            }
        }

        return null;
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
