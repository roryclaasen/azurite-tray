// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AzuriteTray.Core;
using AzuriteTray.Core.Abstractions;
using AzuriteTray.Core.Extensions;
using Windows.Wdk.System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

internal abstract class AzuriteProcessManager : IAzuriteProcessManager
{
    private const uint MaximumCommandLineBytes = 1024 * 1024;

    protected AzuriteProcessManager(AzuriteSource source, string displayName)
    {
        this.Source = source;
        this.DisplayName = displayName;
        this.DataDirectory = @"C:\azurite";
        this.DebugLogPath = Path.Combine(this.DataDirectory, "debug.log");
    }

    public AzuriteSource Source { get; }

    public string DisplayName { get; }

    public string? Version { get; protected set; }

    public string DataDirectory { get; }

    public string DebugLogPath { get; }

    public virtual bool IsAvailable => this.IdentityPath is not null;

    protected abstract string ProcessName { get; }

    [MemberNotNullWhen(true, nameof(IsAvailable))]
    protected abstract string? IdentityPath { get; }

    public bool IsRunning() => this.GetAzuriteProcessIds().Count > 0;

    public bool Start()
    {
        if (this.IsRunning())
        {
            return false;
        }

        var target = this.ResolveLaunchTarget();
        Directory.CreateDirectory(this.DataDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            WorkingDirectory = this.DataDirectory,
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
        startInfo.ArgumentList.Add(this.DataDirectory);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(this.DebugLogPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{this.DisplayName} Azurite did not start.");
        if (process.WaitForExit(TimeSpan.FromSeconds(1)))
        {
            throw new InvalidOperationException(
                $"Azurite exited during startup with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}. " +
                $"Review '{this.DebugLogPath}' for details.");
        }

        return true;
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        var processIds = this.GetAzuriteProcessIds();
        if (processIds.Count == 0)
        {
            return false;
        }

        foreach (int processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // The process exited after it was discovered.
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException($"Azurite process {processId.ToString(CultureInfo.InvariantCulture)} did not stop.", exception);
            }
        }

        return true;
    }

    protected abstract AzuriteLaunchTarget ResolveLaunchTarget();

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
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }

    private List<int> GetAzuriteProcessIds()
    {
        if (string.IsNullOrWhiteSpace(this.IdentityPath) || !File.Exists(this.IdentityPath))
        {
            return [];
        }

        var normalizedIdentityPath = NormalizePath(this.IdentityPath);
        var processIds = new List<int>();
        using var processes = Process.GetProcessesByName(this.ProcessName).ToDisposableList();
        foreach (var process in processes)
        {
            var commandLine = TryGetCommandLine(process.Id);
            if (commandLine is null)
            {
                continue;
            }

            var normalizedCommandLine = NormalizePath(commandLine);
            if (normalizedCommandLine.Contains(normalizedIdentityPath, StringComparison.OrdinalIgnoreCase))
            {
                processIds.Add(process.Id);
            }
        }

        return processIds;

        static string NormalizePath(string value) => value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Replace(@"\\", @"\", StringComparison.Ordinal);
    }

    private static unsafe string? TryGetCommandLine(int processId)
    {
        using var processHandle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, (uint)processId);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        var process = new HANDLE(processHandle.DangerousGetHandle());
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
