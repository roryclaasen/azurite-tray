// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Wdk.System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

internal sealed class AzuriteProcessManager
{
    private const string AzuriteScriptRelativePath = @"node_modules\azurite\dist\src\azurite.js";
    private const uint MaximumCommandLineBytes = 1024 * 1024;

    private readonly string[] azuriteScriptCandidates;

    public AzuriteProcessManager()
    {
        DataDirectory = @"C:\azurite";
        DebugLogPath = Path.Combine(DataDirectory, "debug.log");
        azuriteScriptCandidates = [.. FindAzuriteScriptCandidates()];
    }

    public string DataDirectory { get; }

    public string DebugLogPath { get; }

    public bool IsRunning() => GetAzuriteProcessIds().Count > 0;

    public bool Start()
    {
        if (IsRunning())
        {
            return false;
        }

        string azuriteScriptPath = azuriteScriptCandidates.FirstOrDefault(File.Exists)
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

        if (process.WaitForExit(milliseconds: 1000))
        {
            throw new InvalidOperationException(
                $"Azurite exited during startup with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}. " +
                $"Review '{DebugLogPath}' for details.");
        }

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
        string[] normalizedPaths = azuriteScriptCandidates
            .Where(File.Exists)
            .Select(NormalizePath)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            return [];
        }

        var processIds = new List<int>();

        foreach (Process process in Process.GetProcessesByName("node"))
        {
            using (process)
            {
                string? commandLine = TryGetCommandLine(process.Id);

                if (commandLine is null)
                {
                    continue;
                }

                string normalizedCommandLine = NormalizePath(commandLine);

                if (normalizedPaths.Any(path =>
                    normalizedCommandLine.Contains(path, StringComparison.OrdinalIgnoreCase)))
                {
                    processIds.Add(process.Id);
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

    private static unsafe string? TryGetCommandLine(int processId)
    {
        using SafeFileHandle processHandle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            bInheritHandle: false,
            (uint)processId);

        if (processHandle.IsInvalid)
        {
            return null;
        }

        var process = (HANDLE)processHandle.DangerousGetHandle();
        uint requiredLength = 0;

        if (Windows.Wdk.PInvoke.NtQueryInformationProcess(
                process,
                PROCESSINFOCLASS.ProcessCommandLineInformation,
                ProcessInformation: null,
                ProcessInformationLength: 0,
                ref requiredLength) != NTSTATUS.STATUS_INFO_LENGTH_MISMATCH ||
            requiredLength == 0 ||
            requiredLength > MaximumCommandLineBytes)
        {
            return null;
        }

        var buffer = new byte[requiredLength];

        fixed (byte* processInformation = buffer)
        {
            NTSTATUS status = Windows.Wdk.PInvoke.NtQueryInformationProcess(
                process,
                PROCESSINFOCLASS.ProcessCommandLineInformation,
                processInformation,
                requiredLength,
                ref requiredLength);

            if (status.SeverityCode != NTSTATUS.Severity.Success)
            {
                return null;
            }

            var commandLine = (UNICODE_STRING*)processInformation;
            return commandLine->Length == 0
                ? null
                : new string(commandLine->Buffer, 0, commandLine->Length / sizeof(char));
        }
    }
}
