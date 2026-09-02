// Copyright (c) Rory Claasen. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AzuriteTray.App;

internal sealed partial class AzuriteProcessManager
{
    private const string AzuriteScriptRelativePath = @"node_modules\azurite\dist\src\azurite.js";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint MaximumCommandLineBytes = 1024 * 1024;

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

    private static string? TryGetCommandLine(int processId)
    {
        using SafeProcessHandle processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            (uint)processId);

        if (processHandle.IsInvalid)
        {
            return null;
        }

        int status = NtQueryInformationProcess(
            processHandle,
            ProcessCommandLineInformation,
            processInformation: 0,
            processInformationLength: 0,
            out uint requiredLength);

        if (status != StatusInfoLengthMismatch ||
            requiredLength == 0 ||
            requiredLength > MaximumCommandLineBytes)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal((int)requiredLength);

        try
        {
            status = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                buffer,
                requiredLength,
                out _);

            if (status < 0)
            {
                return null;
            }

            UnicodeString commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
            return commandLine.Buffer == 0
                ? null
                : Marshal.PtrToStringUni(commandLine.Buffer, commandLine.Length / sizeof(char));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint processAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        nint processInformation,
        uint processInformationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly nint Buffer;
    }
}
