using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AskTheCode.Common;
using AskTheCode.ControlFlowGraphs;
using AskTheCode.ControlFlowGraphs.Overlays;
using AskTheCode.PathExploration.Heap;
using AskTheCode.PathExploration.Heuristics;
using AskTheCode.SmtLibStandard;
using CodeContractsRevival.Runtime;

namespace AskTheCode.PathExploration
{
    public class Explorer
    {
        private ExplorationContext context;
        private StartingNodeInfo startingNode;
        private IEntryPointRecognizer finalNodeRecognizer;
        private ISymbolicHeapFactory heapFactory;
        private Action<ExplorationResult> resultCallback;

        private SmtContextHandler smtContextHandler;

        private FlowGraphsNodeOverlay<List<ExplorationState>> statesOnLocations =
            new FlowGraphsNodeOverlay<List<ExplorationState>>(() => new List<ExplorationState>());

        internal Explorer(
            ExplorationContext explorationContext,
            IContextFactory smtContextFactory,
            StartingNodeInfo startingNode,
            IEntryPointRecognizer finalNodeRecognizer,
            ISymbolicHeapFactory heapFactory,
            Action<ExplorationResult> resultCallback)
        {
            // TODO: Solve this marginal case directly in the ExplorationContext
            Contract.Requires(!finalNodeRecognizer.IsFinalNode(startingNode.Node));

            this.context = explorationContext;
            this.startingNode = startingNode;
            this.finalNodeRecognizer = finalNodeRecognizer;
            this.heapFactory = heapFactory;
            this.resultCallback = resultCallback;

            this.smtContextHandler = new SmtContextHandler(smtContextFactory);

            var rootPath = new Path(
                ImmutableArray<Path>.Empty,
                0,
                this.startingNode.Node,
                ImmutableArray<FlowEdge>.Empty);
            var rootState = new ExplorationState(
                rootPath,
                CallSiteStack.Empty,
                this.smtContextHandler.CreateEmptySolver(rootPath, this.startingNode, heapFactory));
            this.AddState(rootState);
        }

        public static int SolverCallCount { get; internal set; }

        public bool IsUnderapproximated { get; private set; }

        // TODO: Make readonly for the heuristics
        public HashSet<ExplorationState> States { get; private set; } = new HashSet<ExplorationState>();

        public IExplorationHeuristic ExplorationHeuristic { get; internal set; }

        public IMergingHeuristic MergingHeuristic { get; internal set; }

        public ISmtHeuristic SmtHeuristic { get; internal set; }

        // TODO: Divide into submethods to make more readable
        internal async Task<bool> ExploreAsync(CancellationToken cancelToken)
        {
            for (
                var currentState = this.ExplorationHeuristic.PickNextState();
                currentState != null;
                currentState = this.ExplorationHeuristic.PickNextState())
            {
                // TODO: Consider reusing the state instead of discarding
                this.RemoveState(currentState);

                IReadOnlyList<FlowEdge> edges;
                var currentNode = currentState.Path.Node;
                var graphProvider = this.context.FlowGraphProvider;
                if (currentNode is EnterFlowNode)
                {
                    if (currentState.CallSiteStack == CallSiteStack.Empty)
                    {
                        edges = await graphProvider.GetCallEdgesToAsync((EnterFlowNode)currentNode);
                    }
                    else
                    {
                        // The execution is constrained by the call site on the stack
                        var edge = graphProvider.GetCallEdge(
                            currentState.CallSiteStack.CallSite,
                            (EnterFlowNode)currentNode);
                        edges = edge.ToSingular();
                    }
                }
                else if ((currentNode as CallFlowNode)?.Location.CanBeExplored == true
                    && !(currentState.Path.Preceeding.FirstOrDefault()?.Node is EnterFlowNode))
                {
                    // If we can model the call and we haven't returned from that method yet
                    edges = await graphProvider.GetReturnEdgesToAsync((CallFlowNode)currentNode);
                }
                else
                {
                    edges = currentNode.IngoingEdges;
                }

                var toSolve = new List<ExplorationState>();

                int i = 0;
                foreach (bool doBranch in this.ExplorationHeuristic.DoBranch(currentState, edges))
                {
                    var edge = edges[i];
                    if (doBranch && this.IsEdgeAllowed(edge))
                    {
                        var branchedPath = new Path(
                            ImmutableArray.Create(currentState.Path),
                            currentState.Path.Depth + 1,
                            edge.From,
                            ImmutableArray.Create(edge));
                        CallSiteStack callSiteStack = GetNewCallSiteStack(currentState, edge);

                        var branchedState = new ExplorationState(
                            branchedPath,
                            callSiteStack,
                            currentState.SolverHandler);

                        bool wasMerged = false;
                        foreach (var mergeCandidate in this.statesOnLocations[branchedState.Path.Node].ToArray())
                        {
                            if (this.MergingHeuristic.DoMerge(branchedState, mergeCandidate))
                            {
                                SmtSolverHandler solverHandler;
                                if (branchedState.SolverHandler != mergeCandidate.SolverHandler)
                                {
                                    solverHandler = this.SmtHeuristic.SelectMergedSolverHandler(
                                        branchedState,
                                        mergeCandidate);
                                }
                                else
                                {
                                    solverHandler = branchedState.SolverHandler;
                                }

                                mergeCandidate.Merge(branchedState, solverHandler);
                                wasMerged = true;

                                break;
                            }
                        }

                        if (!wasMerged)
                        {
                            this.AddState(branchedState);
                        }

                        if (this.IsFinalState(branchedState) || this.SmtHeuristic.DoSolve(branchedState))
                        {
                            toSolve.Add(branchedState);
                        }
                    }
                    else
                    {
                        this.IsUnderapproximated = true;
                    }

                    i++;
                }

                if (toSolve.Count > 0)
                {
                    int j = 0;
                    foreach (bool doReuse in this.SmtHeuristic.DoReuse(currentState.SolverHandler, toSolve))
                    {
                        if (!doReuse)
                        {
                            toSolve[j].SolverHandler = currentState.SolverHandler.Clone();
                        }

                        j++;
                    }

                    foreach (var branchedState in toSolve)
                    {
                        var resultKind = branchedState.SolverHandler.Solve(branchedState.Path);
                        SolverCallCount++;

                        if (resultKind != ExplorationResultKind.Reachable || this.IsFinalState(branchedState))
                        {
                            this.RemoveState(branchedState);
                            var result = branchedState.SolverHandler.LastResult;
                            this.resultCallback(result);
                        }
                    }
                }

                // Check the cancellation before picking next node
                if (cancelToken.IsCancellationRequested)
                {
                    // It is an expected behaviour with well defined result, there is no need to throw an exception
                    break;
                }
            }

            // If there are any exploration states left, the results are not exhaustive
            return this.States.Count == 0;
        }

        private static CallSiteStack GetNewCallSiteStack(ExplorationState currentState, FlowEdge edge)
        {
            CallSiteStack callSiteStack;
            if (edge.From is ReturnFlowNode)
            {
                Contract.Assert(edge is OuterFlowEdge);
                Contract.Assert(((OuterFlowEdge)edge).Kind == OuterFlowEdgeKind.Return);

                callSiteStack = currentState.CallSiteStack.Push((CallFlowNode)edge.To);
            }
            else if (edge.To is EnterFlowNode)
            {
                Contract.Assert(edge is OuterFlowEdge);
                Contract.Assert(((OuterFlowEdge)edge).Kind == OuterFlowEdgeKind.MethodCall);

                callSiteStack = currentState.CallSiteStack.IsEmpty ?
                    currentState.CallSiteStack : currentState.CallSiteStack.Pop();
            }
            else
            {
                callSiteStack = currentState.CallSiteStack;
            }

            return callSiteStack;
        }

        private bool IsEdgeAllowed(FlowEdge edge)
        {
            var ignoredMethods = this.context.Options.IgnoredMethods;
            if (ignoredMethods.Length > 0 && edge is OuterFlowEdge)
            {
                var extensionGraphId = edge.From.Graph.Id;
                var extensionGraphLocation = this.context.FlowGraphProvider.GetLocation(extensionGraphId);
                return !ignoredMethods.Any(m => extensionGraphLocation.ToString().EndsWith(m));
            }

            return true;
        }

        private bool IsFinalState(ExplorationState branchedState)
        {
            return branchedState.CallSiteStack == CallSiteStack.Empty
                && this.finalNodeRecognizer.IsFinalNode(branchedState.Path.Node);
        }

        private void AddState(ExplorationState state)
        {
            this.States.Add(state);
            this.statesOnLocations[state.Path.Node].Add(state);
        }

        private void RemoveState(ExplorationState state)
        {
            this.States.Remove(state);
            this.statesOnLocations[state.Path.Node].Remove(state);
        }
    }
}
