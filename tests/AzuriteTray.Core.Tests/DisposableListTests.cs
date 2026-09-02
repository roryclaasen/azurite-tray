// Copyright (c) Rory Claasen. All rights reserved.

namespace AzuriteTray.Core.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

[TestClass]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposing objects is part of the tests.")]
public sealed class DisposableListTests
{
    [TestMethod]
    public void Dispose_WithEmptyList_DoesNotThrow()
    {
        var list = new DisposableList<IDisposable>([]);

        list.Dispose();
    }

    [TestMethod]
    public void Dispose_WithDisposableItems_DisposesAllItems()
    {
        var item1 = new TrackedDisposable();
        var item2 = new TrackedDisposable();
        var list = new DisposableList<TrackedDisposable>([item1, item2]);

        list.Dispose();

        Assert.IsTrue(item1.IsDisposed);
        Assert.IsTrue(item2.IsDisposed);
    }

    [TestMethod]
    public void Dispose_WithNonDisposableItems_DoesNotThrow()
    {
        var list = new DisposableList<string>(["a", "b", "c"]);

        list.Dispose();
    }

    [TestMethod]
    public void Dispose_WithMixedItems_DisposesAllDisposableItems()
    {
        var disposable = new TrackedDisposable();
        var list = new DisposableList<object>([disposable, "not disposable", 42]);

        list.Dispose();

        Assert.IsTrue(disposable.IsDisposed);
    }

    [TestMethod]
    public void Dispose_WithNullItems_DoesNotThrow()
    {
        var list = new DisposableList<object?>([null, "value"]);

        list.Dispose();
    }

    [TestMethod]
    public void Dispose_WithItems_ClearsList()
    {
        var list = new DisposableList<TrackedDisposable>(
            [new TrackedDisposable(), new TrackedDisposable()]);

        list.Dispose();

        Assert.IsEmpty(list);
    }

    [TestMethod]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var item = new TrackedDisposable();
        var list = new DisposableList<TrackedDisposable>([item]);

        list.Dispose();
        list.Dispose();

        Assert.AreEqual(1, item.DisposeCount);
    }

    [TestMethod]
    public void Dispose_WithMultipleItems_DisposesInListOrder()
    {
        var disposalOrder = new List<int>();
        var expectedOrder = new List<int> { 1, 2, 3 };
        var list = new DisposableList<OrderedDisposable>(
            [
                new(1, disposalOrder),
                new(2, disposalOrder),
                new(3, disposalOrder)
            ]);

        list.Dispose();

        Assert.AreSequenceEqual(expectedOrder, disposalOrder);
    }

    [TestMethod]
    public void Dispose_WithItemsThatThrow_DisposesAllItemsAndAggregatesExceptions()
    {
        var firstException = new InvalidOperationException("First failure");
        var secondException = new ArgumentException("Second failure");
        var firstItem = new ThrowingDisposable(firstException);
        var trackedItem = new TrackedDisposable();
        var secondItem = new ThrowingDisposable(secondException);
        var list = new DisposableList<IDisposable>([firstItem, trackedItem, secondItem]);

        var exception = Assert.ThrowsExactly<AggregateException>(list.Dispose);

        Assert.IsTrue(firstItem.WasDisposeAttempted);
        Assert.IsTrue(trackedItem.IsDisposed);
        Assert.IsTrue(secondItem.WasDisposeAttempted);
        Assert.IsEmpty(list);
        Assert.HasCount(2, exception.InnerExceptions);
        Assert.AreSame(firstException, exception.InnerExceptions[0]);
        Assert.AreSame(secondException, exception.InnerExceptions[1]);
    }

    [TestMethod]
    public void Constructor_WithValues_ContainsAllValues()
    {
        var items = new[]
        {
            new TrackedDisposable(),
            new TrackedDisposable(),
            new TrackedDisposable()
        };

        var list = new DisposableList<TrackedDisposable>(items);

        Assert.HasCount(3, list);
        Assert.AreSequenceEqual(items, list);
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public bool IsDisposed => this.DisposeCount > 0;

        public void Dispose() => this.DisposeCount++;
    }

    private sealed class OrderedDisposable(int value, List<int> disposalOrder) : IDisposable
    {
        public void Dispose() => disposalOrder.Add(value);
    }

    private sealed class ThrowingDisposable(Exception exception) : IDisposable
    {
        public bool WasDisposeAttempted { get; private set; }

        public void Dispose()
        {
            this.WasDisposeAttempted = true;
            throw exception;
        }
    }
}
