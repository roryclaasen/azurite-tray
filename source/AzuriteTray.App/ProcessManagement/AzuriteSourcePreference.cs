// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App.ProcessManagement;

using System;
using System.IO;
using AzuriteTray.Core;

internal sealed class AzuriteSourcePreference
{
    private const AzuriteSource DefaultPreference = AzuriteSource.VisualStudio;

    private readonly string preferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzuriteTray", "source.txt");

    public AzuriteSource Load()
    {
        if (!File.Exists(this.preferencePath))
        {
            return DefaultPreference;
        }

        var value = File.ReadAllText(this.preferencePath).Trim();
        return Enum.TryParse(value, ignoreCase: true, out AzuriteSource source) && Enum.IsDefined(source) ? source : DefaultPreference;
    }

    public void Save(AzuriteSource source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.preferencePath)!);
        File.WriteAllText(this.preferencePath, source.ToString());
    }
}
