// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// Represents a List of <see cref="IDisposable"/> items.
/// </summary>
/// <typeparam name="T">The type for the items stored in the collection.</typeparam>
public class DisposableList<T>(IEnumerable<T> values) : List<T>(values), IDisposable
{
    /// <inheritdoc/>
    public void Dispose()
    {
        var exceptions = new List<Exception>();
        foreach (var item in this)
        {
            try
            {
                (item as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        this.Clear();
        if (exceptions.Count != 0)
        {
            throw new AggregateException("Failed to dispose of elements inside DisposableList<T>", exceptions);
        }
    }
}
