// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core;

public interface IAzuriteSourcePreference
{
    AzuriteSource Load();

    void Save(AzuriteSource source);
}
