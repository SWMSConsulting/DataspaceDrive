namespace DataspaceDrive.Core.Common;

/// <summary>Erlaubte Zustandsübergänge einer Statusmaschine.</summary>
public sealed class Transitions<TState> where TState : struct, Enum
{
    private readonly Dictionary<TState, HashSet<TState>> _allowed = [];

    public Transitions<TState> Allow(TState from, params TState[] to)
    {
        if (!_allowed.TryGetValue(from, out var targets))
            _allowed[from] = targets = [];
        targets.UnionWith(to);
        return this;
    }

    public bool CanMove(TState from, TState to) => _allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public TState Move(TState from, TState to) =>
        CanMove(from, to) ? to : throw new InvalidStateTransitionException(typeof(TState).Name, from.ToString(), to.ToString());
}

public sealed class InvalidStateTransitionException(string machine, string from, string to)
    : InvalidOperationException($"{machine}: Übergang {from} → {to} ist nicht erlaubt.");
