﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Amp.Logging;

namespace Stateless
{
    public partial class StateMachine<TState, TTrigger>
    {
        internal class StateRepresentation
        {
            readonly TState _state;
            readonly ILogger _logger;

            readonly IDictionary<TTrigger, ICollection<TriggerBehaviour>> _triggerBehaviours = new Dictionary<TTrigger, ICollection<TriggerBehaviour>>();
            internal IDictionary<TTrigger, ICollection<TriggerBehaviour>> TriggerBehaviours { get { return _triggerBehaviours; } }

            EntryActionBehavior _entryAction;
            internal EntryActionBehavior EntryAction { get { return _entryAction; } }
            ExitActionBehavior _exitAction;
            internal ExitActionBehavior ExitAction { get { return _exitAction; } }

            public event EventHandler<TriggerResultEventArgs> TriggerResultRaised;

            StateRepresentation _superstate; // null

            readonly ICollection<StateRepresentation> _substates = new List<StateRepresentation>();

            public StateRepresentation(TState state, ILogger logger = null)
            {
                _state = state;
                _logger = logger;
            }

            public bool CanHandle(TTrigger trigger)
            {
                TriggerBehaviour unused;
                return TryFindHandler(trigger, out unused);
            }

            public bool TryFindHandler(TTrigger trigger, out TriggerBehaviour handler)
            {
                return (TryFindLocalHandler(trigger, out handler, t => t.IsGuardConditionMet) ||
                    (Superstate != null && Superstate.TryFindHandler(trigger, out handler)));
            }
            
            bool TryFindLocalHandler(TTrigger trigger, out TriggerBehaviour handler, params Func<TriggerBehaviour, bool>[] filters)
            {
                ICollection<TriggerBehaviour> possible;
                if (!_triggerBehaviours.TryGetValue(trigger, out possible))
                {
                    handler = null;
                    return false;
                }

                var actual = filters.Aggregate(possible, (current, filter) => current.Where(filter).ToArray());

                if (actual.Count() > 1)
                    throw new InvalidOperationException(
                        string.Format(StateRepresentationResources.MultipleTransitionsPermitted,
                        trigger, _state));

                handler = actual.FirstOrDefault();
                return handler != null;
            }

            public bool TryFindHandlerWithUnmetGuardCondition(TTrigger trigger, out TriggerBehaviour handler)
            {
                return (TryFindLocalHandler(trigger, out handler, t => !t.IsGuardConditionMet) || 
                    (Superstate != null && Superstate.TryFindHandlerWithUnmetGuardCondition(trigger, out handler)));
            }

            public void AddEntryAction(TTrigger trigger, Func<Transition, object[], object> func, string entryActionDescription)
            {
                Enforce.ArgumentNotNull(func, nameof(func));
                _entryAction = new EntryActionBehavior((t, args) =>
                    {
                        if (t.Trigger.Equals(trigger))
                            return func(t, args);

                        return null;
                    },
                    Enforce.ArgumentNotNull(entryActionDescription, nameof(entryActionDescription)));
            }

            public void AddEntryAction(Func<Transition, object[], object> func, string entryActionDescription)
            {
                _entryAction = new EntryActionBehavior(
                        Enforce.ArgumentNotNull(func, nameof(func)),
                        Enforce.ArgumentNotNull(entryActionDescription, nameof(entryActionDescription)));
            }

            public void AddExitAction(Func<Transition, object> func, string exitActionDescription)
            {
                _exitAction = new ExitActionBehavior(
                        Enforce.ArgumentNotNull(func, nameof(func)),
                        Enforce.ArgumentNotNull(exitActionDescription, nameof(exitActionDescription)));
            }

            public object Enter(Transition transition, long fireCounter, params object[] entryArgs)
            {
                object result = null;
                Enforce.ArgumentNotNull(transition, nameof(transition));

                if (transition.IsReentry)
                {
                    result = ExecuteEntryActions(transition, entryArgs);
                }
                else if (!Includes(transition.Source))
                {
                    if (_superstate != null)
                        _superstate.Enter(transition, fireCounter, entryArgs);

                    result = ExecuteEntryActions(transition, entryArgs);
                }

                return result;
            }

            public void Exit(Transition transition)
            {
                Enforce.ArgumentNotNull(transition, nameof(transition));

                if (transition.IsReentry)
                {
                    var result = ExecuteExitActions(transition);
                }
                else if (!Includes(transition.Destination))
                {
                    var result = ExecuteExitActions(transition);
                    if (_superstate != null)
                        _superstate.Exit(transition);
                }
            }

            object ExecuteEntryActions(Transition transition, object[] entryArgs)
            {
                Enforce.ArgumentNotNull(transition, nameof(transition));
                Enforce.ArgumentNotNull(entryArgs, nameof(entryArgs));
                
                if(_entryAction == null)
                {
                    return null;
                }

                if (entryArgs != null)
                {
                    if(entryArgs.Length != 1)
                    {
                        var objectToString = string.Empty;
                        foreach (var obj in entryArgs)
                        {
                            objectToString += "[" + obj.ToString() + "]";
                        }

                        _logger?.Info($"[{_entryAction.ActionDescription}] values [{objectToString}]");
                    }
                    else
                    {
                        _logger?.Info($"[{_entryAction.ActionDescription}] values [{entryArgs[0].ToString()}]");
                    }
                }
                else
                {
                    _logger?.Info($"[{_entryAction.ActionDescription}]");
                }

                return _entryAction.Func(transition, entryArgs);
            }           

            object ExecuteExitActions(Transition transition)
            {
                Enforce.ArgumentNotNull(transition, nameof(transition));

                if (_exitAction == null)
                {
                    return null;
                }

                _logger?.Info($"[{_exitAction.ActionDescription}]");
                return _exitAction.Func(transition);
            }

            public void AddTriggerBehaviour(TriggerBehaviour triggerBehaviour)
            {
                ICollection<TriggerBehaviour> allowed;
                if (!_triggerBehaviours.TryGetValue(triggerBehaviour.Trigger, out allowed))
                {
                    allowed = new List<TriggerBehaviour>();
                    _triggerBehaviours.Add(triggerBehaviour.Trigger, allowed);
                }
                allowed.Add(triggerBehaviour);
            }

            public StateRepresentation Superstate
            {
                get
                {
                    return _superstate;
                }
                set
                {
                    _superstate = value;
                }
            }

            public TState UnderlyingState
            {
                get
                {
                    return _state;
                }
            }

            public void AddSubstate(StateRepresentation substate)
            {
                Enforce.ArgumentNotNull(substate, nameof(substate));
                _substates.Add(substate);
            }

            public bool Includes(TState state)
            {
                return _state.Equals(state) || _substates.Any(s => s.Includes(state));
            }

            public bool IsIncludedIn(TState state)
            {
                return
                    _state.Equals(state) ||
                    (_superstate != null && _superstate.IsIncludedIn(state));
            }

            public IEnumerable<TTrigger> PermittedTriggers
            {
                get
                {
                    var result = _triggerBehaviours
                        .Where(t => t.Value.Any(a => a.IsGuardConditionMet))
                        .Select(t => t.Key);

                    if (Superstate != null)
                        result = result.Union(Superstate.PermittedTriggers);

                    return result.ToArray();
                }
            }
        }
    }
}
