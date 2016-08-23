﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amp.Logging;

namespace Stateless
{
    /// <summary>
    /// Models behaviour as transitions between a finite set of states.
    /// </summary>
    /// <typeparam name="TState">The type used to represent the states.</typeparam>
    /// <typeparam name="TTrigger">The type used to represent the triggers that cause state transitions.</typeparam>
    public partial class StateMachine<TState, TTrigger>
    {
        readonly IDictionary<TState, StateRepresentation> _stateConfiguration = new Dictionary<TState, StateRepresentation>();
        readonly IDictionary<TTrigger, TriggerWithParameters> _triggerConfiguration = new Dictionary<TTrigger, TriggerWithParameters>();
        readonly Func<TState> _stateAccessor;
        readonly Action<TState> _stateMutator;
        readonly ConcurrentQueue<QueuedTrigger> _concurrentEventQueue = new ConcurrentQueue<QueuedTrigger>();
        readonly ILogger _logger;
        readonly string _stateMachineName;

        long _fireCounter;
        Action<TState, TTrigger> _unhandledTriggerAction;
        event Action<Transition> _onTransitioned;
        CancellationTokenSource _cancellationTokenSource;

        public event EventHandler<TriggerNotValidEventArgs<TTrigger, TState>> TriggerNotValidRaised; 
        public event EventHandler<TriggerExecutedEventArgs<TTrigger, TState>> TriggerExecutedRaised; 

        private class QueuedTrigger
        {
            public TTrigger Trigger { get; set; }
            public object[] Args { get; set; }
            public long FireCounter { get; set; }
        }

        /// <summary>
        /// Construct a state machine with external state storage.
        /// </summary>
        /// <param name="logger">logger</param>
        /// <param name="stateAccessor">A function that will be called to read the current state value.</param>
        /// <param name="stateMutator">An action that will be called to write new state values.</param>
        /// <param name="name">name</param>
        public StateMachine(Func<TState> stateAccessor, Action<TState> stateMutator, ILogger logger = null, string name = null) : this(logger, name)
        {
            _stateAccessor = Enforce.ArgumentNotNull(stateAccessor, "stateAccessor");
            _stateMutator = Enforce.ArgumentNotNull(stateMutator, "stateMutator");
        }

        /// <summary>
        /// Construct a state machine.
        /// </summary>
        /// <param name="logger">logger</param>
        /// <param name="initialState">The initial state.</param>
        /// <param name="name">name</param>
        public StateMachine(TState initialState, ILogger logger = null, string name = null) : this(logger, name)
        {
            var reference = new StateReference { State = initialState };
            _stateAccessor = () => reference.State;
            _stateMutator = s => reference.State = s;
        }

        /// <summary>
        /// Default constuctor
        /// </summary>
        private StateMachine(ILogger logger = null, string name = null)
        {
            _logger = logger;
            _unhandledTriggerAction = DefaultUnhandledTriggerAction;
            _stateMachineName = name ?? string.Empty;
            _fireCounter = 0;
        }

        /// <summary>
        /// Start statemachine asynchroniously
        /// </summary>
        public void Start(TaskScheduler taskScheduler)
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }

            _cancellationTokenSource = new CancellationTokenSource();

            Task.Factory.StartNew(() =>
            {
                _logger?.Info($"State machine named [{_stateMachineName}] is started");
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    while (_concurrentEventQueue.Count != 0)
                    {
                        QueuedTrigger queuedEvent;
                        if (_concurrentEventQueue.TryDequeue(out queuedEvent))
                        {
                            try
                            {
                                InternalFireOne(queuedEvent.Trigger, queuedEvent.Args);
                                TriggerExecutedRaised?.Invoke(this, new TriggerExecutedEventArgs<TTrigger, TState>(queuedEvent.Trigger, State, queuedEvent.FireCounter));
                            }
                            catch (InvalidTriggerException)
                            {
                                // raise an event to informe
                                _logger?.Info($"Trigger [{queuedEvent.Trigger}] is not valid in current state [{State}]");
                                TriggerNotValidRaised?.Invoke(this, new TriggerNotValidEventArgs<TTrigger, TState>(queuedEvent.Trigger, State, queuedEvent.FireCounter));
                            }
                            catch(Exception ex)
                            {
                                _logger?.Error(ex, "An unexpected error occurs");
                                _cancellationTokenSource.Cancel();
                                throw;
                            }
                        }
                    }

                    // used to not be 100% CPU time consumer
                    Task.Delay(50).RunSynchronously();
                }
            }, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, taskScheduler);

        }

        /// <summary>
        /// Stop statemachine asynchroniously
        /// </summary>
        public void Stop()
        {
            _cancellationTokenSource.Cancel();
            _logger?.Info($"State machine named [{_stateMachineName}] is stopped");
        }

        /// <summary>
        /// The current state.
        /// </summary>
        public TState State
        {
            get
            {
                return _stateAccessor();
            }
            private set
            {
                _stateMutator(value);
            }
        }

        /// <summary>
        /// The currently-permissible trigger values.
        /// </summary>
        public IEnumerable<TTrigger> PermittedTriggers
        {
            get
            {
                return CurrentRepresentation.PermittedTriggers;
            }
        }

        StateRepresentation CurrentRepresentation
        {
            get
            {
                return GetRepresentation(State);
            }
        }

        StateRepresentation GetRepresentation(TState state)
        {
            StateRepresentation result;

            if (!_stateConfiguration.TryGetValue(state, out result))
            {
                result = new StateRepresentation(state, _logger);
                _stateConfiguration.Add(state, result);
            }

            return result;
        }

        /// <summary>
        /// Begin configuration of the entry/exit actions and allowed transitions
        /// when the state machine is in a particular state.
        /// </summary>
        /// <param name="state">The state to configure.</param>
        /// <returns>A configuration object through which the state can be configured.</returns>
        public StateConfiguration Configure(TState state)
        {
            return new StateConfiguration(this, GetRepresentation(state), GetRepresentation);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <param name="trigger">The trigger to fire.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public long Fire(TTrigger trigger)
        {
            return InternalFire(trigger, null);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <param name="trigger">The trigger to fire.</param>
        /// <param name="arg0">The first argument.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public long Fire<TArg0>(TTrigger trigger, TArg0 arg0)
        {
            return InternalFire(trigger, arg0);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <param name="trigger">The trigger to fire.</param>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public long Fire<TArg0, TArg1>(TTrigger trigger, TArg0 arg0, TArg1 arg1)
        {
            return InternalFire(trigger, arg0, arg1);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <param name="trigger">The trigger to fire.</param>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="arg2">The third argument.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public long Fire<TArg0, TArg1, TArg2>(TTrigger trigger, TArg0 arg0, TArg1 arg1, TArg2 arg2)
        {
            return InternalFire(trigger, arg0, arg1, arg2);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <param name="trigger">The trigger to fire.</param>
        /// <param name="arg0">The first argument.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public long Fire<TArg0>(TriggerWithParameters<TArg0> trigger, TArg0 arg0)
        {
            Enforce.ArgumentNotNull(trigger, "trigger");
            return InternalFire(trigger.Trigger, arg0);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="trigger">The trigger to fire.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public long Fire<TArg0, TArg1>(TriggerWithParameters<TArg0, TArg1> trigger, TArg0 arg0, TArg1 arg1)
        {
            Enforce.ArgumentNotNull(trigger, "trigger");
            return InternalFire(trigger.Trigger, arg0, arg1);
        }

        /// <summary>
        /// Transition from the current state via the specified trigger.
        /// The target state is determined by the configuration of the current state.
        /// Actions associated with leaving the current state and entering the new one
        /// will be invoked.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
        /// <param name="arg0">The first argument.</param>
        /// <param name="arg1">The second argument.</param>
        /// <param name="arg2">The third argument.</param>
        /// <param name="trigger">The trigger to fire.</param>
        /// <exception cref="System.InvalidOperationException">The current state does
        /// not allow the trigger to be fired.</exception>
        public long Fire<TArg0, TArg1, TArg2>(TriggerWithParameters<TArg0, TArg1, TArg2> trigger, TArg0 arg0, TArg1 arg1, TArg2 arg2)
        {
            Enforce.ArgumentNotNull(trigger, "trigger");
            return InternalFire(trigger.Trigger, arg0, arg1, arg2);
        }

        /// <summary>
        /// Queue events and then fire in order.
        /// If only one event is queued, this behaves identically to the non-queued version.
        /// </summary>
        /// <param name="trigger">  The trigger. </param>
        /// <param name="args">     A variable-length parameters list containing arguments. </param>
        long InternalFire(TTrigger trigger, params object[] args)
        {
            _fireCounter++;
            _concurrentEventQueue.Enqueue(new QueuedTrigger{Trigger = trigger, Args = args, FireCounter = _fireCounter});
            return _fireCounter;
        }

        void InternalFireOne(TTrigger trigger, params object[] args)
        {
            TriggerWithParameters configuration;
            if (_triggerConfiguration.TryGetValue(trigger, out configuration))
                configuration.ValidateParameters(args);

            var source = State;
            var representativeState = GetRepresentation(source);

            TriggerBehaviour triggerBehaviour;
            if (!representativeState.TryFindHandler(trigger, out triggerBehaviour))
            {
                _unhandledTriggerAction(representativeState.UnderlyingState, trigger);
                return;
            }

            TState destination;
            if (triggerBehaviour.ResultsInTransitionFrom(source, args, out destination))
            {
                var transition = new Transition(source, destination, trigger);

                representativeState.Exit(transition);

                State = transition.Destination;
                var newRepresentation = GetRepresentation(transition.Destination);
                var onTransitioned = _onTransitioned;
                if (onTransitioned != null)
                    onTransitioned(transition);

                newRepresentation.Enter(transition, args);
            }
        }

        /// <summary>
        /// Override the default behaviour of throwing an exception when an unhandled trigger
        /// is fired.
        /// </summary>
        /// <param name="unhandledTriggerAction">An action to call when an unhandled trigger is fired.</param>
        public void OnUnhandledTrigger(Action<TState, TTrigger> unhandledTriggerAction)
        {
            if (unhandledTriggerAction == null) throw new ArgumentNullException("unhandledTriggerAction");
            _unhandledTriggerAction = unhandledTriggerAction;
        }

        /// <summary>
        /// Determine if the state machine is in the supplied state.
        /// </summary>
        /// <param name="state">The state to test for.</param>
        /// <returns>True if the current state is equal to, or a substate of,
        /// the supplied state.</returns>
        public bool IsInState(TState state)
        {
            return CurrentRepresentation.IsIncludedIn(state);
        }

        /// <summary>
        /// Returns true if <paramref name="trigger"/> can be fired
        /// in the current state.
        /// </summary>
        /// <param name="trigger">Trigger to test.</param>
        /// <returns>True if the trigger can be fired, false otherwise.</returns>
        public bool CanFire(TTrigger trigger)
        {
            return CurrentRepresentation.CanHandle(trigger);
        }

        /// <summary>
        /// A human-readable representation of the state machine.
        /// </summary>
        /// <returns>A description of the current state and permitted triggers.</returns>
        public override string ToString()
        {
            return string.Format(
                "StateMachine {{ State = {0}, PermittedTriggers = {{ {1} }}}}",
                State,
                string.Join(", ", PermittedTriggers.Select(t => t.ToString()).ToArray()));
        }

        /// <summary>
        /// Specify the arguments that must be supplied when a specific trigger is fired.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <param name="trigger">The underlying trigger value.</param>
        /// <returns>An object that can be passed to the Fire() method in order to
        /// fire the parameterised trigger.</returns>
        public TriggerWithParameters<TArg0> SetTriggerParameters<TArg0>(TTrigger trigger)
        {
            var configuration = new TriggerWithParameters<TArg0>(trigger);
            SaveTriggerConfiguration(configuration);
            return configuration;
        }

        /// <summary>
        /// Specify the arguments that must be supplied when a specific trigger is fired.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <param name="trigger">The underlying trigger value.</param>
        /// <returns>An object that can be passed to the Fire() method in order to
        /// fire the parameterised trigger.</returns>
        public TriggerWithParameters<TArg0, TArg1> SetTriggerParameters<TArg0, TArg1>(TTrigger trigger)
        {
            var configuration = new TriggerWithParameters<TArg0, TArg1>(trigger);
            SaveTriggerConfiguration(configuration);
            return configuration;
        }

        /// <summary>
        /// Specify the arguments that must be supplied when a specific trigger is fired.
        /// </summary>
        /// <typeparam name="TArg0">Type of the first trigger argument.</typeparam>
        /// <typeparam name="TArg1">Type of the second trigger argument.</typeparam>
        /// <typeparam name="TArg2">Type of the third trigger argument.</typeparam>
        /// <param name="trigger">The underlying trigger value.</param>
        /// <returns>An object that can be passed to the Fire() method in order to
        /// fire the parameterised trigger.</returns>
        public TriggerWithParameters<TArg0, TArg1, TArg2> SetTriggerParameters<TArg0, TArg1, TArg2>(TTrigger trigger)
        {
            var configuration = new TriggerWithParameters<TArg0, TArg1, TArg2>(trigger);
            SaveTriggerConfiguration(configuration);
            return configuration;
        }

        void SaveTriggerConfiguration(TriggerWithParameters trigger)
        {
            if (_triggerConfiguration.ContainsKey(trigger.Trigger))
                throw new InvalidOperationException(
                    string.Format(StateMachineResources.CannotReconfigureParameters, trigger));

            _triggerConfiguration.Add(trigger.Trigger, trigger);
        }

        void DefaultUnhandledTriggerAction(TState state, TTrigger trigger)
        {
            var source = state;
            var representativeState = GetRepresentation(source);

            TriggerBehaviour triggerBehaviour;
            if (representativeState.TryFindHandlerWithUnmetGuardCondition(trigger, out triggerBehaviour))
            {
                throw new InvalidTriggerException(
                    string.Format(
                        StateMachineResources.NoTransitionsUnmetGuardCondition,
                        trigger, state, triggerBehaviour.GuardDescription));
            }

            throw new InvalidTriggerException(
                string.Format(
                    StateMachineResources.NoTransitionsPermitted,
                    trigger, state));
        }

        /// <summary>
        /// Registers a callback that will be invoked every time the statemachine
        /// transitions from one state into another.
        /// </summary>
        /// <param name="onTransitionAction">The action to execute, accepting the details
        /// of the transition.</param>
        public void OnTransitioned(Action<Transition> onTransitionAction)
        {
            if (onTransitionAction == null) throw new ArgumentNullException("onTransitionAction");
            _onTransitioned += onTransitionAction;
        }
    }
}
