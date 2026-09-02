// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

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

internal abstract class AzuriteProcessManager
{
    private const uint MaximumCommandLineBytes = 1024 * 1024;

    protected AzuriteProcessManager(AzuriteSource source, string displayName)
    {
        Source = source;
        DisplayName = displayName;
        DataDirectory = @"C:\azurite";
        DebugLogPath = Path.Combine(DataDirectory, "debug.log");
    }

    public AzuriteSource Source { get; }

    public string DisplayName { get; }

    public string DataDirectory { get; }

    public string DebugLogPath { get; }

    public abstract bool IsAvailable { get; }

    protected abstract string ProcessName { get; }

    public bool IsRunning() => GetAzuriteProcessIds().Count > 0;

    public bool Start()
    {
        if (IsRunning())
        {
            return false;
        }

        AzuriteLaunchTarget target = ResolveLaunchTarget();
        Directory.CreateDirectory(DataDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            WorkingDirectory = DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (string argument in target.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--skipApiVersionCheck");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(DataDirectory);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(DebugLogPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{DisplayName} Azurite did not start.");

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

    protected abstract AzuriteLaunchTarget ResolveLaunchTarget();

    protected abstract IEnumerable<string> GetIdentityPaths();

    protected static string? FindExecutable(string fileName)
    {
        foreach (string directory in GetPathDirectories())
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    protected static IEnumerable<string> GetPathDirectories()
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

    private List<int> GetAzuriteProcessIds()
    {
        string[] normalizedPaths = GetIdentityPaths()
            .Where(File.Exists)
            .Select(NormalizePath)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            return [];
        }

        var processIds = new List<int>();

        foreach (Process process in Process.GetProcessesByName(ProcessName))
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

    protected sealed record AzuriteLaunchTarget(string FileName, IReadOnlyList<string> Arguments);
}
