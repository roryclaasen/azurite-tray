// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core.Abstractions;

public interface IAzuriteSourcePreference
{
    AzuriteSource Load();

    void Save(AzuriteSource source);
}
