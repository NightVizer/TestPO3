using System;
using System.Collections.Generic;
using System.Linq;

using Heaps;
using Moq;
using NUnit.Framework;

namespace UnitTests_for_BMH
{
    [TestFixture]
    public class BinaryMaxHeapTests
    {
        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static BinaryMaxHeap<int> HeapOf(params int[] values)
        {
            var heap = new BinaryMaxHeap<int>();
            heap.Initialize(values);
            return heap;
        }

        /// <summary>True if every parent is >= both of its children.</summary>
        private static bool IsMaxHeap(int[] a)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int left = 2 * i + 1;
                int right = left + 1;
                if (left < a.Length && a[left] > a[i]) return false;
                if (right < a.Length && a[right] > a[i]) return false;
            }
            return true;
        }

        private static List<int> Drain(BinaryMaxHeap<int> heap)
        {
            var result = new List<int>();
            while (!heap.IsEmpty)
                result.Add(heap.ExtractMax());
            return result;
        }

        private static Mock<Comparer<int>> CreateComparerMock(bool reversed = false)
        {
            var mock = new Mock<Comparer<int>>();
            mock.Setup(c => c.Compare(It.IsAny<int>(), It.IsAny<int>()))
                .Returns((int x, int y) => reversed ? y.CompareTo(x) : x.CompareTo(y));
            return mock;
        }

        // ------------------------------------------------------------------
        // Constructors
        // ------------------------------------------------------------------

        [Test]
        public void DefaultConstructor_CreatesEmptyHeap()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.That(heap.Count, Is.EqualTo(0));
            Assert.That(heap.IsEmpty, Is.True);
        }

        [Test]
        public void ComparerConstructor_CreatesEmptyHeap()
        {
            var heap = new BinaryMaxHeap<int>(Comparer<int>.Default);

            Assert.That(heap.Count, Is.EqualTo(0));
            Assert.That(heap.IsEmpty, Is.True);
        }

        [Test]
        public void CapacityConstructor_PositiveCapacity_CreatesEmptyUsableHeap()
        {
            var heap = new BinaryMaxHeap<int>(10, Comparer<int>.Default);

            Assert.That(heap.IsEmpty, Is.True);
            heap.Add(4);
            Assert.That(heap.Peek(), Is.EqualTo(4));
        }

        [Test]
        public void CapacityConstructor_NegativeCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new BinaryMaxHeap<int>(-1, Comparer<int>.Default));
        }

        [Test]
        public void Constructor_NullComparer_FallsBackToDefaultComparer()
        {
            var heap = new BinaryMaxHeap<int>(4, null);

            heap.Add(1);
            heap.Add(3);
            heap.Add(2);

            Assert.That(heap.Peek(), Is.EqualTo(3));
        }

        [Test]
        public void Constructor_CustomComparer_DeterminesOrdering()
        {
            var reversed = Comparer<int>.Create((a, b) => b.CompareTo(a));
            var heap = new BinaryMaxHeap<int>(reversed);

            heap.Add(5);
            heap.Add(1);
            heap.Add(9);

            // With a reversed comparer the "max" is the smallest integer.
            Assert.That(Drain(heap), Is.EqualTo(new[] { 1, 5, 9 }));
        }

        // ------------------------------------------------------------------
        // Count / IsEmpty
        // ------------------------------------------------------------------

        [Test]
        public void CountAndIsEmpty_TrackAddsAndRemovals()
        {
            var heap = new BinaryMaxHeap<int>();
            Assert.That(heap.IsEmpty, Is.True);

            heap.Add(1);
            heap.Add(2);
            Assert.That(heap.Count, Is.EqualTo(2));
            Assert.That(heap.IsEmpty, Is.False);

            heap.RemoveMax();
            heap.RemoveMax();
            Assert.That(heap.Count, Is.EqualTo(0));
            Assert.That(heap.IsEmpty, Is.True);
        }

        // ------------------------------------------------------------------
        // Indexer - get
        // ------------------------------------------------------------------

        [Test]
        public void IndexerGet_ValidIndexes_ReturnHeapOrderElements()
        {
            var heap = HeapOf(1, 2, 3); // heap order: [3, 1, 2]

            Assert.That(heap[0], Is.EqualTo(3));
            Assert.That(heap[1], Is.EqualTo(1));
            Assert.That(heap[2], Is.EqualTo(2));
        }

        [Test]
        public void IndexerGet_EmptyHeap_Throws()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.Throws<IndexOutOfRangeException>(() => { var _ = heap[0]; });
        }

        [Test]
        public void IndexerGet_NegativeIndex_Throws()
        {
            var heap = HeapOf(1, 2, 3);

            Assert.Throws<IndexOutOfRangeException>(() => { var _ = heap[-1]; });
        }

        [Test]
        public void IndexerGet_IndexEqualToCount_Throws()
        {
            var heap = HeapOf(1, 2, 3);

            // Heap's own guard uses '>' so this one is rejected by the inner ArrayList.
            Assert.Throws<IndexOutOfRangeException>(() => { var _ = heap[3]; });
        }

        [Test]
        public void IndexerGet_IndexGreaterThanCount_Throws()
        {
            var heap = HeapOf(1, 2, 3);

            Assert.Throws<IndexOutOfRangeException>(() => { var _ = heap[10]; });
        }

        // ------------------------------------------------------------------
        // Indexer - set
        // ------------------------------------------------------------------

        [TestCase(-1)]
        [TestCase(3)]
        [TestCase(100)]
        public void IndexerSet_InvalidIndex_Throws(int index)
        {
            var heap = HeapOf(1, 2, 3);

            Assert.Throws<IndexOutOfRangeException>(() => heap[index] = 5);
        }

        [Test]
        public void IndexerSet_EmptyHeap_Throws()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.Throws<IndexOutOfRangeException>(() => heap[0] = 5);
        }

        [Test]
        public void IndexerSet_ValueSmallerThanRoot_StoresValueWithoutTouchingRoot()
        {
            var heap = HeapOf(10, 5, 3); // [10, 5, 3]

            heap[2] = 4;

            Assert.That(heap[2], Is.EqualTo(4));
            Assert.That(heap.Peek(), Is.EqualTo(10));
        }

        [Test]
        public void IndexerSet_LoweringInternalNode_MustKeepHeapInvariant()
        {
            var heap = HeapOf(10, 9, 8, 7, 6, 5, 4);

            heap[1] = 1;

            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            Assert.That(Drain(heap), Is.EqualTo(new[] { 10, 8, 7, 6, 5, 4, 1 }));
        }

        [Test]
        public void IndexerSet_LoweringRoot_MustPromoteRemainingMaximum()
        {
            var heap = HeapOf(10, 9, 8);

            heap[0] = 1;

            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            Assert.That(Drain(heap), Is.EqualTo(new[] { 9, 8, 1 }));
        }

        [Test]
        public void IndexerSet_ValueGreaterThanRoot_PromotesNewMax()
        {
            var heap = HeapOf(10, 5, 3);

            heap[2] = 20;

            Assert.That(heap.Peek(), Is.EqualTo(20));
            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            Assert.That(heap.ToArray(), Is.EquivalentTo(new[] { 20, 10, 5 }));
        }

        [Test]
        public void IndexerSet_ValueEqualToRoot_TakesTheGreaterOrEqualBranch()
        {
            var heap = HeapOf(10, 5, 3);

            heap[1] = 10;

            Assert.That(heap.Peek(), Is.EqualTo(10));
            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            Assert.That(heap.ToArray(), Is.EquivalentTo(new[] { 10, 10, 3 }));
        }

        [Test]
        public void IndexerSet_RootIndex_ReplacesRootAndKeepsHeapProperty()
        {
            var heap = HeapOf(10, 5, 3);

            heap[0] = 1; // 1 >= 10 is false: stored as is

            Assert.That(heap[0], Is.EqualTo(1));

            heap[0] = 50; // 50 >= 50 (compared with itself): rebuild path

            Assert.That(heap.Peek(), Is.EqualTo(50));
            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
        }

        // ------------------------------------------------------------------
        // Initialize (heapify)
        // ------------------------------------------------------------------

        [Test]
        public void Initialize_EmptyCollection_OnEmptyHeap_StaysEmpty()
        {
            var heap = new BinaryMaxHeap<int>();

            heap.Initialize(new int[0]);

            Assert.That(heap.IsEmpty, Is.True);
        }

        [Test]
        public void Initialize_EmptyCollection_LeavesExistingContentsUntouched()
        {
            var heap = HeapOf(1, 2, 3);

            heap.Initialize(new List<int>());

            Assert.That(heap.Count, Is.EqualTo(3));
            Assert.That(heap.Peek(), Is.EqualTo(3));
        }

        [Test]
        public void Initialize_NonEmptyCollection_OverridesExistingContents()
        {
            var heap = HeapOf(100, 50);

            heap.Initialize(new[] { 1, 2, 3 });

            Assert.That(heap.Count, Is.EqualTo(3));
            Assert.That(heap.Peek(), Is.EqualTo(3));
            Assert.That(heap.ToArray(), Is.Not.Contains(100));
        }

        [Test]
        public void Initialize_SingleElement()
        {
            var heap = HeapOf(42);

            Assert.That(heap.Count, Is.EqualTo(1));
            Assert.That(heap.Peek(), Is.EqualTo(42));
        }

        [Test]
        public void Initialize_TwoElements_LeftChildIsLastIndex_IsSwappedUp()
        {
            // left == lastIndex: kills a "left < lastIndex" boundary mutation
            Assert.That(HeapOf(1, 2).ToArray(), Is.EqualTo(new[] { 2, 1 }));
        }

        [Test]
        public void Initialize_ThreeElements_LeftIsLargest()
        {
            // only the left child is bigger than the node, right child is not
            Assert.That(HeapOf(1, 5, 2).ToArray(), Is.EqualTo(new[] { 5, 1, 2 }));
        }

        [Test]
        public void Initialize_ThreeElements_RightIsLargest()
        {
            // right child > left child > node
            Assert.That(HeapOf(1, 2, 5).ToArray(), Is.EqualTo(new[] { 5, 2, 1 }));
        }

        [Test]
        public void Initialize_ThreeElements_RootAlreadyLargest_NoSwap()
        {
            Assert.That(HeapOf(9, 2, 5).ToArray(), Is.EqualTo(new[] { 9, 2, 5 }));
        }

        [Test]
        public void Initialize_SevenElements_RequiresRecursiveSiftDown()
        {
            // The '1' has to sift down two levels, so the recursive _maxHeapify call is required.
            var heap = HeapOf(1, 2, 3, 4, 5, 6, 7);

            Assert.That(heap.ToArray(), Is.EqualTo(new[] { 7, 5, 6, 4, 2, 1, 3 }));
        }

        [Test]
        public void Initialize_AcceptsAnIListOfAnyKind()
        {
            var fromList = new BinaryMaxHeap<int>();
            fromList.Initialize(new List<int> { 3, 9, 1 });

            Assert.That(fromList.Peek(), Is.EqualTo(9));
        }

        [Test]
        public void Initialize_DoesNotModifySourceCollection()
        {
            var source = new[] { 1, 2, 3, 4, 5, 6, 7 };

            HeapOf(source);

            Assert.That(source, Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 7 }));
        }

        [Test]
        public void Initialize_WithDuplicates_KeepsAllOfThem()
        {
            var heap = HeapOf(4, 4, 4, 2, 2);

            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            Assert.That(Drain(heap), Is.EqualTo(new[] { 4, 4, 4, 2, 2 }));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(15)]
        [TestCase(16)]
        [TestCase(50)]
        [TestCase(200)]
        public void Initialize_RandomData_ProducesValidHeapAndSortedDrain(int size)
        {
            var rng = new Random(12345 + size);
            var values = Enumerable.Range(0, size).Select(_ => rng.Next(-100, 100)).ToArray();

            var heap = HeapOf(values);

            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            Assert.That(Drain(heap), Is.EqualTo(values.OrderByDescending(x => x).ToList()));
        }

        // ------------------------------------------------------------------
        // Add
        // ------------------------------------------------------------------

        [Test]
        public void Add_ToEmptyHeap_BecomesRoot()
        {
            var heap = new BinaryMaxHeap<int>();

            heap.Add(7);

            Assert.That(heap.Count, Is.EqualTo(1));
            Assert.That(heap.Peek(), Is.EqualTo(7));
        }

        [Test]
        public void Add_SmallerThanMax_MaxUnchanged()
        {
            var heap = HeapOf(10);

            heap.Add(3);

            Assert.That(heap.Peek(), Is.EqualTo(10));
            Assert.That(heap.Count, Is.EqualTo(2));
        }

        [Test]
        public void Add_LargerThanMax_BecomesNewMax()
        {
            var heap = HeapOf(10);

            heap.Add(30);

            Assert.That(heap.Peek(), Is.EqualTo(30));
        }

        [Test]
        public void Add_AscendingSequence_ProducesExpectedLayout()
        {
            var heap = new BinaryMaxHeap<int>();

            heap.Add(1);
            heap.Add(2);
            heap.Add(3);

            Assert.That(heap.ToArray(), Is.EqualTo(new[] { 3, 1, 2 }));
        }

        [Test]
        public void Add_Duplicates_AreAllKept()
        {
            var heap = new BinaryMaxHeap<int>();

            heap.Add(5);
            heap.Add(5);
            heap.Add(5);

            Assert.That(heap.Count, Is.EqualTo(3));
            Assert.That(Drain(heap), Is.EqualTo(new[] { 5, 5, 5 }));
        }

        [Test]
        public void Add_ManyRandomValues_KeepsHeapPropertyAfterEveryInsert()
        {
            var rng = new Random(7);
            var heap = new BinaryMaxHeap<int>();
            var added = new List<int>();

            for (int i = 0; i < 100; i++)
            {
                int value = rng.Next(-50, 50);
                added.Add(value);
                heap.Add(value);

                Assert.That(IsMaxHeap(heap.ToArray()), Is.True, "after inserting " + value);
                Assert.That(heap.Peek(), Is.EqualTo(added.Max()));
            }
        }

        // ------------------------------------------------------------------
        // Peek
        // ------------------------------------------------------------------

        [Test]
        public void Peek_EmptyHeap_Throws()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.That(() => heap.Peek(),
                Throws.TypeOf<Exception>().With.Message.EqualTo("Heap is empty."));
        }

        [Test]
        public void Peek_ReturnsMaxWithoutRemovingIt()
        {
            var heap = HeapOf(3, 8, 5);

            Assert.That(heap.Peek(), Is.EqualTo(8));
            Assert.That(heap.Peek(), Is.EqualTo(8));
            Assert.That(heap.Count, Is.EqualTo(3));
        }

        // ------------------------------------------------------------------
        // RemoveMax
        // ------------------------------------------------------------------

        [Test]
        public void RemoveMax_EmptyHeap_Throws()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.That(() => heap.RemoveMax(),
                Throws.TypeOf<Exception>().With.Message.EqualTo("Heap is empty."));
        }

        [Test]
        public void RemoveMax_SingleElement_LeavesHeapEmpty()
        {
            var heap = HeapOf(1);

            heap.RemoveMax();

            Assert.That(heap.IsEmpty, Is.True);
        }

        [Test]
        public void RemoveMax_TwoElements_LeavesTheSmallerOne()
        {
            var heap = HeapOf(1, 2);

            heap.RemoveMax();

            Assert.That(heap.Count, Is.EqualTo(1));
            Assert.That(heap.Peek(), Is.EqualTo(1));
        }

        [Test]
        public void RemoveMax_ThreeElements_LeftIsLastIndexAfterRemoval_IsSwappedUp()
        {
            // [10, 9, 8] -> swap -> [8, 9, 10] -> remove -> [8, 9]; left == lastIndex, 9 > 8
            var heap = HeapOf(10, 9, 8);

            heap.RemoveMax();

            Assert.That(heap.ToArray(), Is.EqualTo(new[] { 9, 8 }));
        }

        [Test]
        public void RemoveMax_SevenElements_ProducesExpectedLayout()
        {
            var heap = HeapOf(1, 2, 3, 4, 5, 6, 7); // [7, 5, 6, 4, 2, 1, 3]

            heap.RemoveMax();

            Assert.That(heap.ToArray(), Is.EqualTo(new[] { 6, 5, 3, 4, 2, 1 }));
        }

        [Test]
        public void RemoveMax_Repeatedly_ReturnsToEmptyAndThenThrows()
        {
            var heap = HeapOf(2, 1);

            heap.RemoveMax();
            heap.RemoveMax();

            Assert.That(heap.IsEmpty, Is.True);
            Assert.Throws<Exception>(() => heap.RemoveMax());
        }

        // ------------------------------------------------------------------
        // ExtractMax
        // ------------------------------------------------------------------

        [Test]
        public void ExtractMax_EmptyHeap_Throws()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.That(() => heap.ExtractMax(),
                Throws.TypeOf<Exception>().With.Message.EqualTo("Heap is empty."));
        }

        [Test]
        public void ExtractMax_ReturnsMaxAndRemovesIt()
        {
            var heap = HeapOf(4, 9, 2);

            int max = heap.ExtractMax();

            Assert.That(max, Is.EqualTo(9));
            Assert.That(heap.Count, Is.EqualTo(2));
            Assert.That(heap.Peek(), Is.EqualTo(4));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        [TestCase(33)]
        [TestCase(150)]
        public void ExtractMax_Repeatedly_ReturnsDescendingOrder(int size)
        {
            var rng = new Random(99 + size);
            var values = Enumerable.Range(0, size).Select(_ => rng.Next(0, 1000)).ToArray();
            var heap = HeapOf(values);

            var drained = new List<int>();
            while (!heap.IsEmpty)
            {
                drained.Add(heap.ExtractMax());
                Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            }

            Assert.That(drained, Is.EqualTo(values.OrderByDescending(x => x).ToList()));
        }

        // ------------------------------------------------------------------
        // Clear
        // ------------------------------------------------------------------

        [Test]
        public void Clear_EmptyHeap_Throws()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.That(() => heap.Clear(),
                Throws.TypeOf<Exception>().With.Message.EqualTo("Heap is empty."));
        }

        [Test]
        public void Clear_NonEmptyHeap_RemovesEverything()
        {
            var heap = HeapOf(1, 2, 3);

            heap.Clear();

            Assert.That(heap.IsEmpty, Is.True);
            Assert.That(heap.Count, Is.EqualTo(0));
        }

        [Test]
        public void Clear_HeapIsReusableAfterwards()
        {
            var heap = HeapOf(1, 2, 3);
            heap.Clear();

            heap.Add(8);
            heap.Add(4);

            Assert.That(heap.Count, Is.EqualTo(2));
            Assert.That(heap.Peek(), Is.EqualTo(8));
        }

        // ------------------------------------------------------------------
        // RebuildHeap
        // ------------------------------------------------------------------

        [Test]
        public void RebuildHeap_EmptyHeap_DoesNotThrow()
        {
            var heap = new BinaryMaxHeap<int>();

            Assert.DoesNotThrow(() => heap.RebuildHeap());
            Assert.That(heap.IsEmpty, Is.True);
        }

        [Test]
        public void RebuildHeap_RestoresHeapPropertyAfterIndexerSet()
        {
            var heap = HeapOf(10, 9, 8, 7, 6); // [10, 9, 8, 7, 6]

            // Smaller than the root, so the indexer stores it without re-heapifying.
            heap[1] = 1;

            heap.RebuildHeap();

            Assert.That(IsMaxHeap(heap.ToArray()), Is.True);
            Assert.That(heap.ToArray(), Is.EqualTo(new[] { 10, 7, 8, 1, 6 }));
        }

        [Test]
        public void RebuildHeap_OnValidHeap_ChangesNothing()
        {
            var heap = HeapOf(1, 2, 3, 4, 5, 6, 7);
            var before = heap.ToArray();

            heap.RebuildHeap();

            Assert.That(heap.ToArray(), Is.EqualTo(before));
        }

        // ------------------------------------------------------------------
        // ToArray / ToList
        // ------------------------------------------------------------------

        [Test]
        public void ToArray_EmptyHeap_ReturnsEmptyArray()
        {
            Assert.That(new BinaryMaxHeap<int>().ToArray(), Is.Empty);
        }

        [Test]
        public void ToArray_ReturnsElementsInHeapOrder()
        {
            Assert.That(HeapOf(1, 2, 3).ToArray(), Is.EqualTo(new[] { 3, 1, 2 }));
        }

        [Test]
        public void ToArray_ReturnsIndependentCopy()
        {
            var heap = HeapOf(1, 2, 3);

            var array = heap.ToArray();
            array[0] = -999;

            Assert.That(heap.Peek(), Is.EqualTo(3));
        }

        [Test]
        public void ToList_EmptyHeap_ReturnsEmptyList()
        {
            Assert.That(new BinaryMaxHeap<int>().ToList(), Is.Empty);
        }

        [Test]
        public void ToList_ReturnsElementsInHeapOrder()
        {
            Assert.That(HeapOf(1, 2, 3).ToList(), Is.EqualTo(new List<int> { 3, 1, 2 }));
        }

        [Test]
        public void ToList_ReturnsIndependentCopy()
        {
            var heap = HeapOf(1, 2, 3);

            var list = heap.ToList();
            list.Clear();

            Assert.That(heap.Count, Is.EqualTo(3));
        }

        // ------------------------------------------------------------------
        // Union
        // ------------------------------------------------------------------

        [Test]
        public void Union_FirstHeapNull_Throws()
        {
            var caller = new BinaryMaxHeap<int>();
            BinaryMaxHeap<int> first = null;
            var second = HeapOf(1);

            Assert.Throws<ArgumentNullException>(() => caller.Union(ref first, ref second));
        }

        [Test]
        public void Union_SecondHeapNull_Throws()
        {
            var caller = new BinaryMaxHeap<int>();
            var first = HeapOf(1);
            BinaryMaxHeap<int> second = null;

            Assert.Throws<ArgumentNullException>(() => caller.Union(ref first, ref second));
        }

        [Test]
        public void Union_BothHeapsNull_Throws()
        {
            var caller = new BinaryMaxHeap<int>();
            BinaryMaxHeap<int> first = null;
            BinaryMaxHeap<int> second = null;

            Assert.Throws<ArgumentNullException>(() => caller.Union(ref first, ref second));
        }

        [Test]
        public void Union_MergesAllElementsIntoNewValidHeap()
        {
            var caller = new BinaryMaxHeap<int>();
            var first = HeapOf(5, 1, 9);
            var second = HeapOf(7, 3);

            var union = caller.Union(ref first, ref second);

            Assert.That(union.Count, Is.EqualTo(5));
            Assert.That(IsMaxHeap(union.ToArray()), Is.True);
            Assert.That(Drain(union), Is.EqualTo(new[] { 9, 7, 5, 3, 1 }));
        }

        [Test]
        public void Union_SetsBothOriginalReferencesToNull()
        {
            var caller = new BinaryMaxHeap<int>();
            var first = HeapOf(1, 2);
            var second = HeapOf(3, 4);

            caller.Union(ref first, ref second);

            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
        }

        [Test]
        public void Union_FirstEmpty_ReturnsCopyOfSecond()
        {
            var caller = new BinaryMaxHeap<int>();
            var first = new BinaryMaxHeap<int>();
            var second = HeapOf(2, 8, 4);

            var union = caller.Union(ref first, ref second);

            Assert.That(Drain(union), Is.EqualTo(new[] { 8, 4, 2 }));
        }

        [Test]
        public void Union_SecondEmpty_ReturnsCopyOfFirst()
        {
            var caller = new BinaryMaxHeap<int>();
            var first = HeapOf(2, 8, 4);
            var second = new BinaryMaxHeap<int>();

            var union = caller.Union(ref first, ref second);

            Assert.That(Drain(union), Is.EqualTo(new[] { 8, 4, 2 }));
        }

        [Test]
        public void Union_BothEmpty_ReturnsEmptyHeap()
        {
            var caller = new BinaryMaxHeap<int>();
            var first = new BinaryMaxHeap<int>();
            var second = new BinaryMaxHeap<int>();

            var union = caller.Union(ref first, ref second);

            Assert.That(union.IsEmpty, Is.True);
        }

        [Test]
        public void Union_KeepsDuplicatesFromBothHeaps()
        {
            var caller = new BinaryMaxHeap<int>();
            var first = HeapOf(4, 4);
            var second = HeapOf(4, 1);

            var union = caller.Union(ref first, ref second);

            Assert.That(Drain(union), Is.EqualTo(new[] { 4, 4, 4, 1 }));
        }

        // ------------------------------------------------------------------
        // ToMinHeap
        // (assumes BinaryMinHeap<T> exposes Count / Peek() / ExtractMin() through IMinHeap<T>)
        // ------------------------------------------------------------------

        [Test]
        public void ToMinHeap_ContainsAllElementsWithMinimumOnTop()
        {
            var heap = HeapOf(5, 1, 9, 3);

            var minHeap = heap.ToMinHeap();

            Assert.That(minHeap.Count, Is.EqualTo(4));
            Assert.That(minHeap.Peek(), Is.EqualTo(1));
        }

        [Test]
        public void ToMinHeap_ExtractsInAscendingOrder()
        {
            var heap = HeapOf(5, 1, 9, 3, 7);

            var minHeap = heap.ToMinHeap();

            var drained = new List<int>();
            while (minHeap.Count > 0)
                drained.Add(minHeap.ExtractMin());

            Assert.That(drained, Is.EqualTo(new[] { 1, 3, 5, 7, 9 }));
        }

        [Test]
        public void ToMinHeap_DoesNotAffectOriginalHeap()
        {
            var heap = HeapOf(5, 1, 9);

            var minHeap = heap.ToMinHeap();
            minHeap.ExtractMin();

            Assert.That(heap.Count, Is.EqualTo(3));
            Assert.That(heap.Peek(), Is.EqualTo(9));
        }

        [Test]
        public void ToMinHeap_EmptyHeap_ReturnsEmptyMinHeap()
        {
            var minHeap = new BinaryMaxHeap<int>().ToMinHeap();

            Assert.That(minHeap.Count, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------
        // Moq: verifying interaction with the injected comparer
        // ------------------------------------------------------------------

        [Test]
        public void Add_SingleElement_NeverInvokesComparer()
        {
            var comparer = CreateComparerMock();
            var heap = new BinaryMaxHeap<int>(comparer.Object);

            heap.Add(1); // IsEmpty branch: no rebuild, so no comparisons

            comparer.Verify(c => c.Compare(It.IsAny<int>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void Add_SecondElement_InvokesInjectedComparer()
        {
            var comparer = CreateComparerMock();
            var heap = new BinaryMaxHeap<int>(comparer.Object);

            heap.Add(1);
            heap.Add(2); // non-empty branch: rebuild compares elements

            comparer.Verify(c => c.Compare(It.IsAny<int>(), It.IsAny<int>()), Times.AtLeastOnce());
            Assert.That(heap.Peek(), Is.EqualTo(2));
        }

        [Test]
        public void Initialize_InvokesInjectedComparer()
        {
            var comparer = CreateComparerMock();
            var heap = new BinaryMaxHeap<int>(3, comparer.Object);

            heap.Initialize(new[] { 1, 2, 3 });

            comparer.Verify(c => c.Compare(It.IsAny<int>(), It.IsAny<int>()), Times.AtLeastOnce());
            Assert.That(heap.Peek(), Is.EqualTo(3));
        }

        [Test]
        public void ReversedComparerMock_TurnsHeapIntoMinFirstHeap()
        {
            var comparer = CreateComparerMock(reversed: true);
            var heap = new BinaryMaxHeap<int>(comparer.Object);

            foreach (var v in new[] { 4, 8, 2, 6 })
                heap.Add(v);

            Assert.That(Drain(heap), Is.EqualTo(new[] { 2, 4, 6, 8 }));
        }

        [Test]
        public void IndexerSet_SmallerThanRoot_ComparesOnceAndDoesNotRebuild()
        {
            var comparer = CreateComparerMock();
            var heap = new BinaryMaxHeap<int>(3, comparer.Object);
            heap.Initialize(new[] { 10, 5, 3 });
            comparer.Invocations.Clear();

            heap[2] = 4;

            comparer.Verify(c => c.Compare(4, 10), Times.Once());
            comparer.Verify(c => c.Compare(It.IsAny<int>(), It.IsAny<int>()), Times.Once());
        }

        [Test]
        public void IndexerSet_GreaterThanRoot_ComparesAgainstRootThenRebuilds()
        {
            var comparer = CreateComparerMock();
            var heap = new BinaryMaxHeap<int>(3, comparer.Object);
            heap.Initialize(new[] { 10, 5, 3 });
            comparer.Invocations.Clear();

            heap[2] = 20;

            comparer.Verify(c => c.Compare(20, 10), Times.Once());
            comparer.Verify(c => c.Compare(It.IsAny<int>(), It.IsAny<int>()), Times.AtLeast(2));
        }

        [Test]
        public void ToMinHeap_PassesTheSameComparerToTheMinHeap()
        {
            var comparer = CreateComparerMock();
            var heap = new BinaryMaxHeap<int>(3, comparer.Object);
            heap.Initialize(new[] { 3, 1, 2 });
            comparer.Invocations.Clear();

            var minHeap = heap.ToMinHeap();

            // The min-heap was built through the injected comparer.
            comparer.Verify(c => c.Compare(It.IsAny<int>(), It.IsAny<int>()), Times.AtLeastOnce());
            Assert.That(minHeap.Peek(), Is.EqualTo(1));
        }
    }
}
