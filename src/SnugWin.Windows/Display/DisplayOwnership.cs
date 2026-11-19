using SnugWin.Core.Persistence;

namespace SnugWin.Windows.Display;

internal sealed class DisplayOwnership
{
    // Old files contain only DisplayIds, which were inferred from GDI names and
    // may belong to the user. Only persist/consume per-arrival receipts.
    private sealed record Receipts
    {
        public CherryMonitor[] Monitors { get; init; } = [];
    }

    private readonly JsonFileStore<Receipts>? _store;
    private readonly HashSet<CherryMonitor> _owned;

    public DisplayOwnership(string? path)
    {
        if (path is not null) _store = new(path);
        _owned = (_store?.LoadOrQuarantine()?.Monitors ?? []).ToHashSet();
    }

    public IReadOnlyList<CherryMonitor> All() => _owned.ToArray();

    public void Record(CherryMonitor target)
    {
        if (_owned.Add(target)) Save();
    }

    public void Release(CherryMonitor target)
    {
        if (_owned.Remove(target)) Save();
    }

    private void Save()
    {
        if (_owned.Count == 0) _store?.Delete();
        else _store?.Save(new Receipts { Monitors = _owned.ToArray() });
    }
}
