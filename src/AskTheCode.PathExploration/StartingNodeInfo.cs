﻿using System;
using System.Collections.Generic;
using System.Text;
using AskTheCode.ControlFlowGraphs;
using AskTheCode.ControlFlowGraphs.Operations;
using CodeContractsRevival.Runtime;

namespace AskTheCode.PathExploration
{
    public class StartingNodeInfo
    {
        public StartingNodeInfo(FlowNode node, int? assignmentIndex, bool isAssertionChecked)
        {
            Contract.Requires(node != null);

            this.Node = node;
            this.AssignmentIndex = (assignmentIndex >= 0) ? assignmentIndex : null;
            this.IsAssertionChecked = isAssertionChecked;
        }

        public FlowNode Node { get; private set; }

        public int? AssignmentIndex { get; private set; }

        public bool IsAssertionChecked { get; private set; }

        public Operation Operation
        {
            get
            {
                if (this.AssignmentIndex == null)
                {
                    return null;
                }

                var innerNode = this.Node as InnerFlowNode;
                if (innerNode == null)
                {
                    return null;
                }

                return innerNode.Operations[this.AssignmentIndex.Value];
            }
        }
    }
}