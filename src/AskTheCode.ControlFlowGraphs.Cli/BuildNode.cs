﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Text;
using System.Threading.Tasks;
using AskTheCode.Common;
using AskTheCode.ControlFlowGraphs.Cli.TypeModels;
using AskTheCode.SmtLibStandard;
using Microsoft.CodeAnalysis;

namespace AskTheCode.ControlFlowGraphs.Cli
{
    internal class BuildNode
    {
        private List<BuildEdge> outgoingEdges = new List<BuildEdge>();
        private FlowNode flowNode;
        private ITypeModel variableModel;
        private ITypeModel valueModel;

        public BuildNode(SyntaxNodeOrToken syntax)
        {
            this.Syntax = syntax;
        }

        public List<BuildEdge> OutgoingEdges
        {
            get { return this.outgoingEdges; }
        }

        // TODO: Optimize the type if necessary (make 2 fields?)
        public SyntaxNodeOrToken Syntax { get; set; }

        public FlowNode FlowNode
        {
            get { return this.flowNode; }
            set { DataHelper.SetOnceAssert(ref this.flowNode, value); }
        }

        public ITypeModel VariableModel
        {
            get { return this.variableModel; }
            set { DataHelper.SetOnceAssert(ref this.variableModel, value); }
        }

        public ITypeModel ValueModel
        {
            get { return this.valueModel; }
            set { DataHelper.SetOnceAssert(ref this.valueModel, value); }
        }

        public Task PendingTask { get; set; }

        public BuildEdge AddEdge(BuildNode to, Expression valueCondition = null)
        {
            var edge = new BuildEdge(to, valueCondition);
            this.OutgoingEdges.Add(edge);

            return edge;
        }

        public void AddEdge(BuildEdge edge)
        {
            this.OutgoingEdges.Add(edge);
        }

        public void SwapVariableModel(BuildNode other)
        {
            DataHelper.Swap(ref this.variableModel, ref other.variableModel);
        }

        public void SwapEdges(BuildNode other)
        {
            DataHelper.Swap(ref this.outgoingEdges, ref other.outgoingEdges);
        }

        // TODO: Add proper hashing
    }
}