// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core.Extensions;

using System.Collections.Generic;

public static class IEnumerableExtensions
{
    /// <summary>
    /// Wraps the specified collection
    /// </summary>
    /// <typeparam name="T">The type of the items stored in the collection.</typeparam>
    /// <param name="enumerable">The enumerable.</param>
    /// <returns>The disposable list.</returns>
    public static DisposableList<T> ToDisposableList<T>(this IEnumerable<T> enumerable) => new(enumerable);
}
