﻿using System.Collections.Immutable;
using AskTheCode.PathExploration.Heap;
using AskTheCode.PathExploration.Heuristics;

namespace AskTheCode.PathExploration
{
    public class ExplorationOptions
    {
        public static ExplorationOptions Default
        {
            get { return new ExplorationOptions(); }
        }

        public IEntryPointRecognizer FinalNodeRecognizer { get; set; } = new BorderEntryPointRecognizer();

        public ISymbolicHeapFactory SymbolicHeapFactory { get; set; } = new ArrayTheorySymbolicHeapFactory();

        public IHeuristicFactory<IExplorationHeuristic> ExplorationHeuristicFactory { get; set; } =
            new LimitedExplorationHeuristicFactory(10, 10);

        public IHeuristicFactory<IMergingHeuristic> MergingHeuristicFactory { get; set; } =
            new SimpleHeuristicFactory<NeverMergeHeuristic>();

        public IHeuristicFactory<ISmtHeuristic> SmtHeuristicFactory { get; set; } =
            new SimpleHeuristicFactory<MultipleIngoingSmtHeuristic>();

        public int? TimeoutSeconds { get; set; } = 30;

        public ImmutableArray<string> IgnoredMethods { get; set; } = ImmutableArray<string>.Empty;
    }
}