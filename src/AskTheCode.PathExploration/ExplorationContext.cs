using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AskTheCode.ControlFlowGraphs;
using AskTheCode.PathExploration.Heuristics;
using AskTheCode.SmtLibStandard;

namespace AskTheCode.PathExploration
{
    public class ExplorationContext
    {
        // TODO: Consider enforcing it to be read-only
        private Subject<ExecutionModel> executionModels = new Subject<ExecutionModel>();

        public ExplorationContext(
            IFlowGraphProvider flowGraphProvider,
            IContextFactory smtContextFactory,
            StartingNodeInfo startingNode,
            ExplorationOptions options)
        {
            this.FlowGraphProvider = flowGraphProvider;
            this.SmtContextFactory = smtContextFactory;
            this.StartingNode = startingNode;
            this.Options = options;
            this.Options.FinalNodeRecognizer.FlowGraphProvider = flowGraphProvider;
        }

        public ExplorationContext(
            IFlowGraphProvider flowGraphProvider,
            IContextFactory smtContextFactory,
            StartingNodeInfo startingNode)
            : this(flowGraphProvider, smtContextFactory, startingNode, ExplorationOptions.Default)
        {
        }

        public IObservable<ExecutionModel> ExecutionModels => this.executionModels;

        internal IFlowGraphProvider FlowGraphProvider { get; private set; }

        internal IContextFactory SmtContextFactory { get; private set; }

        internal IHeuristicFactory<IExplorationHeuristic> ExplorationHeuristicFactory { get; private set; }

        internal IHeuristicFactory<IMergingHeuristic> MergingHeuristicFactory { get; private set; }

        internal StartingNodeInfo StartingNode { get; private set; }

        internal ExplorationOptions Options { get; private set; }

        public void Explore()
        {
            var explorer = this.CreateExplorer();
            explorer.Explore(default(CancellationToken));
        }

        public async Task ExploreAsync()
        {
            // TODO: Implement exploration partitioning to multiple explorers
            var explorer = this.CreateExplorer();

            var cancelSource = new CancellationTokenSource();

            var explorationTask = new Task(() => explorer.Explore(cancelSource.Token));
            explorationTask.Start();

            cancelSource.CancelAfter(this.Options.TimeoutSeconds * 1000);

            await explorationTask;
        }

        private Explorer CreateExplorer()
        {
            var explorer = new Explorer(
                this,
                this.SmtContextFactory,
                this.StartingNode,
                this.Options.FinalNodeRecognizer,
                this.ExplorerResultCallback);

            explorer.ExplorationHeuristic = this.Options.ExplorationHeuristicFactory.CreateHeuristic(explorer);
            explorer.MergingHeuristic = this.Options.MergingHeuristicFactory.CreateHeuristic(explorer);
            explorer.SmtHeuristic = this.Options.SmtHeuristicFactory.CreateHeuristic(explorer);

            return explorer;
        }

        private void ExplorerResultCallback(ExplorationResult result)
        {
            // TODO: Implement locking or some intelligent sequencing when multiple explorers are implemented
            if (result?.ExecutionModel != null)
            {
                this.executionModels.OnNext(result.ExecutionModel);
            }
            else if (result?.PathCounterExample != null)
            {
                // TODO
            }
        }
    }
}
