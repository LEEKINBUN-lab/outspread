namespace Outspread.Core;

/// <summary>
/// Undo and redo, held as labelled snapshots of the document text.
/// </summary>
/// <remarks>
/// Snapshots rather than inverse patches, because a step the user took and a step an
/// agent wrote into the file have to sit in the same history, and an agent's edit may
/// touch nodes this process has never seen. Rolling one of those back with a patch
/// would need this program to know what the agent meant.
///
/// A snapshot costs about the size of the board file; the cap is what keeps a long
/// session from holding sixty of them. What is undone is then written back through the
/// same patch path as any other change, so undo still merges with concurrent agent
/// edits instead of dumping an old copy of the whole file.
/// </remarks>
public sealed class History
{
    public const int DefaultCapacity = 60;

    private readonly LinkedList<Entry> _undo = [];

    private readonly LinkedList<Entry> _redo = [];

    public History(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
    }

    public sealed record Entry(string Label, string Json);

    public int Capacity { get; }

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>What Ctrl+Z would go back to, for the menu label.</summary>
    public string? NextUndoLabel => _undo.Last?.Value.Label;

    public string? NextRedoLabel => _redo.Last?.Value.Label;

    /// <summary>
    /// Records the state to return to. An action that changed nothing is not
    /// recorded — otherwise a stray click would eat an undo step.
    /// </summary>
    public bool Record(string label, string beforeJson, string afterJson)
    {
        if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
        {
            return false;
        }

        _undo.AddLast(new Entry(label, beforeJson));
        _redo.Clear();

        while (_undo.Count > Capacity)
        {
            _undo.RemoveFirst();
        }

        return true;
    }

    /// <summary>
    /// Pops the previous state, remembering <paramref name="currentJson"/> so the step
    /// can be redone.
    /// </summary>
    public Entry? TakeUndo(string currentJson)
    {
        if (_undo.Last is not { } node)
        {
            return null;
        }

        _undo.RemoveLast();
        _redo.AddLast(new Entry(node.Value.Label, currentJson));

        return node.Value;
    }

    public Entry? TakeRedo(string currentJson)
    {
        if (_redo.Last is not { } node)
        {
            return null;
        }

        _redo.RemoveLast();
        _undo.AddLast(new Entry(node.Value.Label, currentJson));

        return node.Value;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
