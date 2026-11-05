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

    // A cell or tile read by one formula holds the node itself; read by more, a List<Node>.
    private readonly Dictionary<long, object> cellReaders = [];
    private readonly Dictionary<long, object> tileReaders = [];
    private readonly Dictionary<int, List<Node>> wideReaders = [];
    private readonly HashSet<long> changed = [];
    private readonly HashSet<Node> volatiles = [];
    private bool all;
    private int stamp;

    /// <summary>How many formula cells the graph holds.</summary>
    public int Count => nodes.Count;

    /// <summary>Whether <paramref name="cell"/> holds a formula the graph knows.</summary>
    public bool Contains(CellKey cell) => nodes.ContainsKey(cell.Packed);

    /// <summary>The ranges the formula at <paramref name="cell"/> reads, or an empty span for a cell that has none.</summary>
    public ReadOnlySpan<Precedent> PrecedentsOf(CellKey cell) => nodes.TryGetValue(cell.Packed, out Node? node) ? node.Precedents : [];

    /// <summary>
    /// Records the formula at <paramref name="cell"/>, replacing what it read before, and notes it as changed. A volatile
    /// formula (one calling NOW, RAND or INDIRECT, say) is calculated in every plan. An array formula gives values to the
    /// cells of <paramref name="output"/> as well as its own, so the formulas reading those cells come after it.
    /// </summary>
    public void Set(CellKey cell, ReadOnlySpan<Precedent> precedents, bool isVolatile, RangeAddress? output = null)
    {
        Remove(cell);
        Node node = new(cell, precedents.ToArray(), output);
        nodes.Add(cell.Packed, node);
        foreach (Precedent precedent in node.Precedents)
        {
            Index(node, precedent, add: true);
        }

        if (isVolatile)
        {
            volatiles.Add(node);
        }
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

    /// <summary>Forgets the changes noted so far: the formulas' results are up to date. Volatile formulas stay stale.</summary>
    public void ForgetChanges()
    {
        changed.Clear();
        all = false;
    }

    /// <summary>Whether anything was noted since the last plan.</summary>
    public bool HasChanges => all || changed.Count > 0 || volatiles.Count > 0;

    /// <summary>The formula cells that read <paramref name="cell"/>, or the range of the array formula it holds.</summary>
    public IEnumerable<CellKey> Dependents(CellKey cell)
    {
        List<Node> found = [];
        Readers(cell, found);
        if (nodes.TryGetValue(cell.Packed, out Node? node) && node.Output is { } output)
        {
            OutputReaders(cell, output, found);
            HashSet<Node> once = [];
            found.RemoveAll(n => !once.Add(n));
        }

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
        Walk walk = Stale();
        changed.Clear();
        all = false;
        return Order(walk);
    }

    /// <summary>
    /// The stale formulas, numbered in the order found, and for each the stale formulas that read it: breadth first from the
    /// changed cells and the volatile formulas, where each stale formula's own cell counts as changed.
    /// </summary>
    private Walk Stale()
    {
        int current = ++stamp;
        Walk walk = new();
        List<Node> found = [];

        // Formulas reached from a changed cell that holds no formula: stale, with no edge to record.
        void Seed(long packed)
        {
            if (nodes.TryGetValue(packed, out Node? node))
            {
                walk.Add(node, current);
                return;
            }

            found.Clear();
            Readers(CellKey.Unpack(packed), found);
            foreach (Node reader in found)
            {
                walk.Add(reader, current);
            }
        }

        if (all)
        {
            foreach (Node node in nodes.Values)
            {
                walk.Add(node, current);
            }
        }
        else
        {
            foreach (long packed in changed)
            {
                Seed(packed);
            }

            foreach (Node node in volatiles)
            {
                walk.Add(node, current);
            }
        }

        // Each stale formula once, in the order numbered: its readers become stale and are its edges.
        for (int i = 0; i < walk.Nodes.Count; i++)
        {
            found.Clear();
            Node source = walk.Nodes[i];
            Readers(source.Key, found);
            if (source.Output is { } output)
            {
                OutputReaders(source.Key, output, found);
            }

            walk.EdgeStart.Add(walk.Edges.Count);
            foreach (Node reader in found)
            {
                walk.Edges.Add(walk.Add(reader, current));
            }

            walk.EdgeEnd.Add(walk.Edges.Count);
        }

        return walk;
    }

    /// <summary>
    /// Orders the stale formulas: Tarjan's strongly connected components over the formulas that read each other (iterative,
    /// so a chain of a million cells does not overflow the stack), then each component's step is one more than the latest
    /// step of a component it reads.
    /// </summary>
    private static CalculationPlan Order(Walk walk)
    {
        int count = walk.Nodes.Count;
        if (count == 0)
        {
            return new CalculationPlan([]);
        }

        ReadOnlySpan<int> edges = CollectionsMarshal.AsSpan(walk.Edges);
        ReadOnlySpan<int> edgeStart = CollectionsMarshal.AsSpan(walk.EdgeStart);
        ReadOnlySpan<int> edgeEnd = CollectionsMarshal.AsSpan(walk.EdgeEnd);
        int[] component = Components(count, edgeStart, edgeEnd, edges, out int components);

        // The nodes grouped by component, by counting.
        int[] first = new int[components + 1];
        foreach (int c in component)
        {
            first[c + 1]++;
        }

        for (int c = 0; c < components; c++)
        {
            first[c + 1] += first[c];
        }

        int[] members = new int[count];
        int[] fill = first[..components];
        for (int i = 0; i < count; i++)
        {
            members[fill[component[i]]++] = i;
        }

        // Tarjan numbers components in reverse topological order: a component is numbered after every component it
        // reaches, so readers come first. Steps go from the highest number down.
        int[] level = new int[components];
        int maxLevel = 0;
        for (int c = components - 1; c >= 0; c--)
        {
            for (int m = first[c]; m < first[c + 1]; m++)
            {
                int i = members[m];
                for (int e = edgeStart[i]; e < edgeEnd[i]; e++)
                {
                    int target = component[edges[e]];
                    if (target != c && level[target] <= level[c])
                    {
                        level[target] = level[c] + 1;
                    }
                }
            }

            maxLevel = Math.Max(maxLevel, level[c]);
        }

        // Within a level, plain formulas form one parallel step; each cycle is a step of its own after them.
        int[] plainCount = new int[maxLevel + 1];
        bool[] cycle = new bool[components];
        for (int c = 0; c < components; c++)
        {
            int size = first[c + 1] - first[c];
            int i = members[first[c]];
            cycle[c] = size > 1 || edges[edgeStart[i]..edgeEnd[i]].Contains(i);
            if (!cycle[c])
            {
                plainCount[level[c]]++;
            }
        }

        CellKey[][] plain = new CellKey[maxLevel + 1][];
        int[] plainFill = new int[maxLevel + 1];
        List<(int Level, CellKey[] Cells)> rings = [];
        for (int c = components - 1; c >= 0; c--)
        {
            int l = level[c];
            if (cycle[c])
            {
                CellKey[] ring = new CellKey[first[c + 1] - first[c]];
                for (int m = first[c]; m < first[c + 1]; m++)
                {
                    ring[m - first[c]] = walk.Nodes[members[m]].Key;
                }

                rings.Add((l, ring));
            }
            else
            {
                plain[l] ??= new CellKey[plainCount[l]];
                plain[l][plainFill[l]++] = walk.Nodes[members[first[c]]].Key;
            }
        }

        rings.Sort((a, b) => a.Level.CompareTo(b.Level));
        List<CalculationStep> steps = [];
        int r = 0;
        for (int l = 0; l <= maxLevel; l++)
        {
            if (plain[l] is { } cells)
            {
                steps.Add(new CalculationStep(cells, Cycle: false));
            }

            for (; r < rings.Count && rings[r].Level == l; r++)
            {
                steps.Add(new CalculationStep(rings[r].Cells, Cycle: true));
            }
        }

        return new CalculationPlan(steps);
    }

    /// <summary>Tarjan's algorithm without recursion: each node's component number, in reverse topological order.</summary>
    private static int[] Components(int count, ReadOnlySpan<int> edgeStart, ReadOnlySpan<int> edgeEnd, ReadOnlySpan<int> edges, out int components)
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
                while (e < edgeEnd[v])
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
        if (cellReaders.TryGetValue(cell.Packed, out object? direct))
        {
            Append(direct, found);
        }

        CellAddress address = new(cell.Row, cell.Column);
        if (tileReaders.TryGetValue(TileKey(cell.Sheet, cell.Row >> TileShift, cell.Column >> TileShift), out object? tiled))
        {
            if (tiled is Node one)
            {
                if (one.Reads(cell.Sheet, address))
                {
                    found.Add(one);
                }
            }
            else
            {
                foreach (Node node in (List<Node>)tiled)
                {
                    if (node.Reads(cell.Sheet, address))
                    {
                        found.Add(node);
                    }
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

    /// <summary>Adds the formulas reading any cell of an array formula's range but its own, each once.</summary>
    private void OutputReaders(CellKey anchor, RangeAddress output, List<Node> found)
    {
        int start = found.Count;
        for (int row = output.First.Row; row <= output.Last.Row; row++)
        {
            for (int column = output.First.Column; column <= output.Last.Column; column++)
            {
                if (row != anchor.Row || column != anchor.Column)
                {
                    Readers(new CellKey(anchor.Sheet, row, column), found);
                }
            }
        }

        if (found.Count - start > 1)
        {
            HashSet<Node> once = [];
            found.RemoveAll(n => !once.Add(n));
        }
    }

    private static void Append(object readers, List<Node> found)
    {
        if (readers is Node one)
        {
            found.Add(one);
        }
        else
        {
            found.AddRange((List<Node>)readers);
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
            ref List<Node>? wide = ref CollectionsMarshal.GetValueRefOrAddDefault(wideReaders, precedent.Sheet, out _);
            wide ??= [];
            if (add)
            {
                if (wide.Count == 0 || wide[^1] != node)
                {
                    wide.Add(node);
                }
            }
            else
            {
                wide.RemoveAll(n => n == node);
            }

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

    /// <summary>Adds a reader to an index entry, or removes it; the entry is the node alone until a second one comes.</summary>
    private static void Update(Dictionary<long, object> index, long key, Node node, bool add)
    {
        if (add)
        {
            ref object? entry = ref CollectionsMarshal.GetValueRefOrAddDefault(index, key, out bool exists);
            if (!exists || entry is null)
            {
                entry = node;
            }
            else if (entry is Node one)
            {
                if (one != node)
                {
                    entry = new List<Node>(2) { one, node };
                }
            }
            else
            {
                List<Node> list = (List<Node>)entry;
                if (list[^1] != node)
                {
                    list.Add(node);
                }
            }

            return;
        }

        if (!index.TryGetValue(key, out object? current))
        {
            return;
        }

        if (current is Node single)
        {
            if (single == node)
            {
                index.Remove(key);
            }

            return;
        }

        List<Node> readers = (List<Node>)current;
        readers.RemoveAll(n => n == node);
        if (readers.Count == 0)
        {
            index.Remove(key);
        }
        else if (readers.Count == 1)
        {
            index[key] = readers[0];
        }
    }

    private static long TileKey(int sheet, int row, int column) => ((long)sheet << 32) | ((long)row << 16) | (uint)column;

    /// <summary>The stale formulas of a plan being made, numbered, and the edges from each to the formulas that read it.</summary>
    private sealed class Walk
    {
        public List<Node> Nodes { get; } = [];

        public List<int> Edges { get; } = [];

        public List<int> EdgeStart { get; } = [];

        public List<int> EdgeEnd { get; } = [];

        /// <summary>The node's number in this walk, numbering it if it has none yet.</summary>
        public int Add(Node node, int stamp)
        {
            if (node.Stamp != stamp)
            {
                node.Stamp = stamp;
                node.Number = Nodes.Count;
                Nodes.Add(node);
            }

            return node.Number;
        }
    }

    /// <summary>A formula cell and the ranges it reads.</summary>
    private sealed class Node(CellKey key, Precedent[] precedents, RangeAddress? output)
    {
        public CellKey Key { get; } = key;

        public Precedent[] Precedents { get; } = precedents;

        /// <summary>The range an array formula fills, its own cell included; null for an ordinary formula.</summary>
        public RangeAddress? Output { get; } = output;

        /// <summary>The plan this node was last numbered in, and its number there.</summary>
        public int Stamp { get; set; }

        public int Number { get; set; }

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
