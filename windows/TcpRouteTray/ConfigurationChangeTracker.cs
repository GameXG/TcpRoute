namespace TcpRouteTray;

internal sealed class ConfigurationChangeTracker(string applied)
{
    private string _applied = applied;
    private string? _pending;

    public bool Observe(string snapshot)
    {
        if (snapshot == _applied) { _pending = null; return false; }
        if (snapshot != _pending) { _pending = snapshot; return false; }
        Accept(snapshot);
        return true;
    }

    public void Accept(string snapshot) { _applied = snapshot; _pending = null; }
}
