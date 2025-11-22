using Xunit;
using VPM.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPM.Tests.Models
{
    public class AsyncObservableCollectionTests
    {
        [Fact]
        public void AsyncObservableCollection_DefaultConstructor_CreatesEmptyCollection()
        {
            var collection = new AsyncObservableCollection<int>();

            Assert.NotNull(collection);
            Assert.Empty(collection);
        }

        [Fact]
        public void AsyncObservableCollection_Add_IncreasesCount()
        {
            var collection = new AsyncObservableCollection<int>();

            collection.Add(1);

            Assert.Single(collection);
            Assert.Contains(1, collection);
        }

        [Fact]
        public void AsyncObservableCollection_Add_MultipleItems_CountsCorrectly()
        {
            var collection = new AsyncObservableCollection<int>();

            collection.Add(1);
            collection.Add(2);
            collection.Add(3);

            Assert.Equal(3, collection.Count);
        }

        [Fact]
        public void AsyncObservableCollection_Remove_RemovesItem()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2, 3 };

            var removed = collection.Remove(2);

            Assert.True(removed);
            Assert.Equal(2, collection.Count);
            Assert.DoesNotContain(2, collection);
        }

        [Fact]
        public void AsyncObservableCollection_Remove_NonExistentItem_ReturnsFalse()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2 };

            var removed = collection.Remove(99);

            Assert.False(removed);
            Assert.Equal(2, collection.Count);
        }

        [Fact]
        public void AsyncObservableCollection_Clear_RemovesAllItems()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2, 3, 4, 5 };

            collection.Clear();

            Assert.Empty(collection);
            Assert.Equal(0, collection.Count);
        }

        [Fact]
        public void AsyncObservableCollection_Contains_ReturnsTrueForExistingItem()
        {
            var collection = new AsyncObservableCollection<string> { "a", "b", "c" };

            Assert.True(collection.Contains("b"));
        }

        [Fact]
        public void AsyncObservableCollection_Contains_ReturnsFalseForNonExistentItem()
        {
            var collection = new AsyncObservableCollection<string> { "a", "b", "c" };

            Assert.False(collection.Contains("z"));
        }

        [Fact]
        public void AsyncObservableCollection_Indexer_RetrievesItem()
        {
            var collection = new AsyncObservableCollection<int> { 10, 20, 30 };

            Assert.Equal(20, collection[1]);
        }

        [Fact]
        public void AsyncObservableCollection_Indexer_SetsItem()
        {
            var collection = new AsyncObservableCollection<int> { 10, 20, 30 };

            collection[1] = 25;

            Assert.Equal(25, collection[1]);
        }

        [Fact]
        public void AsyncObservableCollection_InsertAt_InsertsItemAtIndex()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2, 4, 5 };

            collection.Insert(2, 3);

            Assert.Equal(5, collection.Count);
            Assert.Equal(3, collection[2]);
        }

        [Fact]
        public void AsyncObservableCollection_RemoveAt_RemovesItemAtIndex()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2, 3, 4, 5 };

            collection.RemoveAt(2);

            Assert.Equal(4, collection.Count);
            Assert.Equal(4, collection[2]);
        }

        [Fact]
        public void AsyncObservableCollection_IndexOf_ReturnsCorrectIndex()
        {
            var collection = new AsyncObservableCollection<string> { "a", "b", "c", "d" };

            var index = collection.IndexOf("c");

            Assert.Equal(2, index);
        }

        [Fact]
        public void AsyncObservableCollection_IndexOf_ReturnsNegativeOneForNonExistent()
        {
            var collection = new AsyncObservableCollection<string> { "a", "b", "c" };

            var index = collection.IndexOf("z");

            Assert.Equal(-1, index);
        }

        [Fact]
        public void AsyncObservableCollection_Enumerate_ReturnsAllItems()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2, 3, 4, 5 };

            var items = collection.ToList();

            Assert.Equal(5, items.Count);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, items);
        }

        [Fact]
        public void AsyncObservableCollection_ReplaceAll_AddsMultipleItems()
        {
            var collection = new AsyncObservableCollection<int>();
            var itemsToAdd = new[] { 1, 2, 3, 4, 5 };

            collection.ReplaceAll(itemsToAdd);

            Assert.Equal(5, collection.Count);
            Assert.Equal(itemsToAdd, collection.ToArray());
        }

        [Fact]
        public void AsyncObservableCollection_CopyTo_CopiesItemsToArray()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2, 3 };
            var destination = new int[5];

            collection.CopyTo(destination, 1);

            Assert.Equal(0, destination[0]);
            Assert.Equal(1, destination[1]);
            Assert.Equal(2, destination[2]);
            Assert.Equal(3, destination[3]);
            Assert.Equal(0, destination[4]);
        }

        [Fact]
        public void AsyncObservableCollection_IsReadOnly_ReturnsFalse()
        {
            var collection = new AsyncObservableCollection<int>();

            Assert.False(collection.IsReadOnly);
        }

        [Fact]
        public void AsyncObservableCollection_WithDifferentTypes_Works()
        {
            var stringCollection = new AsyncObservableCollection<string> { "a", "b" };
            var doubleCollection = new AsyncObservableCollection<double> { 1.5, 2.5 };
            var boolCollection = new AsyncObservableCollection<bool> { true, false };

            Assert.Equal(2, stringCollection.Count);
            Assert.Equal(2, doubleCollection.Count);
            Assert.Equal(2, boolCollection.Count);
        }

        [Fact]
        public void AsyncObservableCollection_SequentialOperations_MaintainsCorrectState()
        {
            var collection = new AsyncObservableCollection<int>();

            collection.Add(1);
            collection.Add(2);
            collection.Add(3);
            Assert.Equal(3, collection.Count);

            collection.Remove(2);
            Assert.Equal(2, collection.Count);

            collection.Insert(1, 2);
            Assert.Equal(3, collection.Count);

            collection.Clear();
            Assert.Equal(0, collection.Count);
        }

        [Fact]
        public void AsyncObservableCollection_AddSameItemMultipleTimes_AllowsDuplicates()
        {
            var collection = new AsyncObservableCollection<int>();

            collection.Add(5);
            collection.Add(5);
            collection.Add(5);

            Assert.Equal(3, collection.Count);
        }

        [Fact]
        public void AsyncObservableCollection_RemoveOnlyRemovesFirstOccurrence()
        {
            var collection = new AsyncObservableCollection<int> { 1, 2, 2, 2, 3 };

            collection.Remove(2);

            Assert.Equal(4, collection.Count);
            Assert.Equal(new[] { 1, 2, 2, 3 }, collection.ToArray());
        }

        [Fact]
        public void AsyncObservableCollection_BoundaryConditions_EmptyCollectionIndexOutOfRange()
        {
            var collection = new AsyncObservableCollection<int>();

            Assert.Throws<ArgumentOutOfRangeException>(() => collection[0]);
        }

        [Fact]
        public void AsyncObservableCollection_LargeCollection_HandlesThousandItems()
        {
            var collection = new AsyncObservableCollection<int>();
            var count = 1000;

            for (int i = 0; i < count; i++)
            {
                collection.Add(i);
            }

            Assert.Equal(count, collection.Count);
            Assert.Equal(500, collection[500]);
        }
    }
}
