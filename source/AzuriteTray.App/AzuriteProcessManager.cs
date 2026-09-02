// Copyright (c) Rory Claasen. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace AzuriteTray.App;

internal sealed class AzuriteProcessManager
{
    private const string AzuriteScriptRelativePath = @"node_modules\azurite\dist\src\azurite.js";

    private readonly string[] _azuriteScriptCandidates;

    public AzuriteProcessManager()
    {
        DataDirectory = @"C:\azurite";
        DebugLogPath = Path.Combine(DataDirectory, "debug.log");
        _azuriteScriptCandidates = FindAzuriteScriptCandidates().ToArray();
        IconPath = _azuriteScriptCandidates
            .Select(static path => Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(path)!, @"..\..\icon.ico")))
            .FirstOrDefault(File.Exists);
    }

    public string DataDirectory { get; }

    public string DebugLogPath { get; }

    public string? IconPath { get; }

    public bool IsRunning() => GetAzuriteProcessIds().Count > 0;

    public bool Start()
    {
        if (IsRunning())
        {
            return false;
        }

        string azuriteScriptPath = _azuriteScriptCandidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "Azurite was not found. Install it with 'npm install --global azurite'.");
        string nodePath = FindExecutable("node.exe")
            ?? throw new FileNotFoundException(
                "Node.js was not found on PATH. Install Node.js before starting Azurite.");

        Directory.CreateDirectory(DataDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(azuriteScriptPath);
        startInfo.ArgumentList.Add("--skipApiVersionCheck");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(DataDirectory);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(DebugLogPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Node.js did not start.");

        return true;
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        List<int> processIds = GetAzuriteProcessIds();

        if (processIds.Count == 0)
        {
            return false;
        }

        foreach (int processId in processIds)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // The process exited after it was discovered.
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    $"Azurite process {processId.ToString(CultureInfo.InvariantCulture)} did not stop.",
                    exception);
            }
        }

        return true;
    }

    private List<int> GetAzuriteProcessIds()
    {
        string[] normalizedPaths = _azuriteScriptCandidates
            .Where(File.Exists)
            .Select(NormalizePath)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            return [];
        }

        var processIds = new List<int>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'");
        using ManagementObjectCollection processes = searcher.Get();

        foreach (ManagementObject process in processes.Cast<ManagementObject>())
        {
            using (process)
            {
                if (process["CommandLine"] is not string commandLine)
                {
                    continue;
                }

                string normalizedCommandLine = NormalizePath(commandLine);

                if (normalizedPaths.Any(path =>
                    normalizedCommandLine.Contains(path, StringComparison.OrdinalIgnoreCase)))
                {
                    processIds.Add(Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture));
                }
            }
        }

        return processIds;
    }

    private static HashSet<string> FindAzuriteScriptCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(appData, "npm", AzuriteScriptRelativePath)));
        }

        foreach (string directory in GetPathDirectories())
        {
            string commandPath = Path.Combine(directory, "azurite.cmd");

            if (File.Exists(commandPath))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(directory, AzuriteScriptRelativePath)));
            }
        }

        return candidates;
    }

    private static string? FindExecutable(string fileName)
    {
        foreach (string directory in GetPathDirectories())
        {
            string path = Path.Combine(directory, fileName);

            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetPathDirectories()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));

            if (Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }

    private static string NormalizePath(string value) =>
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace(@"\\", @"\", StringComparison.Ordinal);
}
