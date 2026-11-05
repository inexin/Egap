using System.Runtime.InteropServices;

namespace Egap.Spreadsheets.Recalculation;

/// <summary>A cell of the workbook: a sheet's number in the graph, and its zero-based row and column.</summary>
internal readonly record struct CellKey(int Sheet, int Row, int Column)
{
    /// <summary>The key in one number: 16 bits of sheet, 20 of row and 14 of column.</summary>
    public long Packed => ((long)Sheet << 34) | ((long)Row << 14) | (uint)Column;

    public static CellKey Unpack(long packed) => new((int)(packed >> 34), (int)((packed >> 14) & 0xFFFFF), (int)(packed & 0x3FFF));

    public override string ToString() => $"{Sheet}!{new CellAddress(Row, Column)}";
}

/// <summary>A range a formula reads: a sheet's number in the graph and the range on it.</summary>
internal readonly record struct Precedent(int Sheet, RangeAddress Range)
{
    public Precedent(CellKey cell)
        : this(cell.Sheet, new RangeAddress(cell.Row, cell.Column, cell.Row, cell.Column))
    {
    }
}

/// <summary>
/// Which formula cells read which cells, kept so a change can find the formulas it makes stale. A formula cell is a node with
/// the ranges it reads; single cells are found through a hash table, small ranges through the 64-by-64 tiles they cover,
/// and large ones (whole columns, say) through a short list per sheet. Changes are only noted when they happen; the formulas
/// they make stale are worked out once, by <see cref="Plan"/>, so a thousand edits cost one walk of the graph. Not safe
/// for use from more than one thread at a time.
/// </summary>
internal sealed class DependencyGraph
{
    private const int TileShift = 6;
    private const int MaxTiles = 16;

    private readonly Dictionary<long, Node> nodes = [];
    private readonly Dictionary<long, List<Node>> cellReaders = [];
    private readonly Dictionary<long, List<Node>> tileReaders = [];
    private readonly Dictionary<int, List<Node>> wideReaders = [];
    private readonly HashSet<long> changed = [];
    private readonly HashSet<Node> volatiles = [];
    private bool all;

    /// <summary>How many formula cells the graph holds.</summary>
    public int Count => nodes.Count;

    /// <summary>Whether <paramref name="cell"/> holds a formula the graph knows.</summary>
    public bool Contains(CellKey cell) => nodes.ContainsKey(cell.Packed);

    /// <summary>The ranges the formula at <paramref name="cell"/> reads, or an empty span for a cell that has none.</summary>
    public ReadOnlySpan<Precedent> PrecedentsOf(CellKey cell) => nodes.TryGetValue(cell.Packed, out Node? node) ? node.Precedents : [];

    /// <summary>
    /// Records the formula at <paramref name="cell"/>, replacing what it read before, and notes it as changed. A volatile
    /// formula (one calling NOW, RAND or INDIRECT, say) is calculated in every plan.
    /// </summary>
    public void Set(CellKey cell, ReadOnlySpan<Precedent> precedents, bool isVolatile)
    {
        Remove(cell);
        Node node = new(cell, precedents.ToArray());
        nodes.Add(cell.Packed, node);
        foreach (Precedent precedent in node.Precedents)
        {
            Index(node, precedent, add: true);
        }

        if (isVolatile)
        {
            volatiles.Add(node);
        }

        changed.Add(cell.Packed);
    }

    /// <summary>Forgets the formula at <paramref name="cell"/>, if there is one, and notes the cell as changed.</summary>
    public bool Remove(CellKey cell)
    {
        changed.Add(cell.Packed);
        if (!nodes.Remove(cell.Packed, out Node? node))
        {
            return false;
        }

        foreach (Precedent precedent in node.Precedents)
        {
            Index(node, precedent, add: false);
        }

        volatiles.Remove(node);
        return true;
    }

    /// <summary>Notes that the value at <paramref name="cell"/> changed, so the formulas reading it are stale.</summary>
    public void Changed(CellKey cell) => changed.Add(cell.Packed);

    /// <summary>Notes that every formula is stale, as for a full calculation.</summary>
    public void ChangedAll() => all = true;

    /// <summary>Whether anything was noted since the last plan.</summary>
    public bool HasChanges => all || changed.Count > 0 || volatiles.Count > 0;

    /// <summary>The formula cells that read <paramref name="cell"/>.</summary>
    public IEnumerable<CellKey> Dependents(CellKey cell)
    {
        List<Node> found = [];
        Readers(cell, found);
        return found.Select(n => n.Key);
    }

    /// <summary>
    /// The order to calculate in: every formula made stale by the changes noted since the last plan, every volatile formula,
    /// and every formula that reads one of those, in steps. A step's formulas read nothing calculated in the same or a later
    /// step, so a step that is not a cycle can be calculated in parallel. Formulas that read each other, directly or in a
    /// ring, form a cycle step of their own. The noted changes are cleared.
    /// </summary>
    public CalculationPlan Plan()
    {
        List<Node> stale = Stale();
        changed.Clear();
        all = false;
        return Order(stale);
    }

    private List<Node> Stale()
    {
        if (all)
        {
            return [.. nodes.Values];
        }

        // Breadth first from the changed cells and the volatile formulas: each stale formula's own cell is changed too.
        List<Node> stale = [];
        HashSet<Node> seen = [];
        Queue<CellKey> queue = new();
        foreach (long packed in changed)
        {
            queue.Enqueue(CellKey.Unpack(packed));
            if (nodes.TryGetValue(packed, out Node? node) && seen.Add(node))
            {
                stale.Add(node);
            }
        }

        foreach (Node node in volatiles)
        {
            if (seen.Add(node))
            {
                stale.Add(node);
                queue.Enqueue(node.Key);
            }
        }

        List<Node> found = [];
        while (queue.TryDequeue(out CellKey cell))
        {
            found.Clear();
            Readers(cell, found);
            foreach (Node reader in found)
            {
                if (seen.Add(reader))
                {
                    stale.Add(reader);
                    queue.Enqueue(reader.Key);
                }
            }
        }

        return stale;
    }

    /// <summary>
    /// Orders the stale formulas: Tarjan's strongly connected components over the formulas that read each other (iterative,
    /// so a chain of a million cells does not overflow the stack), then each component's step is one more than the latest
    /// step of a component it reads.
    /// </summary>
    private CalculationPlan Order(List<Node> stale)
    {
        int count = stale.Count;
        Dictionary<Node, int> number = new(count);
        for (int i = 0; i < count; i++)
        {
            number[stale[i]] = i;
        }

        // Edges from each formula to the stale formulas that read it.
        int[] edgeStart = new int[count + 1];
        List<int> edges = [];
        List<Node> found = [];
        bool[] selfLoop = new bool[count];
        for (int i = 0; i < count; i++)
        {
            edgeStart[i] = edges.Count;
            found.Clear();
            Readers(stale[i].Key, found);
            foreach (Node reader in found)
            {
                if (number.TryGetValue(reader, out int j))
                {
                    edges.Add(j);
                    selfLoop[i] |= j == i;
                }
            }
        }

        edgeStart[count] = edges.Count;
        int[] component = Components(count, edgeStart, CollectionsMarshal.AsSpan(edges), out int components);

        // Tarjan numbers components in reverse topological order: a component is numbered after every component it
        // reaches, so readers come first. Steps go from the highest number down.
        int[] size = new int[components];
        foreach (int c in component)
        {
            size[c]++;
        }

        int[] level = new int[components];
        List<int>[] members = new List<int>[components];
        for (int c = 0; c < components; c++)
        {
            members[c] = [];
        }

        for (int i = 0; i < count; i++)
        {
            members[component[i]].Add(i);
        }

        int maxLevel = 0;
        for (int c = components - 1; c >= 0; c--)
        {
            foreach (int i in members[c])
            {
                for (int e = edgeStart[i]; e < edgeStart[i + 1]; e++)
                {
                    int target = component[edges[e]];
                    if (target != c)
                    {
                        level[target] = Math.Max(level[target], level[c] + 1);
                    }
                }
            }

            maxLevel = Math.Max(maxLevel, level[c]);
        }

        // Within a level, plain formulas form one parallel step; each cycle is a step of its own after them.
        List<CellKey>[] plain = new List<CellKey>[maxLevel + 1];
        List<List<CellKey>>[] cycles = new List<List<CellKey>>[maxLevel + 1];
        for (int c = components - 1; c >= 0; c--)
        {
            int l = level[c];
            bool cycle = size[c] > 1 || selfLoop[members[c][0]];
            if (cycle)
            {
                (cycles[l] ??= []).Add([.. members[c].Select(i => stale[i].Key)]);
            }
            else
            {
                (plain[l] ??= []).Add(stale[members[c][0]].Key);
            }
        }

        List<CalculationStep> steps = [];
        for (int l = 0; l <= maxLevel; l++)
        {
            if (plain[l] is { } cells)
            {
                steps.Add(new CalculationStep([.. cells], Cycle: false));
            }

            foreach (List<CellKey> ring in cycles[l] ?? [])
            {
                steps.Add(new CalculationStep([.. ring], Cycle: true));
            }
        }

        return new CalculationPlan(steps);
    }

    /// <summary>Tarjan's algorithm without recursion: each node's component number, in reverse topological order.</summary>
    private static int[] Components(int count, int[] edgeStart, ReadOnlySpan<int> edges, out int components)
    {
        int[] index = new int[count];
        int[] low = new int[count];
        int[] component = new int[count];
        bool[] onStack = new bool[count];
        Array.Fill(index, -1);
        Stack<int> stack = new();
        Stack<(int Node, int Edge)> calls = new();
        int next = 0;
        components = 0;
        for (int root = 0; root < count; root++)
        {
            if (index[root] >= 0)
            {
                continue;
            }

            calls.Push((root, edgeStart[root]));
            index[root] = low[root] = next++;
            stack.Push(root);
            onStack[root] = true;
            while (calls.TryPop(out (int Node, int Edge) frame))
            {
                int v = frame.Node;
                int e = frame.Edge;
                bool descended = false;
                while (e < edgeStart[v + 1])
                {
                    int w = edges[e++];
                    if (index[w] < 0)
                    {
                        calls.Push((v, e));
                        calls.Push((w, edgeStart[w]));
                        index[w] = low[w] = next++;
                        stack.Push(w);
                        onStack[w] = true;
                        descended = true;
                        break;
                    }

                    if (onStack[w])
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }
                }

                if (descended)
                {
                    continue;
                }

                if (low[v] == index[v])
                {
                    int w;
                    do
                    {
                        w = stack.Pop();
                        onStack[w] = false;
                        component[w] = components;
                    }
                    while (w != v);
                    components++;
                }

                if (calls.TryPeek(out (int Node, int Edge) parent))
                {
                    low[parent.Node] = Math.Min(low[parent.Node], low[v]);
                }
            }
        }

        return component;
    }

    /// <summary>Adds to <paramref name="found"/> the formulas whose ranges hold <paramref name="cell"/>, each once.</summary>
    private void Readers(CellKey cell, List<Node> found)
    {
        int start = found.Count;
        if (cellReaders.TryGetValue(cell.Packed, out List<Node>? direct))
        {
            found.AddRange(direct);
        }

        CellAddress address = new(cell.Row, cell.Column);
        if (tileReaders.TryGetValue(TileKey(cell.Sheet, cell.Row >> TileShift, cell.Column >> TileShift), out List<Node>? tiled))
        {
            foreach (Node node in tiled)
            {
                if (node.Reads(cell.Sheet, address))
                {
                    found.Add(node);
                }
            }
        }

        if (wideReaders.TryGetValue(cell.Sheet, out List<Node>? wide))
        {
            foreach (Node node in wide)
            {
                if (node.Reads(cell.Sheet, address))
                {
                    found.Add(node);
                }
            }
        }

        // A formula that reads the cell through two of its ranges is listed once.
        if (found.Count - start > 1)
        {
            HashSet<Node> once = [];
            int write = start;
            for (int read = start; read < found.Count; read++)
            {
                if (once.Add(found[read]))
                {
                    found[write++] = found[read];
                }
            }

            found.RemoveRange(write, found.Count - write);
        }
    }

    private void Index(Node node, Precedent precedent, bool add)
    {
        RangeAddress range = precedent.Range;
        if (range.CellCount == 1)
        {
            Update(cellReaders, new CellKey(precedent.Sheet, range.First.Row, range.First.Column).Packed, node, add);
            return;
        }

        int firstRow = range.First.Row >> TileShift, lastRow = range.Last.Row >> TileShift;
        int firstColumn = range.First.Column >> TileShift, lastColumn = range.Last.Column >> TileShift;
        if ((long)(lastRow - firstRow + 1) * (lastColumn - firstColumn + 1) > MaxTiles)
        {
            Update(wideReaders, precedent.Sheet, node, add);
            return;
        }

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                Update(tileReaders, TileKey(precedent.Sheet, row, column), node, add);
            }
        }
    }

    private static void Update<TKey>(Dictionary<TKey, List<Node>> index, TKey key, Node node, bool add)
        where TKey : notnull
    {
        if (add)
        {
            ref List<Node>? list = ref CollectionsMarshal.GetValueRefOrAddDefault(index, key, out _);
            list ??= [];
            if (list.Count == 0 || list[^1] != node)
            {
                list.Add(node);
            }

            return;
        }

        if (index.TryGetValue(key, out List<Node>? readers))
        {
            readers.RemoveAll(n => n == node);
            if (readers.Count == 0)
            {
                index.Remove(key);
            }
        }
    }

    private static long TileKey(int sheet, int row, int column) => ((long)sheet << 32) | ((long)row << 16) | (uint)column;

    /// <summary>A formula cell and the ranges it reads.</summary>
    private sealed class Node(CellKey key, Precedent[] precedents)
    {
        public CellKey Key { get; } = key;

        public Precedent[] Precedents { get; } = precedents;

        public bool Reads(int sheet, CellAddress cell)
        {
            foreach (Precedent precedent in Precedents)
            {
                if (precedent.Sheet == sheet && precedent.Range.Contains(cell))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>A step of a <see cref="CalculationPlan"/>: formulas that read none of each other, or a cycle of formulas that do.</summary>
internal readonly record struct CalculationStep(CellKey[] Cells, bool Cycle);

/// <summary>The steps to calculate stale formulas in; a step reads only what earlier steps calculated.</summary>
internal sealed class CalculationPlan(List<CalculationStep> steps)
{
    public IReadOnlyList<CalculationStep> Steps { get; } = steps;

    /// <summary>How many formulas the plan calculates.</summary>
    public int Count => Steps.Sum(s => s.Cells.Length);
}
