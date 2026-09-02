// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.App;

using System;
using System.IO;

internal sealed class AzuriteSourcePreference
{
    private readonly string preferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AzuriteTray",
        "source.txt");

    public AzuriteSource Load()
    {
        if (!File.Exists(preferencePath))
        {
            return AzuriteSource.Npm;
        }

        string value = File.ReadAllText(preferencePath).Trim();
        return Enum.TryParse(value, ignoreCase: true, out AzuriteSource source) &&
            Enum.IsDefined(source)
            ? source
            : AzuriteSource.Npm;
    }

    public void Save(AzuriteSource source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        File.WriteAllText(preferencePath, source.ToString());
    }
}
