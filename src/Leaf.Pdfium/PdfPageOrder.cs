namespace Leaf.Pdfium;

/// <summary>
/// Turns "these are the pages I want, in this order" into the two calls the engine needs: which pages to
/// delete, and the permutation to apply to what is left.
/// </summary>
/// <remarks>
/// Separated from the UI because it is the part that is easy to get wrong. Deleting pages renumbers everything
/// after them, so the permutation cannot use the original indices: a surviving page's new index is its rank
/// among the survivors.
/// </remarks>
public static class PdfPageOrder
{
    /// <summary>
    /// Plans an edit against a document of <paramref name="pageCount"/> pages.
    /// <paramref name="keptInOrder"/> lists the pages to keep, by their current index, in the order they should
    /// finish in. Every index must be in range and appear at most once.
    /// </summary>
    /// <returns>
    /// Removed: indices to delete, ascending, in terms of the document as it is now.
    /// Order: the permutation to apply after the deletions, where Order[i] is the index the i-th final page
    /// then sits at.
    /// </returns>
    public static (int[] Removed, int[] Order) Plan(int pageCount, ReadOnlySpan<int> keptInOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        if (keptInOrder.Length == 0)
        {
            throw new ArgumentException("A document must keep at least one page.", nameof(keptInOrder));
        }

        var kept = new HashSet<int>(keptInOrder.Length);
        foreach (int index in keptInOrder)
        {
            if ((uint)index >= (uint)pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(keptInOrder), $"Page index {index} is outside 0..{pageCount - 1}.");
            }

            if (!kept.Add(index))
            {
                throw new ArgumentException($"Page index {index} appears more than once.", nameof(keptInOrder));
            }
        }

        var removed = new List<int>(pageCount - kept.Count);
        for (int i = 0; i < pageCount; i++)
        {
            if (!kept.Contains(i))
            {
                removed.Add(i);
            }
        }

        // Deleting preserves the relative order of what is left, so rank among survivors is the new index.
        int[] survivors = [.. kept.Order()];
        var rank = new Dictionary<int, int>(survivors.Length);
        for (int i = 0; i < survivors.Length; i++)
        {
            rank[survivors[i]] = i;
        }

        var order = new int[keptInOrder.Length];
        for (int i = 0; i < keptInOrder.Length; i++)
        {
            order[i] = rank[keptInOrder[i]];
        }

        return ([.. removed], order);
    }
}
